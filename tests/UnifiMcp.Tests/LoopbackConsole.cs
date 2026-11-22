using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>
/// A console on loopback HTTPS presenting a certificate the test chose, so trust is tested with real handshakes.
/// It answers GET /v1/info, and /v1/moved with a redirect elsewhere.
/// </summary>
public sealed class LoopbackConsole : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string?> _keys;

    private LoopbackConsole(WebApplication app, List<string?> keys, Uri url) => (_app, _keys, Url) = (app, keys, url);

    /// <summary>The console's address, https://127.0.0.1:port.</summary>
    public Uri Url { get; }

    /// <summary>The API key each request that got through carried.</summary>
    public IReadOnlyList<string?> Keys
    {
        get { lock (_keys) return [.. _keys]; }
    }

    /// <summary>A self-signed certificate, with <paramref name="key"/> when given so a reissue can keep it.</summary>
    public static X509Certificate2 NewCertificate(string subject = "CN=udm", RSA? key = null)
    {
        using var own = key is null ? RSA.Create(2048) : null;
        var request = new CertificateRequest(subject, key ?? own!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Through PKCS#12, so the TLS stack on every platform can use the private key.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>The SHA-256 of the certificate, worked out here rather than by the code under test.</summary>
    public static string CertificateSha256(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>The SHA-256 of the certificate's public key (SubjectPublicKeyInfo).</summary>
    public static string KeySha256(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>While true, each new connection is dropped before the TLS handshake finishes: a failure that is no refusal.</summary>
    public bool HangUp { get; set; }

    public static async Task<LoopbackConsole> StartAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        LoopbackConsole? console = null;
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
        {
            l.Use(next => connection =>
            {
                if (console!.HangUp)
                {
                    connection.Abort();
                    return Task.CompletedTask;
                }
                return next(connection);
            });
            l.UseHttps(certificate);
        }));
        var app = builder.Build();
        var keys = new List<string?>();
        app.MapGet(FakeUnifi.Prefix + "/v1/info", (HttpContext c) =>
        {
            lock (keys) keys.Add(c.Request.Headers["X-API-Key"].SingleOrDefault());
            // No keep-alive, so every request makes its own handshake.
            c.Response.Headers.Connection = "close";
            return Results.Text("""{"applicationVersion":"11.0.97"}""", "application/json");
        });
        app.MapGet(FakeUnifi.Prefix + "/v1/moved", () => Results.Redirect("https://example.invalid/elsewhere"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return console = new LoopbackConsole(app, keys, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
