using System.Text;

namespace UnifiMcp;

/// <summary>
/// The setup page's markup. It reads well with scripts off (the guide, the commands, every check as "not checked yet");
/// setup.js runs the checks, lights the ports and shows the code. Every value passes through <see cref="Pages.Encode"/>.
/// </summary>
public static class SetupHtml
{
    private sealed record Port(string Id, string Label, string Cable);

    private static readonly Port[] Console =
        [new("console", "Console", "blue"), new("certificate", "Certificate", "blue"), new("apikey", "API key", "blue"), new("site", "Site", "blue")];

    private static Port[] Claude(int guide) =>
        [new("connector", "Connector", "orange"), new("tunnel", "Tunnel", "orange"), new("signin", "Sign-in", "orange"),
         new("callers", guide == 1 ? "Claude Code" : "Tailnet", guide == 1 ? "blue" : "green")];

    private static string E(string s) => Pages.Encode(s);

    private static string Version => typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Page(Settings s, SetupGuide guide, ConnectorState connector, bool codes)
    {
        var g = SetupGuide.GuideOf(s);
        var body = new StringBuilder();
        body.Append(Plate(s, g, codes, locked: false));
        body.Append("<main id=\"main\">");
        body.Append("<div class=\"desk\">");
        body.Append(Checks(g));
        body.Append(Connect(s, g, connector, codes));
        body.Append("</div>");
        body.Append(Guide(guide, g));
        body.Append("</main>");
        body.Append(Footer(s));
        return Shell("Setup", body.ToString(), g, codes);
    }

    public static string Locked(Settings s, string? why)
    {
        var g = SetupGuide.GuideOf(s);
        var docker = Listeners.UnixPath(s.Listen) is not null;
        var where = docker
            ? """<pre><code>docker compose logs unifi-mcp | grep "Setup page"</code></pre>"""
            : "<p>It's in the terminal where the server runs, a few lines after it starts.</p>";
        var body = $"""
            {Plate(s, g, codes: false, locked: true)}
            <main id="main" class="locked">
              <h1>Open this page from the server's log</h1>
              {(why is null ? "" : $"<p class=\"why\">{E(why)}</p>")}
              <p>The setup page can show the sign-in code, so it opens only from the link the server prints when it starts.
              That link carries a key; this browser keeps it until the server restarts.</p>
              {where}
              <p>Look for the line that starts with <code>Setup page:</code> and open its link here.</p>
            </main>
            {Footer(s)}
            """;
        return Shell("Locked", body, g, codes: false);
    }

    private static string Shell(string title, string body, int guide, bool codes) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="referrer" content="same-origin"><meta name="color-scheme" content="light dark">
        <title>{E(title)} · unifi-mcp</title>
        <link rel="icon" href="/setup/icon.png"><link rel="stylesheet" href="/setup/setup.css">
        <script src="/setup/setup.js" defer></script></head>
        <body data-guide="{guide}" data-codes="{(codes ? "on" : "off")}">
        <a class="skip" href="#main">Skip to the checks</a>
        {body}
        </body></html>
        """;

    private static string Plate(Settings s, int g, bool codes, bool locked)
    {
        var chips = new StringBuilder();
        chips.Append($"<li>Console <b>{E(s.BaseUrl.Authority)}</b></li>");
        if (!locked)
        {
            chips.Append($"<li>Guide <b>{g}</b> setup</li>");
            chips.Append(s.AllowWrites ? "<li class=\"hot\">Writes <b>on</b></li>" : "<li>Read-only</li>");
            if (s.AccessToken is not null) chips.Append("<li>Access token <b>on</b></li>");
        }

        var ports = new StringBuilder();
        foreach (var (name, bank) in new[] { ("To the console", Console), ("To Claude", Claude(g)) })
        {
            ports.Append($"<div class=\"bank\" role=\"group\" aria-label=\"{E(name)}\"><p class=\"bank-label\" aria-hidden=\"true\">{E(name)}</p><ol class=\"bank-ports\">");
            foreach (var p in bank)
            {
                ports.Append($"""
                    <li class="port" data-check="{p.Id}" data-cable="{p.Cable}"><a href="#check-{p.Id}">
                    <span class="port-label">{E(p.Label)}</span>
                    <span class="jack" aria-hidden="true"><span class="led link"></span><span class="led act"></span><span class="hole"></span><span class="plug"></span></span>
                    <span class="port-state">{(locked ? "locked" : "not checked yet")}</span></a></li>
                    """);
            }
            ports.Append("</ol></div>");
        }

        var readout = locked
            ? """<div class="lcd"><p class="lcd-label">Setup page</p><p class="digits">Locked</p><p class="lcd-line">Needs the link from the log.</p></div>"""
            : codes
            ? $"""
              <section class="lcd" id="code" aria-labelledby="code-label">
                <h2 class="lcd-label" id="code-label">Sign-in code</h2>
                <p class="digits" id="code-digits" aria-live="polite">-----&#8202;-----</p>
                <p class="lcd-line" id="code-line">Shows here when Claude opens the sign-in page.</p>
                <div class="meter" aria-hidden="true"><span id="code-meter"></span></div>
                <div class="lcd-actions"><button type="button" id="code-copy" disabled>Copy code</button><button type="button" id="code-new">Get a code now</button></div>
              </section>
              """
            : Readout(s, g);
        var foot = locked ? "" :
            """<div class="plate-foot"><button type="button" id="rerun" class="plate-button">Run the checks again</button><p class="tally" id="tally" aria-live="polite"></p></div>""";

        return $"""
            <header class="rack">
              <div class="plate">
                <span class="ear left" aria-hidden="true"></span><span class="ear right" aria-hidden="true"></span>
                <div class="plate-top">
                  <p class="brand"><span class="name">unifi-mcp</span> <span class="ver">{E(Version)}</span></p>
                  <ul class="chips">{chips}</ul>
                </div>
                <div class="plate-body">
                  {readout}
                  {foot}
                  <div class="ports">{ports}</div>
                </div>
              </div>
            </header>
            """;
    }

    /// <summary>Without sign-in codes, the readout shows the one address the owner pastes into Claude.</summary>
    private static string Readout(Settings s, int g)
    {
        var (label, value, line) = g == 1
            ? ("Claude Code connects to", LocalMcp(s), "With the header Claude Code sends; see Connect Claude.")
            : s.PublicBaseUrl is { } pub
            ? ("Connector URL", pub.GetLeftPart(UriPartial.Authority) + "/mcp", "Paste it into Claude's Add custom connector.")
            : ("Connector URL", "Not set", "Set MCP_PUBLIC_BASE_URL first.");
        return $"""
            <section class="lcd readout" aria-labelledby="readout-label">
              <h2 class="lcd-label" id="readout-label">{E(label)}</h2>
              <p class="digits url">{E(value)}</p>
              <p class="lcd-line">{E(line)}</p>
            </section>
            """;
    }

    private static string LocalMcp(Settings s) =>
        s.Listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal).TrimEnd('/') + "/mcp";

    private static string Checks(int g)
    {
        var sb = new StringBuilder("""
            <section class="checks" aria-labelledby="checks-h">
              <h2 id="checks-h">Checks</h2>
              <p class="lede">Each one tests a setting against the real thing: the console, Cloudflare, your sign-in. They only read; nothing is changed.</p>
              <ol class="results" id="results">
            """);
        foreach (var p in Console.Concat(Claude(g)))
        {
            sb.Append($"""
                <li class="result" id="check-{p.Id}" data-check="{p.Id}">
                  <span class="dot" aria-hidden="true"></span>
                  <h3 class="result-name">{E(p.Label)}</h3>
                  <p class="result-summary">Not checked yet.</p>
                  <div class="result-more"></div>
                </li>
                """);
        }
        sb.Append("</ol></section>");
        return sb.ToString();
    }

    private static string Connect(Settings s, int g, ConnectorState connector, bool codes)
    {
        var sb = new StringBuilder("""<aside class="connect" aria-labelledby="connect-h"><h2 id="connect-h">Connect Claude</h2>""");
        if (g == 1)
        {
            var login = s.AllowedLogins().FirstOrDefault() ?? "me";
            sb.Append("<p>In a second terminal, add the server to Claude Code. The header carries a name from <code>MCP_ALLOWED_LOGINS</code>.</p>");
            sb.Append(Code($"claude mcp add --transport http unifi {LocalMcp(s)} --header \"{CallerPolicy.LoginHeader}: {login}\""));
            sb.Append("<p>Then ask Claude to call <code>getting_started</code>. It should say the server is configured.</p>");
        }
        else if (s.PublicBaseUrl is not { } pub0)
        {
            sb.Append("<p>Set <code>MCP_PUBLIC_BASE_URL</code> to the tunnel's public hostname and restart; the connector URL then appears here.</p>");
        }
        else
        {
            var pub = pub0.GetLeftPart(UriPartial.Authority);
            sb.Append("<ol class=\"steps\">");
            sb.Append("<li>In Claude, open Settings, Connectors, and choose Add custom connector.</li>");
            sb.Append($"<li>Paste the connector URL:{Code(pub + "/mcp")}</li>");
            sb.Append(s.SignIn == SignInMode.Code
                ? "<li>Claude opens a sign-in page. Its code appears at the top of this page" + (codes ? "" : " once the connector is ready") + "; enter it there and approve.</li>"
                : "<li>Claude opens a sign-in page that sends you to Google. Sign in with an allowed address and approve.</li>");
            sb.Append("<li>Ask Claude to call <code>getting_started</code>.</li></ol>");
            sb.Append($"<p class=\"aside\">Connected clients, each with a Disconnect button: <a href=\"{E(pub)}/connections\" rel=\"noopener\" target=\"_blank\">{E(pub)}/connections</a></p>");
            if (s.AccessToken is not null)
            {
                sb.Append("<h3>Claude Code with the access token</h3>");
                sb.Append(Code($"claude mcp add --transport http unifi {pub}/mcp --header \"Authorization: Bearer <MCP_ACCESS_TOKEN>\""));
                sb.Append("<p class=\"aside\">Put the token from <code>.env</code> in place of the placeholder. This page never shows it.</p>");
            }
            if (s.AllowedLogins().Count > 0)
            {
                sb.Append("<h3>Claude Code over Tailscale</h3>");
                sb.Append(Code("sudo tailscale serve --bg --https=7420 unix:$PWD/run/mcp.sock"));
                sb.Append(Code("claude mcp add --transport http unifi https://<machine>.<tailnet>.ts.net:7420/mcp"));
            }
            if (!connector.Ready) sb.Append("<p class=\"aside warn\">The connector isn't ready yet; the Connector check says why.</p>");
        }
        sb.Append("</aside>");
        return sb.ToString();
    }

    private static string Code(string text) => $"<pre class=\"cmd\"><code>{E(text)}</code></pre>";

    private static string Guide(SetupGuide guide, int g)
    {
        var sb = new StringBuilder("""<section class="guide" id="guide" aria-labelledby="guide-h"><h2 id="guide-h">Setup guide</h2>""");
        if (guide.Parts.Count == 0)
        {
            sb.Append($"<p>This build doesn't carry the guide. It's in the <a href=\"{SetupGuide.RepoUrl}#setup\" rel=\"noopener\" target=\"_blank\">README on GitHub</a>.</p></section>");
            return sb.ToString();
        }
        sb.Append($"<p class=\"lede\">The README's setup section. Highlighted values are this server's own. Your settings follow guide {g}, so it opens first.</p>");
        foreach (var part in guide.Parts.Where(p => p.Id == "intro"))
            sb.Append($"<div class=\"intro\">{part.Html}</div>");
        sb.Append("<div class=\"tabs\" role=\"tablist\" aria-label=\"Setup guide parts\" hidden></div>");
        foreach (var part in guide.Parts.Where(p => p.Id != "intro"))
            sb.Append($"<section class=\"part\" data-part=\"{E(part.Id)}\" data-title=\"{E(part.Title)}\">{part.Html}</section>");
        sb.Append("</section>");
        return sb.ToString();
    }

    private static string Footer(Settings s) => $"""
        <footer class="foot">
          <p>unifi-mcp {E(Version)}. This page answers only on {E(s.SetupListen)}, never through the tunnel, and only to the
          browser that opened the setup link. Turn it off with <code>MCP_SETUP_PAGE=off</code>.</p>
          <p><a href="{SetupGuide.RepoUrl}" rel="noopener" target="_blank">Source and README on GitHub</a>.</p>
        </footer>
        """;
}
