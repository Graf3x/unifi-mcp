using System.Buffers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UnifiMcp;

/// <summary>Whether the console's certificate is trusted, and by what.</summary>
public enum CertificateVerdict { Refused, KeyPin, CertificatePin, Ca }

/// <summary>
/// How a request reaches the console: nothing is sent until the key and pin exist, the key travels only in its header
/// and only to a console whose certificate passed, redirects are handed back rather than followed, and the key is
/// scrubbed from everything returned. A refused certificate comes back as an error that says what the console
/// presented and where to change the pin. What one request may cost is bounded here: its turn, its deadline, its bytes.
/// </summary>
/// <remarks>
/// The console's certificate is usually self-signed, so by default it is pinned: a pin is the SHA-256 of the
/// certificate's DER or of its public key (SubjectPublicKeyInfo, which survives a re-issued certificate that keeps its
/// key), and several may be listed. With no pin nothing matches, so verification cannot be off. With
/// UNIFI_CERT_TRUST=ca the usual chain and host-name checks apply instead. Pins are read again at each handshake.
/// </remarks>
public sealed class ConsoleTransport
{
    /// <summary>A certificate the console presented and this server refused: its fingerprints and the TLS stack's view.</summary>
    private sealed record Refused(string Certificate, string Key, SslPolicyErrors Errors);

    /// <summary>The most this server reads of one answer, declared or not (16 MiB).</summary>
    public const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>How many requests may be at the console at once, from every tool call together.</summary>
    public const int MaxInFlight = 4;

    /// <summary>How long one request may take, from waiting for its turn to the last byte of its answer.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly Settings _settings;
    private readonly HttpClient _http;
    private readonly TimeSpan _deadline;
    private readonly SemaphoreSlim _slots = new(MaxInFlight, MaxInFlight);

    /// <summary>
    /// The certificate most recently refused. Every request goes to the one console, so a refused handshake is explained
    /// with it; it is never cleared by reading, so requests refused at the same time all get the explanation, and an
    /// accepted handshake forgets it.
    /// </summary>
    private volatile Refused? _refused;

    /// <param name="handler">Tests only: stands in for the console, skipping TLS. Otherwise the pinned handler is built here.</param>
    /// <param name="deadline">Tests only: in place of <see cref="Deadline"/>.</param>
    public ConsoleTransport(Settings settings, HttpMessageHandler? handler = null, TimeSpan? deadline = null)
    {
        _settings = settings;
        _deadline = deadline ?? Deadline;
        // The deadline below covers the whole request, the body included, so the client's own timeout is not needed.
        _http = new HttpClient(handler ?? PinnedHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// A request to <paramref name="path"/>, from the console's root (/proxy/network/…). It waits for one of
    /// <see cref="MaxInFlight"/> turns, and has <see cref="Deadline"/> in all, that wait and the body included. The answer is read as it
    /// streams in and refused past <see cref="MaxResponseBytes"/>, or past what the current answer's
    /// <see cref="UpstreamBudget"/> has left, whether or not the console declared a length.
    /// </summary>
    public async Task<UnifiResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var missing = _settings.Missing();
        if (missing.Count > 0)
            throw new UnifiNotConfigured("UniFi MCP is not configured, so nothing was sent. Still needed: " + string.Join("; ", missing) + ".");

        var what = $"{method} {path.Split('?')[0]}";
        var budget = UpstreamBudget.Current;
        budget?.Request(what);

        var key = _settings.ApiKey()!;
        using var req = new HttpRequestMessage(method, _settings.BaseUrl.GetLeftPart(UriPartial.Authority) + path);
        req.Headers.Add("X-API-Key", key);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        try
        {
            await _slots.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UnifiError($"The console is busy with this server's other requests, so {what} was not sent. Try again in a moment.");
        }

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var left = budget?.BytesLeft ?? long.MaxValue;
            var limit = Math.Min(MaxResponseBytes, left);
            UnifiError TooLarge() => limit < MaxResponseBytes
                ? budget!.OutOfBytes(what)
                : new UnifiError($"The console's answer to {what} is larger than {UpstreamBudget.Megabytes(MaxResponseBytes)}, so this server stopped reading it. "
                                 + "Ask for less: a filter or a smaller limit on list tools, or one device, client or shorter window on the others.");
            var text = await ReadAsync(resp.Content, limit, TooLarge, budget, deadline.Token);
            return new UnifiResponse((int)resp.StatusCode, Scrub(text, key));
        }
        catch (HttpRequestException ex)
        {
            // Only a handshake that failed authentication is the certificate's fault; a hang-up or a closed port is not.
            var refused = ex.HttpRequestError == HttpRequestError.SecureConnectionError && ex.InnerException is AuthenticationException ? _refused : null;
            throw new UnifiError(refused is null ? Scrub($"Could not reach the console: {ex.Message}", key) : Refusal(refused));
        }
        catch (IOException ex)
        {
            // The connection broke while the answer was being read.
            throw new UnifiError(Scrub($"Could not reach the console: {ex.Message}", key));
        }
        // The deadline, or the client's own timeout; a cancellation from the caller is the caller's to see.
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested && (deadline.IsCancellationRequested || e is TaskCanceledException))
        {
            throw new UnifiError("The console did not answer in time.");
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The body as text, read as it arrives: refused before reading when the console declares more than
    /// <paramref name="limit"/> bytes, and stopped as soon as more than that arrives when it does not (a chunked answer).
    /// What was read is charged to <paramref name="budget"/>, also when it is refused.
    /// </summary>
    private static async Task<string> ReadAsync(HttpContent content, long limit, Func<UnifiError> tooLarge, UpstreamBudget? budget, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw tooLarge();
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int n; (n = await stream.ReadAsync(chunk, ct)) > 0;)
            {
                if (body.Length + n > limit) throw tooLarge();
                body.Write(chunk, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            budget?.Read(body.Length);
        }

        var bytes = body.GetBuffer().AsSpan(0, (int)body.Length);
        var encoding = Encoding.UTF8;
        try
        {
            if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset) encoding = Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            // An unknown charset: JSON is UTF-8.
        }
        var preamble = encoding.Preamble;
        if (preamble.Length > 0 && bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
        return encoding.GetString(bytes);
    }

    /// <summary>
    /// The one rule for trusting the console's certificate, at the handshake and on the setup page. In CA mode, trusted
    /// when the platform has no complaint (chain, host name); pins are not read. Otherwise trusted when a pin is the
    /// certificate's or its key's fingerprint, whatever the chain says; the key pin is reported when both are pinned.
    /// </summary>
    public static CertificateVerdict Judge(Settings settings, string certificateSha256, string keySha256, SslPolicyErrors errors)
    {
        if (settings.Trust == CertTrust.Ca) return errors == SslPolicyErrors.None ? CertificateVerdict.Ca : CertificateVerdict.Refused;
        var pins = settings.CertPins() ?? [];
        bool Pinned(string sha256) => pins.Contains(sha256, StringComparer.OrdinalIgnoreCase);
        return Pinned(keySha256) ? CertificateVerdict.KeyPin
            : Pinned(certificateSha256) ? CertificateVerdict.CertificatePin
            : CertificateVerdict.Refused;
    }

    /// <summary>The SHA-256 of the certificate's DER, as a pin.</summary>
    public static string CertificateSha256(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>The SHA-256 of the certificate's public key (SubjectPublicKeyInfo), as a pin that survives a re-issue with the same key.</summary>
    public static string KeySha256(X509Certificate cert)
    {
        if (cert is X509Certificate2 c2) return Convert.ToHexString(SHA256.HashData(c2.PublicKey.ExportSubjectPublicKeyInfo()));
        using var copy = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
        return Convert.ToHexString(SHA256.HashData(copy.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    private SocketsHttpHandler PinnedHandler() => new()
    {
        AllowAutoRedirect = false, // the key must not follow a redirect elsewhere
        UseProxy = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null) return false;
                var (certificate, key) = (CertificateSha256(cert), KeySha256(cert));
                var ok = Judge(_settings, certificate, key, errors) != CertificateVerdict.Refused;
                _refused = ok ? null : new(certificate, key, errors);
                return ok;
            },
        },
    };

    private string Refusal(Refused seen) => _settings.Trust == CertTrust.Ca
        ? $"The console's certificate is not trusted ({seen.Errors}), so nothing was sent. With UNIFI_CERT_TRUST=ca it needs a certificate from a CA this server trusts, issued for {_settings.BaseUrl.Host} (the host in UNIFI_URL). It presented certificate SHA-256 {seen.Certificate}."
        : $"The console's certificate does not match the pin, so nothing was sent. It presented certificate SHA-256 {seen.Certificate} with public key SHA-256 {seen.Key}; compare one with the console, then set UNIFI_CERT_SHA256 to it and restart the server (for example in .env, or with fleet-key), or put it in {_settings.CertPinFile} if that variable is unset. Several pins can be listed, separated by commas.";

    private static string Scrub(string text, string key) => text.Replace(key, "[redacted]", StringComparison.Ordinal);
}
