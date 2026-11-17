using System.Text;

namespace UnifiMcp;

/// <summary>
/// The connector's browser pages: one plain HTML shell, no scripts, nothing loaded from elsewhere. Every value that goes
/// into a page passes through <see cref="Encode"/>; callers hand <see cref="Html"/> markup they built that way.
/// </summary>
public static class Pages
{
    /// <summary>
    /// Must stay same-origin, never no-referrer: a browser form POST from a no-referrer page sends "Origin: null", which the
    /// request guard and the disconnect check (rightly) refuse, so Allow, Deny and Disconnect would all 403. same-origin
    /// still sends no Referer to other origins (the client callback the consent redirects to).
    /// </summary>
    public const string ReferrerPolicy = "same-origin";

    private const string Style =
        "body{font:16px/1.5 system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;color:#1b1b1b;background:#fff}"
        + ".app{border:1px solid #8886;border-radius:.5rem;padding:.75rem 1rem;margin:.75rem 0}.muted{color:#777;font-size:.9rem}"
        + ".app form{margin:.5rem 0 0}button{font:inherit;padding:.3rem .9rem}"
        + "h1{font-size:1.4rem}@media(prefers-color-scheme:dark){body{color:#eee;background:#161616}}";

    /// <summary>A whole page. <paramref name="bodyHtml"/> is markup: encode every value in it with <see cref="Encode"/>.</summary>
    public static string Html(string title, string bodyHtml) =>
        $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="referrer" content="same-origin"><title>{Encode(title)} · UniFi Network</title><style>{Style}</style></head>
        <body>{bodyHtml}</body></html>
        """;

    /// <summary>A page that says what went wrong. Both values are encoded.</summary>
    public static string ErrorPage(string title, string text) => Html(title, $"<h1>{Encode(title)}</h1><p>{Encode(text)}</p>");

    /// <summary>
    /// Encodes for element content and double-quoted attributes: &amp; &lt; &gt; and &quot;. The apostrophe is left as is,
    /// so never put a value in a single-quoted attribute.
    /// </summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>Writes a page: HTML, never cached, never framed, and no scripts allowed to run.</summary>
    /// <param name="formTarget">
    /// An origin (scheme://host[:port]) the page's form may end up at besides this one: browsers apply form-action to the
    /// redirect a form POST answers with, so the consent page names the client's callback origin here.
    /// </param>
    public static Task WriteAsync(HttpResponse response, int status, string html, string? formTarget = null)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = ReferrerPolicy;
        var formAction = "'self'" + (CspOrigin(formTarget) is { } extra ? " " + extra : "");
        response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; form-action {formAction}";
        return response.WriteAsync(html);
    }

    /// <summary>An absolute http(s) origin as a CSP source, or null; nothing else may reach the header.</summary>
    private static string? CspOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.GetLeftPart(UriPartial.Authority)
            : null;
}
