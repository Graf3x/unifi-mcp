using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Whether a write may run, and how the owner approved it. A client that can show a form (one on the 2026-07-28 revision
/// that declares form elicitation) gets one: the whole change with Accept or Decline, and <c>confirm</c> is ignored, so
/// the model cannot approve its own write. A change too big to show whole on the form is refused. Any other client is
/// refused every write, unless the owner set UNIFI_ALLOW_CONFIRM_FALLBACK=true
/// (<see cref="Settings.AllowConfirmFallback"/>): then <c>confirm</c> set to the tool's own name runs it, which means the
/// model approves its own writes on that client.
/// </summary>
/// <remarks>
/// The server is stateless, so the form travels as a multi round-trip request: the first call answers "input required"
/// with the form and a sealed token, and the client calls again with the owner's answer and the token. The token names
/// this tool, the request it will send, these exact arguments (less <c>confirm</c>) and this owner; it lapses after
/// <see cref="Lifetime"/> and is spent by the answer it carries. A call whose token does not open, or is spent, gets a
/// fresh form. Tokens are sealed with a key that lives only as long as the process: they need no more, and the
/// connector's keyring (which may be broken or absent) cannot then take writes down with it. Because a token cannot
/// outlive the process, its lifetime is kept on the process's monotonic clock, so setting the system clock (either way)
/// neither revives a token nor stops new ones from opening.
/// </remarks>
public static class WriteApproval
{
    public abstract record Decision;

    /// <summary>Send it. <paramref name="How"/> goes on the audit line.</summary>
    public sealed record Run(string How) : Decision;

    /// <summary>Send nothing, answer this.</summary>
    public sealed record Stop(CallToolResult Result) : Decision;

    internal const string InputKey = "approve";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    /// <summary>The most the form may hold, in characters. A change that needs more is refused, never shown in part.</summary>
    internal const int MaxForm = 10_000;
    private static readonly IDataProtector Protector = new EphemeralDataProtectionProvider().CreateProtector("UnifiMcp.WriteApproval.v2");
    private static readonly ConditionalWeakTable<TimeProvider, Dictionary<string, long>> Ledgers = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Run, stop with an answer, or ask the owner by throwing <see cref="InputRequiredException"/>. Called once the request
    /// is built and checked, so the owner approves the <paramref name="path"/> that will be sent.
    /// </summary>
    public static Decision Decide(ApiOperation op, string path, IDictionary<string, JsonElement> args, RequestContext<CallToolRequestParams> request)
    {
        var login = Audit.Login(request.User);
        var audit = Audit.Logger(request.Services);
        if (!CanAsk(request.Server))
        {
            // The fallback is the owner's choice alone; without settings to say so, it is off.
            if (request.Services?.GetService<Settings>()?.AllowConfirmFallback != true)
            {
                audit?.Log(Audit.Level(write: true), "{Login} {Tool} refused: the client cannot show a form", login, op.ToolName);
                return new Stop(ToolText.Error(
                    $"Nothing was sent. {op.ToolName} ({op.Method} {path}) changes the live network, and the owner approves each "
                    + "change on a confirmation form. This client cannot show one (that needs MCP revision 2026-07-28 with form "
                    + "elicitation), so the write is refused; confirm does not change that. Do not retry. Tell the owner: they "
                    + "can use a client that shows the form, or set UNIFI_ALLOW_CONFIRM_FALLBACK=true on the server, which lets "
                    + "the model approve its own writes on clients without one."));
            }
            return Confirm(args) == op.ToolName ? new Run("approved by confirm argument") : new Stop(ToolText.Ok(
                $"Nothing was sent. {op.Method} {path} changes the live network.\n"
                + $"Arguments: {JsonSerializer.Serialize(Approved(args))}\n"
                + $"To run it, call {op.ToolName} again with confirm set to exactly \"{op.ToolName}\"."));
        }

        var form = Form(op, path, args);
        if (form.Length > MaxForm)
        {
            audit?.Log(Audit.Level(write: true), "{Login} {Tool} refused: too big for the form", login, op.ToolName);
            return new Stop(ToolText.Error(
                $"Nothing was sent ({op.Method} {path}). The owner must see the whole change on the confirmation form, and "
                + $"this one needs {form.Length.ToString("N0", CultureInfo.InvariantCulture)} characters; the form holds "
                + $"{MaxForm.ToString("N0", CultureInfo.InvariantCulture)}. Make a smaller change: split it into several calls "
                + "where the API allows it, or ask the owner to make this one in the UniFi console."));
        }

        var time = request.Services?.GetService<TimeProvider>() ?? TimeProvider.System;
        if (Answer(request.Params) is { } answer)
        {
            if (Open(Protector, time, request.Params!.RequestState, op.ToolName, path, args, login) is { } token && Spend(token, time))
            {
                if (answer.IsAccepted) return new Run("approved by form");
                var how = string.Equals(answer.Action, "cancel", StringComparison.OrdinalIgnoreCase) ? "cancelled on form" : "declined by form";
                audit?.Log(Audit.Level(write: true), "{Login} {Tool} {How}", login, op.ToolName, how);
                return new Stop(ToolText.Ok($"The owner declined this change. Nothing was sent ({op.Method} {path}). "
                                            + "Do not retry it unless the owner asks you to."));
            }
            audit?.Log(Audit.Level(write: true), "{Login} {Tool} answer did not match its form; asked again", login, op.ToolName);
        }

        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams { Message = form, RequestedSchema = new() }),
            },
            Seal(Protector, time, op.ToolName, path, args, login));
    }

    /// <summary>A form can be shown: a 2026-07-28 request that declares form elicitation (a bare declaration means form).</summary>
    private static bool CanAsk(McpServer server) => server.IsMrtrSupported && server.ClientCapabilities?.Elicitation?.Form is not null;

    /// <summary>The owner's answer, or null for none or one that cannot be read.</summary>
    private static ElicitResult? Answer(CallToolRequestParams? p)
    {
        if (p?.InputResponses?.TryGetValue(InputKey, out var response) != true || response is null) return null;
        try { return response.Deserialize(InputResponse.ElicitResultJsonTypeInfo); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// What the owner sees: the change and the exact request, every argument, then the body, all in full. Nothing is cut:
    /// the model chooses the order of the body's fields, so the one that decides the change could sit past any cut.
    /// <see cref="Decide"/> refuses a form longer than <see cref="MaxForm"/>.
    /// </summary>
    internal static string Form(ApiOperation op, string path, IDictionary<string, JsonElement> args)
    {
        var sb = new StringBuilder();
        sb.Append(op.Summary).Append("\n\nThis changes your live network: ").Append(op.Method).Append(' ').Append(path).Append('\n');
        var named = Approved(args).Where(a => a.Key != "body").ToList();
        if (named.Count > 0)
        {
            sb.Append('\n');
            foreach (var (name, value) in named)
                sb.Append(name).Append(": ").Append(Shown(value)).Append('\n');
        }
        if (args.TryGetValue("body", out var body))
            sb.Append("\nBody:\n").Append(JsonSerializer.Serialize(body, Indented));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A value as JSON escapes it (a string without its quotes), so no value can break a line or carry a character that
    /// reads as one, and the model cannot write lines of its own onto the form.
    /// </summary>
    private static string Shown(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(value.GetString())[1..^1] : JsonSerializer.Serialize(value);

    /// <summary>A token for this tool, request, arguments and owner, good once and for <see cref="Lifetime"/>.</summary>
    internal static string Seal(IDataProtector protector, TimeProvider time, string tool, string path, IDictionary<string, JsonElement> args, string login) =>
        protector.Protect(string.Join('\n', Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), tool, path, Digest(args), login,
            (time.GetTimestamp() + (long)(Lifetime.TotalSeconds * time.TimestampFrequency)).ToString(CultureInfo.InvariantCulture)));

    /// <summary>The token's id when it is ours, unexpired, and names exactly this tool, request, arguments and owner.</summary>
    internal static string? Open(IDataProtector protector, TimeProvider time, string? token, string tool, string path, IDictionary<string, JsonElement> args, string login)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string[] parts;
        try { parts = protector.Unprotect(token).Split('\n'); }
        catch (CryptographicException) { return null; }
        return parts.Length == 6 && parts[1] == tool && parts[2] == path && parts[3] == Digest(args) && parts[4] == login
               && long.TryParse(parts[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expires)
               && time.GetTimestamp() <= expires
            ? parts[0] + "\n" + parts[5]
            : null;
    }

    /// <summary>
    /// The spent token ids of one clock, with their expiry; <see cref="Spend"/> locks it. An id is dropped once its token
    /// has expired, and the clock it is judged by never goes back. Each clock has its own (the host has one), so a fake
    /// clock in one test cannot clean up another's.
    /// </summary>
    internal static object LedgerFor(TimeProvider time) => Ledgers.GetValue(time, _ => new Dictionary<string, long>(StringComparer.Ordinal));

    /// <summary>
    /// Marks an opened token used; false when it already was or has expired. The clock read, the expiry check, the clean-up
    /// and the mark happen under one lock, on the monotonic clock the token was sealed with, so a call that stalls cannot
    /// act on an old time and a spent id is never dropped while its token could still open.
    /// </summary>
    internal static bool Spend(string opened, TimeProvider time)
    {
        var expires = long.Parse(opened[(opened.IndexOf('\n') + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var spent = (Dictionary<string, long>)LedgerFor(time);
        lock (spent)
        {
            var now = time.GetTimestamp();
            if (now > expires) return false;
            foreach (var old in spent.Where(s => s.Value < now).Select(s => s.Key).ToList()) spent.Remove(old);
            return spent.TryAdd(opened, expires);
        }
    }

    /// <summary>The arguments the owner approves: everything but <c>confirm</c>, by name.</summary>
    private static SortedDictionary<string, JsonElement> Approved(IDictionary<string, JsonElement> args) =>
        new(args.Where(a => a.Key != "confirm").ToDictionary(), StringComparer.Ordinal);

    private static string Digest(IDictionary<string, JsonElement> args) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Approved(args)))));

    private static string? Confirm(IDictionary<string, JsonElement> args) =>
        args.TryGetValue("confirm", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
