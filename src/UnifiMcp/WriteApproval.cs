using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Whether a write may run, and how the owner approved it. A client that can show a form (one on the 2026-07-28 revision
/// that declares form elicitation) gets one: the change with Accept or Decline, and <c>confirm</c> is ignored, so the
/// model cannot approve its own write. Any other client falls back to <c>confirm</c> set to the tool's own name.
/// </summary>
/// <remarks>
/// The server is stateless, so the form travels as a multi round-trip request: the first call answers "input required"
/// with the form and a sealed token, and the client calls again with the owner's answer and the token. The token names
/// this tool, the request it will send, these exact arguments (less <c>confirm</c>) and this owner; it lapses after
/// <see cref="Lifetime"/> and is spent by the answer it carries. A call whose token does not open, or is spent, gets a
/// fresh form. Tokens are sealed with a key that lives only as long as the process: they need no more, and the
/// connector's keyring (which may be broken or absent) cannot then take writes down with it.
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
    private const int MaxBody = 2_000;
    private const int MaxValue = 200;
    private static readonly IDataProtector Protector = new EphemeralDataProtectionProvider().CreateProtector("UnifiMcp.WriteApproval.v2");
    /// <summary>Spent token ids and when each expires (Unix milliseconds). Guarded by locking on itself.</summary>
    private static readonly Dictionary<string, long> Spent = new(StringComparer.Ordinal);
    private const long Keep = 60_000;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Run, stop with an answer, or ask the owner by throwing <see cref="InputRequiredException"/>. Called once the request
    /// is built and checked, so the owner approves the <paramref name="path"/> that will be sent.
    /// </summary>
    public static Decision Decide(ApiOperation op, string path, IDictionary<string, JsonElement> args, RequestContext<CallToolRequestParams> request)
    {
        if (!CanAsk(request.Server))
            return Confirm(args) == op.ToolName ? new Run("approved by confirm argument") : new Stop(ToolText.Ok(
                $"Nothing was sent. {op.Method} {path} changes the live network.\n"
                + $"Arguments: {JsonSerializer.Serialize(Approved(args))}\n"
                + $"To run it, call {op.ToolName} again with confirm set to exactly \"{op.ToolName}\"."));

        var login = Audit.Login(request.User);
        var time = request.Services?.GetService<TimeProvider>() ?? TimeProvider.System;
        var audit = Audit.Logger(request.Services);
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
                [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams { Message = Form(op, path, args), RequestedSchema = new() }),
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
    /// What the owner sees: the change and the exact request, every argument but the body in full, then the body, cut short
    /// when long. What identifies the target (the path and its parameters) always comes before the cut.
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
                sb.Append(name).Append(": ").Append(Cut(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), MaxValue)).Append('\n');
        }
        if (args.TryGetValue("body", out var body))
        {
            var shown = JsonSerializer.Serialize(body, Indented);
            sb.Append("\nBody:\n").Append(shown.Length <= MaxBody
                ? shown
                : shown[..MaxBody] + $"\n[truncated: {MaxBody:N0} of {shown.Length:N0} characters shown]");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A token for this tool, request, arguments and owner, good once and for <see cref="Lifetime"/>.</summary>
    internal static string Seal(IDataProtector protector, TimeProvider time, string tool, string path, IDictionary<string, JsonElement> args, string login) =>
        protector.Protect(string.Join('\n', Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), tool, path, Digest(args), login,
            (time.GetUtcNow() + Lifetime).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));

    /// <summary>The token's id when it is ours, unexpired, and names exactly this tool, request, arguments and owner.</summary>
    internal static string? Open(IDataProtector protector, TimeProvider time, string? token, string tool, string path, IDictionary<string, JsonElement> args, string login)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string[] parts;
        try { parts = protector.Unprotect(token).Split('\n'); }
        catch (CryptographicException) { return null; }
        return parts.Length == 6 && parts[1] == tool && parts[2] == path && parts[3] == Digest(args) && parts[4] == login
               && long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
               && time.GetUtcNow().ToUnixTimeMilliseconds() <= expires
            ? parts[0] + "\n" + parts[5]
            : null;
    }

    /// <summary>
    /// Marks an opened token used; false when it already was or has expired. The check, the clean-up and the mark happen
    /// under one lock and in the token's own unit (milliseconds), so a token can never be spent after expiry, and a spent
    /// id is only dropped once it can no longer be spent (kept a minute longer, in case the clock steps back).
    /// </summary>
    internal static bool Spend(string opened, TimeProvider time)
    {
        var expires = long.Parse(opened[(opened.IndexOf('\n') + 1)..], CultureInfo.InvariantCulture);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (Spent)
        {
            if (now > expires) return false;
            foreach (var old in Spent.Where(s => s.Value < now - Keep).Select(s => s.Key).ToList()) Spent.Remove(old);
            return Spent.TryAdd(opened, expires);
        }
    }

    /// <summary>The arguments the owner approves: everything but <c>confirm</c>, by name.</summary>
    private static SortedDictionary<string, JsonElement> Approved(IDictionary<string, JsonElement> args) =>
        new(args.Where(a => a.Key != "confirm").ToDictionary(), StringComparer.Ordinal);

    private static string Digest(IDictionary<string, JsonElement> args) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Approved(args)))));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string? Confirm(IDictionary<string, JsonElement> args) =>
        args.TryGetValue("confirm", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
