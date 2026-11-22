using System.Text;

namespace UnifiMcp;

/// <summary>
/// How a request reaches the console: nothing is sent until the key and pin exist, the key travels only in its header
/// and only to a console whose certificate passed (see <see cref="CertPin"/>), redirects are handed back rather than
/// followed, and the key is scrubbed from everything returned. A refused certificate comes back as an error that says
/// what the console presented and where to change the pin.
/// </summary>
public sealed class ConsoleTransport
{
    private readonly Settings _settings;
    private readonly PinState _pin = new();
    private readonly HttpClient _http;

    /// <param name="handler">Tests only: stands in for the console, skipping TLS. Otherwise the pinned handler is built here.</param>
    public ConsoleTransport(Settings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? CertPin.CreateHandler(() => settings.CertPins(), settings.Trust, _pin), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>A request to <paramref name="path"/>, from the console's root (/proxy/network/…).</summary>
    public async Task<UnifiResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var missing = _settings.Missing();
        if (missing.Count > 0)
            throw new UnifiNotConfigured("UniFi MCP is not configured, so nothing was sent. Still needed: " + string.Join("; ", missing) + ".");

        var key = _settings.ApiKey()!;
        using var req = new HttpRequestMessage(method, _settings.BaseUrl.GetLeftPart(UriPartial.Authority) + path);
        req.Headers.Add("X-API-Key", key);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return new UnifiResponse((int)resp.StatusCode, Scrub(await resp.Content.ReadAsStringAsync(ct), key));
        }
        catch (HttpRequestException ex)
        {
            var seen = _pin.Rejected;
            _pin.Rejected = null;
            throw new UnifiError(seen is null ? Scrub($"Could not reach the console: {ex.Message}", key) : Refusal(seen));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UnifiError("The console did not answer in time.");
        }
    }

    private string Refusal(RejectedCertificate seen) => _settings.Trust == CertTrust.Ca
        ? $"The console's certificate is not trusted ({seen.Errors}), so nothing was sent. With UNIFI_CERT_TRUST=ca it needs a certificate from a CA this server trusts, issued for {_settings.BaseUrl.Host} (the host in UNIFI_URL). It presented certificate SHA-256 {seen.Certificate}."
        : $"The console's certificate does not match the pin, so nothing was sent. It presented certificate SHA-256 {seen.Certificate} with public key SHA-256 {seen.Key}; compare one with the console, then set UNIFI_CERT_SHA256 to it and restart the server (for example in .env, or with fleet-key), or put it in {_settings.CertPinFile} if that variable is unset. Several pins can be listed, separated by commas.";

    private static string Scrub(string text, string key) => text.Replace(key, "[redacted]", StringComparison.Ordinal);
}
