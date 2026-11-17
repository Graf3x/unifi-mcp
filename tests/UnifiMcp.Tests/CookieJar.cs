using System.Net;

namespace UnifiMcp.Tests;

/// <summary>
/// Keeps cookies across TestServer requests the way a browser would: <see cref="CookieContainer"/> sends a Secure cookie
/// only over https and drops one whose expiry has passed. Raw Set-Cookie lines are kept so tests can read attributes
/// the container does not model (SameSite).
/// </summary>
public sealed class CookieJar : DelegatingHandler
{
    private readonly List<string> _setCookies = [];

    public CookieContainer Container { get; } = new();

    /// <summary>Every Set-Cookie line the server sent, oldest first.</summary>
    public IReadOnlyList<string> SetCookies => _setCookies;

    public Cookie? Get(Uri uri, string name) => Container.GetCookies(uri).FirstOrDefault(c => c.Name == name && !c.Expired);

    /// <summary>The last Set-Cookie line for <paramref name="name"/>.</summary>
    public string? LastSetCookie(string name) => _setCookies.LastOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var header = Container.GetCookieHeader(uri);
        if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
        var response = await base.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var lines))
            foreach (var line in lines)
            {
                _setCookies.Add(line);
                Container.SetCookies(uri, line);
            }
        return response;
    }
}
