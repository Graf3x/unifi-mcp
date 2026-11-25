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
/// that declares form elicitation) gets one: the preview with Accept or Decline, and <c>confirm</c> is ignored, so the
/// model cannot approve its own write. Any other client falls back to <c>confirm</c> set to the tool's own name.
/// </summary>
/// <remarks>
/// The server is stateless, so the form travels as a multi round-trip request: the first call answers "input required"
/// with the form and a sealed token, and the client calls again with the owner's answer and the token. The token names
/// this tool, these exact arguments (less <c>confirm</c>) and this owner, and lapses after <see cref="Lifetime"/>; a call
/// whose token does not open for it gets a fresh form.
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
    private const string Purpose = "UnifiMcp.WriteApproval.v1";
    private const int MaxArguments = 2_500;
    private static readonly IDataProtectionProvider Fallback = new EphemeralDataProtectionProvider();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Run, stop with an answer, or ask the owner by throwing <see cref="InputRequiredException"/>.</summary>
    public static Decision Decide(ApiOperation op, IDictionary<string, JsonElement> args, RequestContext<CallToolRequestParams> request)
    {
        if (!CanAsk(request.Server))
            return Confirm(args) == op.ToolName ? new Run("approved by confirm argument") : new Stop(ToolText.Ok(
                $"Nothing was sent. {op.Method} {op.Path} changes the live network.\n"
                + $"Arguments: {JsonSerializer.Serialize(Approved(args))}\n"
                + $"To run it, call {op.ToolName} again with confirm set to exactly \"{op.ToolName}\"."));

        var login = Audit.Login(request.User);
        var protector = (request.Services?.GetService<IDataProtectionProvider>() ?? Fallback).CreateProtector(Purpose);
        var time = request.Services?.GetService<TimeProvider>() ?? TimeProvider.System;
        var answer = request.Params?.InputResponses?.TryGetValue(InputKey, out var response) == true
            ? response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)
            : null;
        if (answer is not null && Opens(protector, time, request.Params!.RequestState, op.ToolName, args, login))
        {
            if (answer.IsAccepted) return new Run("approved by form");
            Audit.Logger(request.Services)?.Log(Audit.Level(write: true), "{Login} {Tool} declined by form", login, op.ToolName);
            return new Stop(ToolText.Ok($"The owner declined this change. Nothing was sent ({op.Method} {op.Path}). "
                                        + "Do not retry it unless the owner asks you to."));
        }

        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams { Message = Form(op, args), RequestedSchema = new() }),
            },
            Seal(protector, time, op.ToolName, args, login));
    }

    /// <summary>A form can be shown: a 2026-07-28 request that declares form elicitation (a bare declaration means form).</summary>
    private static bool CanAsk(McpServer server) => server.IsMrtrSupported && server.ClientCapabilities?.Elicitation?.Form is not null;

    /// <summary>What the owner sees: the change, the request and its arguments, with long arguments cut short.</summary>
    internal static string Form(ApiOperation op, IDictionary<string, JsonElement> args)
    {
        var shown = JsonSerializer.Serialize(Approved(args), Indented);
        if (shown.Length > MaxArguments) shown = shown[..MaxArguments] + "\n[truncated: the arguments are longer than this]";
        return $"{op.Summary}\n\nThis changes your live network: {op.Method} {op.Path}\n\nArguments:\n{shown}";
    }

    /// <summary>A token for this tool, these arguments and this owner, good for <see cref="Lifetime"/>.</summary>
    internal static string Seal(IDataProtector protector, TimeProvider time, string tool, IDictionary<string, JsonElement> args, string login) =>
        protector.Protect(string.Join('\n', tool, Digest(args), login,
            (time.GetUtcNow() + Lifetime).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));

    /// <summary>The token is ours, unexpired, and names exactly this tool, these arguments and this owner.</summary>
    internal static bool Opens(IDataProtector protector, TimeProvider time, string? token, string tool, IDictionary<string, JsonElement> args, string login)
    {
        if (string.IsNullOrEmpty(token)) return false;
        string[] parts;
        try { parts = protector.Unprotect(token).Split('\n'); }
        catch (CryptographicException) { return false; }
        return parts.Length == 4 && parts[0] == tool && parts[1] == Digest(args) && parts[2] == login
               && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
               && time.GetUtcNow().ToUnixTimeMilliseconds() <= expires;
    }

    /// <summary>The arguments the owner approves: everything but <c>confirm</c>, by name.</summary>
    private static SortedDictionary<string, JsonElement> Approved(IDictionary<string, JsonElement> args) =>
        new(args.Where(a => a.Key != "confirm").ToDictionary(), StringComparer.Ordinal);

    private static string Digest(IDictionary<string, JsonElement> args) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Approved(args)))));

    private static string? Confirm(IDictionary<string, JsonElement> args) =>
        args.TryGetValue("confirm", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
