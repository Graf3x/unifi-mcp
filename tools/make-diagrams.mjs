// Writes the README's setup diagrams to docs/images, a light and a dark file each (the README picks one with <picture>).
// Run from the repo root: node tools/make-diagrams.mjs
// Visual language: links are drawn as patch cables in wiring-pair colours, one colour per way in. A striped cable is
// optional (only the full setup uses it).
import { writeFileSync, mkdirSync } from "node:fs";

const themes = {
  light: { box: "#F6F8FA", line: "#D0D7DE", ink: "#1F2328", muted: "#59636E", region: "#8C959F", orange: "#C4520A",
           blue: "#0969DA", green: "#1A7F37", brown: "#9A6700", cable: "#6E7781", stripe: "#FFFFFF", onAccent: "#FFFFFF" },
  dark:  { box: "#161B22", line: "#3D444D", ink: "#F0F6FC", muted: "#9198A1", region: "#656C76", orange: "#F0883E",
           blue: "#4493F8", green: "#3FB950", brown: "#D29922", cable: "#9198A1", stripe: "#0D1117", onAccent: "#0D1117" },
};

const SANS = `-apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans", Helvetica, Arial, sans-serif`;
const MONO = `ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace`;
const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function svg(w, h, title, body, c) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" role="img" aria-label="${esc(title)}">
<title>${esc(title)}</title>
<style>
  text { font-family: ${SANS}; }
  .t { font-size: 15px; font-weight: 600; fill: ${c.ink}; }
  .s { font-size: 12.5px; fill: ${c.muted}; }
  .m { font-family: ${MONO}; font-size: 12px; fill: ${c.ink}; }
  .mm { font-family: ${MONO}; font-size: 12px; fill: ${c.muted}; }
  .l { font-size: 13px; font-weight: 600; fill: ${c.muted}; }
  .tag { font-size: 11.5px; font-weight: 600; }
  .n { font-size: 12px; font-weight: 700; fill: ${c.onAccent}; }
  .halo { paint-order: stroke; stroke: ${c.stripe}; stroke-width: 5px; stroke-linejoin: round; }
</style>
${body}
</svg>
`;
}

// Building blocks
const box = (c, x, y, w, h, title, sub, { mono = false, center = true } = {}) => {
  const tx = center ? x + w / 2 : x + 14, anchor = center ? "middle" : "start";
  const ty = sub ? y + h / 2 - 3 : y + h / 2 + 5;
  return `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="8" fill="${c.box}" stroke="${c.line}" stroke-width="1.5"/>
<text class="t" x="${tx}" y="${ty}" text-anchor="${anchor}">${esc(title)}</text>` +
    (sub ? `\n<text class="${mono ? "mm" : "s"}" x="${tx}" y="${ty + 19}" text-anchor="${anchor}">${esc(sub)}</text>` : "");
};
const cable = (c, d, color, striped = false) =>
  `<path d="${d}" fill="none" stroke="${color}" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>` +
  (striped ? `<path d="${d}" fill="none" stroke="${c.stripe}" stroke-width="2.2" stroke-dasharray="5 7" stroke-linecap="butt"/>` : "");
// An RJ45-style jack straddling a box edge at (x, y).
const jack = (c, x, y) =>
  `<rect x="${x - 7}" y="${y - 8}" width="14" height="16" rx="2" fill="${c.box}" stroke="${c.ink}" stroke-width="1.5"/>
<rect x="${x - 3}" y="${y + 3}" width="6" height="4" fill="${c.ink}"/>`;
const tag = (c, x, y, text, color) => {
  const w = Math.round(text.length * 6.7 + 18);
  return `<rect x="${x}" y="${y}" width="${w}" height="20" rx="10" fill="none" stroke="${color}" stroke-width="1.5"/>
<text class="tag" x="${x + w / 2}" y="${y + 14}" text-anchor="middle" fill="${color}">${esc(text)}</text>`;
};

// 1. The three ways in.
function paths(c) {
  const b = [];
  b.push(`<text class="l" x="20" y="40">Internet</text>`);
  b.push(`<rect x="388" y="16" width="596" height="438" rx="14" fill="none" stroke="${c.region}" stroke-width="1.5" stroke-dasharray="6 5"/>`);
  b.push(`<text class="l" x="406" y="40">Your network</text>`);
  b.push(`<rect x="408" y="56" width="420" height="384" rx="10" fill="none" stroke="${c.line}" stroke-width="1.5"/>`);
  b.push(`<text class="s" x="426" y="80">The machine that runs unifi-mcp</text>`);

  // Cables first, so boxes and jacks sit on top of their ends.
  b.push(cable(c, "M170 132 H206", c.orange));
  b.push(cable(c, "M356 132 H428", c.orange));
  b.push(cable(c, "M578 132 H660", c.orange));
  b.push(cable(c, "M578 258 H660", c.blue));
  b.push(cable(c, "M170 384 H428", c.green, true));
  b.push(cable(c, "M578 384 H660", c.green, true));
  b.push(cable(c, "M810 258 H858", c.cable));
  b.push(`<text class="s halo" x="392" y="120" text-anchor="middle">tunnel</text>`);
  b.push(`<text class="s" x="286" y="372" text-anchor="middle">Tailscale, HTTPS :7420</text>`);

  b.push(tag(c, 20, 66, "Guides 2 and 3", c.orange));
  b.push(box(c, 20, 96, 150, 72, "Claude", "web, desktop, phone"));
  b.push(box(c, 206, 96, 150, 72, "Cloudflare", "unifi.example.com", { mono: true }));
  b.push(box(c, 428, 96, 150, 72, "cloudflared", "tunnel container"));
  b.push(tag(c, 428, 192, "Guide 1", c.blue));
  b.push(box(c, 428, 222, 150, 72, "Claude Code", "on this machine"));
  b.push(tag(c, 20, 318, "Guide 3, optional", c.green));
  b.push(box(c, 20, 348, 150, 72, "Claude Code", "on your tailnet"));
  b.push(box(c, 428, 348, 150, 72, "tailscale serve", "on the host"));

  b.push(`<rect x="660" y="96" width="150" height="324" rx="8" fill="${c.box}" stroke="${c.line}" stroke-width="1.5"/>`);
  b.push(`<text class="t" x="735" y="192" text-anchor="middle" style="font-size:16px">unifi-mcp</text>`);
  b.push(`<text class="s" x="735" y="212" text-anchor="middle">Docker container</text>`);
  b.push(`<text class="s" x="735" y="228" text-anchor="middle">or dotnet run</text>`);
  for (const [y, label] of [[132, "public.sock"], [258, "127.0.0.1:5199"], [384, "mcp.sock"]]) {
    b.push(jack(c, 660, y));
    b.push(`<text class="m" x="676" y="${y + 4}">${label}</text>`);
  }
  b.push(box(c, 858, 218, 114, 80, "UniFi console", "192.168.1.1", { mono: true }));
  b.push(`<text class="s" x="915" y="320" text-anchor="middle">pinned TLS</text>`);
  b.push(`<text class="s" x="915" y="336" text-anchor="middle">and API key</text>`);
  return svg(1000, 470, "The three ways Claude reaches unifi-mcp: locally, through a Cloudflare tunnel, or over Tailscale", b.join("\n"), c);
}

// 2. Where each .env value comes from.
function envValues(c) {
  const vars = [
    ["PUID", "=1000", "shell"], ["PGID", "=1000", "shell"],
    ["UNIFI_URL", "=https://192.168.1.1", "unifi"], ["UNIFI_API_KEY", "", "unifi"],
    ["UNIFI_CERT_SHA256", "", "cert"], ["UNIFI_ALLOW_WRITES", "=false", null],
    ["MCP_PUBLIC_BASE_URL", "=https://unifi.example.com", "cf"], ["MCP_SIGNIN", "=code", "signin"],
    ["GOOGLE_CLIENT_ID", "", "google"], ["GOOGLE_CLIENT_SECRET", "", "google"], ["MCP_ALLOWED_GOOGLE_EMAILS", "", "google"],
    ["UNIFI_MCP_DATA_KEY", "", "random"], ["TUNNEL_TOKEN", "", "cf"],
  ];
  const sources = [
    ["shell", "Your shell", "id -u and id -g", true],
    ["unifi", "UniFi Network", "Settings > Control Plane > Integrations", false],
    ["cert", "Console certificate", "openssl, then confirmed over SSH", false],
    ["cf", "Cloudflare Zero Trust", "Networks > Tunnels, and your hostname", false],
    ["signin", "How you sign in", "code in guide 2, google in guide 3", false],
    ["google", "Google Cloud Console", "guide 3 only", false],
    ["random", "A random key you keep", "openssl rand -base64 32", true],
  ];
  const b = [];
  const panelX = 600, firstY = 96, step = 26;
  const varY = i => firstY + i * step;
  const srcY = {}, top = 48, bh = 48, gap = 7;
  sources.forEach(([id], i) => { srcY[id] = top + i * (bh + gap) + bh / 2; });

  // Cables
  vars.forEach(([, , src], i) => {
    if (!src) return;
    const y1 = srcY[src], y2 = varY(i) - 4, x1 = 330, x2 = panelX + 22;
    b.push(cable(c, `M${x1} ${y1} C ${x1 + 150} ${y1}, ${x2 - 150} ${y2}, ${x2} ${y2}`, c.brown, src === "google"));
  });
  sources.forEach(([id, title, sub, mono], i) => {
    const y = top + i * (bh + gap);
    b.push(`<rect x="20" y="${y}" width="310" height="${bh}" rx="8" fill="${c.box}" stroke="${c.line}" stroke-width="1.5"/>`);
    b.push(`<text class="t" x="34" y="${y + 20}" style="font-size:14px">${esc(title)}</text>`);
    b.push(`<text class="${mono ? "mm" : "s"}" x="34" y="${y + 37}">${esc(sub)}</text>`);
  });
  // The .env file
  b.push(`<rect x="${panelX}" y="44" width="380" height="${varY(vars.length - 1) + 22 - 44}" rx="8" fill="${c.box}" stroke="${c.line}" stroke-width="1.5"/>`);
  b.push(`<path d="M${panelX} 72 H${panelX + 380}" stroke="${c.line}" stroke-width="1.5"/>`);
  b.push(`<text class="m" x="${panelX + 16}" y="63" style="font-weight:700">.env</text>`);
  vars.forEach(([name, value, src], i) => {
    const y = varY(i);
    if (src) b.push(`<circle cx="${panelX + 22}" cy="${y - 4}" r="4" fill="${c.brown}"/>`);
    b.push(`<text class="m" x="${panelX + 34}" y="${y}">${name}<tspan fill="${c.muted}">${esc(value || "=")}</tspan></text>`);
  });
  return svg(1000, varY(vars.length - 1) + 40, "Where each value in .env comes from", b.join("\n"), c);
}

// 3. Code sign-in, as a sequence.
function codeSignIn(c) {
  const lanes = [[130, "Claude", "web, desktop or phone"], [380, "Your browser", "on any device"],
                 [630, "unifi-mcp", "behind the tunnel"], [870, "Your terminal", "where Docker runs"]];
  const b = [];
  for (const [x] of lanes)
    b.push(`<path d="M${x} 72 V500" stroke="${c.region}" stroke-width="1.5" stroke-dasharray="4 5"/>`);
  for (const [x, t, s] of lanes) b.push(box(c, x - 95, 16, 190, 56, t, s));
  b.push(`<defs><marker id="a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="${c.ink}"/></marker></defs>`);

  const msg = (n, from, to, y, lines, dashed = false) => {
    const mid = (from + to) / 2, dir = to > from ? 1 : -1;
    b.push(`<path d="M${from + dir * 4} ${y} H${to - dir * 6}" stroke="${c.ink}" stroke-width="1.6"${dashed ? ' stroke-dasharray="6 5"' : ""} marker-end="url(#a)"/>`);
    lines.forEach((l, i) => {
      const ly = y - 10 - (lines.length - 1 - i) * 17;
      b.push(typeof l === "string"
        ? `<text class="s halo" x="${mid}" y="${ly}" text-anchor="middle" style="fill:${c.ink}">${esc(l)}</text>`
        : `<text class="m halo" x="${mid}" y="${ly}" text-anchor="middle">${esc(l.mono)}</text>`);
    });
    b.push(`<circle cx="${from + dir * 18}" cy="${y}" r="10" fill="${c.orange}"/><text class="n" x="${from + dir * 18}" y="${y + 4}" text-anchor="middle">${n}</text>`);
  };
  msg(1, 130, 630, 118, ["Add custom connector", { mono: "https://unifi.example.com/mcp" }]);
  msg(2, 130, 380, 172, ["Opens the sign-in page"]);
  msg(3, 380, 630, 226, ["Loads the page"]);
  msg(4, 630, 870, 280, ["Writes a one-time code to its log"]);
  b.push(`<rect x="760" y="294" width="220" height="30" rx="6" fill="${c.box}" stroke="${c.line}" stroke-width="1.5"/>`);
  b.push(`<text class="m" x="870" y="314" text-anchor="middle">Sign-in code 01234-56789</text>`);
  msg(5, 870, 380, 382, ["You copy it from", { mono: "docker compose logs unifi-mcp" }], true);
  msg(6, 380, 630, 436, ["Enter the code, approve"]);
  msg(7, 630, 130, 490, ["Connected: Claude can call the tools"]);
  return svg(1000, 512, "Signing in with a one-time code from the server's log", b.join("\n"), c);
}

mkdirSync("docs/images", { recursive: true });
for (const [name, draw] of [["setup-paths", paths], ["env-values", envValues], ["code-sign-in", codeSignIn]])
  for (const [theme, c] of Object.entries(themes))
    writeFileSync(`docs/images/${name}-${theme}.svg`, draw(c));
console.log("wrote docs/images");
