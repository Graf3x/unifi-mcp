// unifi-mcp setup page: runs the checks and lights the ports, shows the sign-in code, turns the guide into tabs.
// Everything from the server goes into the page as text (textContent), never as markup.
(() => {
  "use strict";
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const body = document.body;
  const guideNo = body.dataset.guide;

  const WORD = { ok: "Passed", warn: "Warning", fail: "Failed", skip: "Not used", blocked: "Waiting", checking: "Checking", idle: "Not checked yet" };

  // Guide headings and the checks that cover the step under them.
  const PILLS = {
    "before-you-start-two-values-from-your-console": ["certificate", "apikey"],
    "guide-1-quick-local-setup": ["console", "certificate", "apikey", "callers"],
    "step-2-console-values": ["console", "certificate", "apikey", "site"],
    "step-3-cloudflare-tunnel": ["tunnel"],
    "step-4-the-rest-of-env-then-start": ["connector"],
    "step-5-add-the-connector-in-claude": ["signin"],
    "step-1-a-google-oauth-client": ["signin"],
    "step-2-switch-the-server-to-google-sign-in": ["signin"],
    "step-4-optional-claude-code-over-tailscale": ["callers"],
  };

  async function call(path, payload) {
    const r = await fetch("/setup/api/" + path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload ?? {}),
      credentials: "same-origin",
      cache: "no-store",
    });
    if (r.status === 401) { location.reload(); throw new Error("locked"); }
    let data = null;
    try { data = await r.json(); } catch { /* not JSON */ }
    return { status: r.status, data };
  }

  function el(tag, props = {}, ...children) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (k === "text") e.textContent = v;
      else if (k === "class") e.className = v;
      else e.setAttribute(k, v);
    }
    for (const c of children) if (c) e.append(c);
    return e;
  }

  // ---- Copying ------------------------------------------------------------------------------------------------------

  async function copyText(text, button) {
    let done = false;
    try {
      if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); done = true; }
    } catch { /* fall back */ }
    if (!done) {
      const area = el("textarea", { readonly: "", "aria-hidden": "true" });
      area.value = text;
      area.style.position = "fixed"; area.style.opacity = "0";
      document.body.append(area);
      area.select();
      try { done = document.execCommand("copy"); } catch { done = false; }
      area.remove();
    }
    if (button) {
      const was = button.dataset.label || button.textContent;
      button.dataset.label = was;
      button.textContent = done ? "Copied" : "Select and copy";
      setTimeout(() => { button.textContent = was; }, 1400);
    }
  }

  function copyButton(getText, label = "Copy") {
    const b = el("button", { type: "button", class: "copy", text: label });
    b.addEventListener("click", () => copyText(getText(), b));
    return b;
  }

  for (const pre of $$("pre")) {
    const code = $("code", pre) || pre;
    pre.classList.add("has-copy");
    pre.append(copyButton(() => code.textContent.replace(/\n$/, "")));
  }

  if (!$("#results")) return; // the locked page

  // ---- Guide tabs ---------------------------------------------------------------------------------------------------

  const parts = $$(".part").filter(p => p.dataset.part !== "intro");
  const tablist = $(".tabs");
  const tabs = [];

  function partOf(node) { return node && node.closest ? node.closest(".part") : null; }

  function select(part, focus = false) {
    for (const p of parts) p.hidden = p !== part;
    for (const t of tabs) {
      const on = t.dataset.part === part.dataset.part;
      t.setAttribute("aria-selected", on ? "true" : "false");
      t.tabIndex = on ? 0 : -1;
      if (on && focus) t.focus();
    }
  }

  if (tablist && parts.length > 1) {
    for (const p of parts) {
      const id = p.dataset.part;
      const [head, ...rest] = p.dataset.title.split(":");
      const isGuide = /^guide-\d/.test(id);
      const t = el("button", { type: "button", class: "tab", role: "tab", id: "tab-" + id, "aria-controls": "part-" + id, "data-part": id });
      t.append(isGuide ? head : p.dataset.title.replace(/:.*$/, ""));
      if (id.startsWith("guide-" + guideNo + "-")) t.append(el("span", { class: "yours", text: "Yours" }));
      if (isGuide && rest.length) t.append(el("small", { text: rest.join(":").trim() }));
      p.id = "part-" + id;
      p.setAttribute("role", "tabpanel");
      p.setAttribute("aria-labelledby", t.id);
      t.addEventListener("click", () => select(p));
      t.addEventListener("keydown", e => {
        const i = tabs.indexOf(t);
        const next = e.key === "ArrowRight" ? i + 1 : e.key === "ArrowLeft" ? i - 1 : e.key === "Home" ? 0 : e.key === "End" ? tabs.length - 1 : null;
        if (next === null) return;
        e.preventDefault();
        const target = tabs[(next + tabs.length) % tabs.length];
        select(parts.find(x => x.dataset.part === target.dataset.part), true);
      });
      tabs.push(t);
      tablist.append(t);
    }
    tablist.hidden = false;
    const fromHash = location.hash && document.getElementById(decodeURIComponent(location.hash.slice(1)));
    const start = partOf(fromHash) || parts.find(p => p.dataset.part.startsWith("guide-" + guideNo + "-")) || parts[0];
    select(start);
    if (fromHash && partOf(fromHash)) requestAnimationFrame(() => fromHash.scrollIntoView());
  }

  // In-page links into a hidden tab open that tab first.
  document.addEventListener("click", e => {
    const a = e.target.closest && e.target.closest('a[href^="#"]');
    if (!a) return;
    const target = document.getElementById(decodeURIComponent(a.getAttribute("href").slice(1)));
    const part = partOf(target);
    if (!target || !part || !part.hidden) return;
    e.preventDefault();
    select(part);
    target.scrollIntoView({ block: "start" });
    history.replaceState(null, "", a.getAttribute("href"));
  });

  // ---- Checks -------------------------------------------------------------------------------------------------------

  const ids = $$("#results .result").map(li => li.dataset.check);
  const labels = Object.fromEntries($$("#results .result").map(li => [li.dataset.check, $(".result-name", li).textContent]));

  for (const [heading, checks] of Object.entries(PILLS)) {
    const h = document.getElementById(heading);
    if (!h) continue;
    const span = el("span", { class: "pills" });
    for (const id of checks.filter(c => ids.includes(c))) {
      span.append(el("a", { class: "pill", href: "#check-" + id, "data-check": id, "data-state": "idle", text: labels[id] }), " ");
    }
    h.append(span);
  }

  function headingText(id) {
    const h = document.getElementById(id);
    if (!h) return null;
    const copy = h.cloneNode(true);
    for (const p of $$(".pills", copy)) p.remove();
    return copy.textContent.trim();
  }

  function show(id, state, result) {
    for (const node of $$(`[data-check="${id}"]`)) node.dataset.state = state;
    const port = $(`.port[data-check="${id}"] .port-state`);
    const li = $(`#check-${id}`);
    const summary = result?.summary ?? WORD[state];
    if (port) port.textContent = `${WORD[state]}. ${summary}`;
    for (const pill of $$(`.pill[data-check="${id}"]`)) pill.title = `${WORD[state]}: ${summary}`;
    if (!li) return;

    const name = $(".result-name", li);
    let word = $(".word", name);
    if (!word) { word = el("span", { class: "word" }); name.append(word); }
    word.textContent = WORD[state];
    $(".result-summary", li).textContent = summary;

    const more = $(".result-more", li);
    more.replaceChildren();
    if (!result) return;
    if (result.detail) more.append(el("p", { text: result.detail }));
    if (result.copy && result.copy.length) {
      const dl = el("dl", { class: "values" });
      for (const v of result.copy) {
        const code = el("code", { text: v.value });
        dl.append(el("div", {}, el("dt", { text: v.label }), el("dd", {}, code, copyButton(() => v.value))));
      }
      more.append(dl);
    }
    if (result.anchor && state !== "ok" && state !== "skip") {
      const title = headingText(result.anchor);
      if (title) more.append(el("a", { class: "fix", href: "#" + result.anchor, text: "In the guide: " + title }));
    }
  }

  const rerun = $("#rerun");
  const tally = $("#tally");

  function count() {
    const n = { ok: 0, fail: 0, warn: 0, blocked: 0, skip: 0 };
    for (const id of ids) { const s = $(`#check-${id}`).dataset.state; if (s in n) n[s]++; }
    const parts = [];
    if (n.ok) parts.push(`${n.ok} passed`);
    if (n.fail) parts.push(`${n.fail} failed`);
    if (n.warn) parts.push(`${n.warn} with a warning`);
    if (n.blocked) parts.push(`${n.blocked} waiting on another`);
    if (n.skip) parts.push(`${n.skip} not used`);
    tally.textContent = parts.join(", ") + ".";
  }

  async function runAll() {
    rerun.disabled = true;
    tally.textContent = "Checking…";
    await Promise.allSettled(ids.map(async id => {
      show(id, "checking");
      try {
        const r = await call("check/" + id);
        if (r.status === 200 && r.data) show(id, String(r.data.status).toLowerCase(), r.data);
        else show(id, "fail", { summary: `The check itself failed (HTTP ${r.status}).` });
      } catch (e) {
        if (e.message !== "locked") show(id, "fail", { summary: "The server didn't answer. Is it still running?" });
      }
    }));
    rerun.disabled = false;
    count();
  }

  rerun.addEventListener("click", runAll);
  runAll();

  // ---- Sign-in code -------------------------------------------------------------------------------------------------

  if (body.dataset.codes !== "on") return;

  const lcd = $("#code");
  const digits = $("#code-digits");
  const line = $("#code-line");
  const meter = $("#code-meter");
  const copyCode = $("#code-copy");
  const getNew = $("#code-new");
  const LIFETIME = 10 * 60 * 1000;
  const EMPTY = "----- -----";
  let live = null; // { code, expires }
  let ticker = 0;

  function idle(message) {
    live = null;
    clearInterval(ticker);
    lcd.classList.remove("live");
    digits.textContent = EMPTY;
    line.textContent = message;
    meter.style.width = "0";
    copyCode.disabled = true;
  }

  function tick() {
    if (!live) return;
    const left = live.expires - Date.now();
    if (left <= 0) { idle("Expired. A new one shows here when Claude opens the sign-in page again."); return; }
    const m = Math.floor(left / 60000), s = Math.floor(left / 1000) % 60;
    line.textContent = `Works once, for ${m}:${String(s).padStart(2, "0")} more.`;
    meter.style.width = `${Math.max(0, Math.min(100, (left / LIFETIME) * 100))}%`;
  }

  function showCode(answer) {
    if (!answer || !answer.code) return false;
    const expires = Date.parse(answer.expiresAt);
    if (live && live.code === answer.code) return true;
    live = { code: answer.code, expires };
    lcd.classList.add("live");
    digits.textContent = answer.code;
    copyCode.disabled = false;
    clearInterval(ticker);
    ticker = setInterval(tick, 1000);
    tick();
    return true;
  }

  async function peek() {
    if (document.visibilityState !== "visible") return;
    try {
      const r = await call("code", { new: false });
      if (r.status !== 200) { idle(r.data?.error ?? "Codes aren't available."); clearInterval(poller); getNew.disabled = true; return; }
      if (!showCode(r.data) && live) idle("That code is gone: it was used, or thrown away after 5 wrong tries.");
    } catch { /* next round */ }
  }

  copyCode.addEventListener("click", () => live && copyText(live.code, copyCode));
  getNew.addEventListener("click", async () => {
    getNew.disabled = true;
    try {
      const r = await call("code", { new: true });
      if (r.status === 200) showCode(r.data);
      else line.textContent = r.data?.error ?? `Couldn't get a code (HTTP ${r.status}).`;
    } finally {
      getNew.disabled = false;
    }
  });

  // ---- Cables: when the port banks wrap onto two rows, the upper bank's cords stay short ----
  const ports = document.querySelector(".ports");
  if (ports) {
    const stack = () => {
      const banks = ports.querySelectorAll(".bank");
      const stacked = banks.length > 1 && banks[banks.length - 1].offsetTop > banks[0].offsetTop + 4;
      ports.classList.toggle("stacked", stacked);
    };
    new ResizeObserver(stack).observe(ports);
    stack();
  }

  const poller = setInterval(peek, 3000);
  document.addEventListener("visibilitychange", peek);
  peek();
})();
