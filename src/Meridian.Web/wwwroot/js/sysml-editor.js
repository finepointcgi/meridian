// SysML v2 text editor: a native <textarea> (keeps browser undo, IME, selection, accessibility) over a
// syntax-highlighted layer, with line numbers and inline error markers. No dependencies, works offline.

const KEYWORDS = new Set(("about abstract accept action actor alias all allocate allocation analysis and as assert assign assume attribute " +
  "bind by calc case comment concern connect connection constant constraint decide def default defined dependency derived do doc done " +
  "else end entry enum event exhibit exit expose false filter first flow for fork frame from if import in include individual inout " +
  "interface item join library locale loop merge message metadata nonunique not null objective occurrence of or ordered out package " +
  "parallel part perform port private protected public readonly redefines ref references rendering require requirement return " +
  "satisfy send snapshot specializes stakeholder standard start state subject subsets succession then timeslice to transition true " +
  "typed until use variant variation verification verify via view viewpoint while xor").split(" "));
const DEF_KEYWORDS = new Set(["def", "package", "part", "port", "attribute", "item", "action", "state", "requirement", "enum", "constraint",
  "connection", "interface", "occurrence", "verification", "case", "transition", "message", "flow", "subject", "ref", "decide", "fork", "join", "merge"]);

const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function highlight(text) {
  const out = [];
  const re = /(\/\/\*[\s\S]*?(?:\*\/|$))|(\/\/[^\n]*)|(\/\*[\s\S]*?(?:\*\/|$))|('(?:[^'\\\n]|\\.)*'?)|("(?:[^"\\\n]|\\.)*"?)|(\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|([A-Za-z_][A-Za-z0-9_]*)|(:>>|::>|:>|::|:=|\.\.|[{}\[\];:=<>~#@,.])|(\s+|[^\sA-Za-z0-9_])/g;
  let m, prevWord = null, expectName = false;
  while ((m = re.exec(text)) !== null) {
    const t = m[0];
    if (m[1] || m[2]) out.push(`<span class="c">${esc(t)}</span>`);
    else if (m[3]) out.push(`<span class="${/^\/\*\s*@id\s/.test(t) ? "id" : "c doc"}">${esc(t)}</span>`);
    else if (m[4] || m[7]) {
      const kw = m[7] && KEYWORDS.has(t);
      if (kw) { out.push(`<span class="k">${t}</span>`); expectName = DEF_KEYWORDS.has(t); prevWord = t; continue; }
      out.push(expectName ? `<span class="n">${esc(t)}</span>` : `<span class="r">${esc(t)}</span>`);
      expectName = false; prevWord = t; continue;
    }
    else if (m[5]) out.push(`<span class="s">${esc(t)}</span>`);
    else if (m[6]) out.push(`<span class="num">${t}</span>`);
    else if (m[8]) { out.push(`<span class="p">${esc(t)}</span>`); if (t !== "<" && t !== ">") expectName = false; continue; }
    else out.push(esc(t));
    if (!/^\s+$/.test(t)) expectName = false;
  }
  return out.join("") + "\n ";
}

function lineCol(text, offset) {
  let line = 1, last = -1;
  for (let i = 0; i < offset && i < text.length; i++) if (text.charCodeAt(i) === 10) { line++; last = i; }
  return { line, col: offset - last };
}

export function create(host, dotnet, text, readOnly) {
  host.innerHTML = `
    <div class="sx-gutter"><div class="sx-gutter-inner"></div></div>
    <div class="sx-main">
      <div class="sx-layer"><pre class="sx-hl" aria-hidden="true"></pre><div class="sx-marks"></div><div class="sx-flash"></div><div class="sx-measure">MMMMMMMMMM</div></div>
      <textarea class="sx-ta" spellcheck="false" autocapitalize="off" autocomplete="off" autocorrect="off" wrap="off" aria-label="SysML v2 text"></textarea>
    </div>`;
  const ed = {
    dotnet, ta: host.querySelector(".sx-ta"), hl: host.querySelector(".sx-hl"), layer: host.querySelector(".sx-layer"),
    marks: host.querySelector(".sx-marks"), flash: host.querySelector(".sx-flash"), gutter: host.querySelector(".sx-gutter-inner"),
    diags: [], timer: 0, raf: 0, lines: 0,
  };
  host._sx = ed;
  ed.ta.value = text || "";
  ed.ta.readOnly = !!readOnly;

  const metrics = () => {
    const meas = host.querySelector(".sx-measure");
    ed.cw = meas.getBoundingClientRect().width / 10 || 7.2;
    ed.lh = parseFloat(getComputedStyle(ed.hl).lineHeight) || 19;
    const cs = getComputedStyle(ed.ta);
    ed.padTop = parseFloat(cs.paddingTop) || 0;
    ed.padLeft = parseFloat(cs.paddingLeft) || 0;
  };
  ed.metrics = metrics;

  const sync = () => {
    ed.layer.style.transform = `translate(${-ed.ta.scrollLeft}px, ${-ed.ta.scrollTop}px)`;
    ed.gutter.style.transform = `translateY(${-ed.ta.scrollTop}px)`;
  };
  ed.render = () => {
    ed.raf = 0;
    const v = ed.ta.value;
    ed.hl.innerHTML = highlight(v);
    const n = v.split("\n").length;
    if (n !== ed.lines) { ed.lines = n; renderGutter(ed); }
    renderMarks(ed);
    sync();
  };
  const schedule = () => { if (!ed.raf) ed.raf = requestAnimationFrame(ed.render); };

  ed.ta.addEventListener("scroll", sync);
  ed.ta.addEventListener("input", () => {
    schedule();
    clearTimeout(ed.timer);
    ed.timer = setTimeout(() => dotnet.invokeMethodAsync("OnEdit", ed.ta.value), 280);
  });
  ed.ta.addEventListener("keydown", e => {
    if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
      e.preventDefault();
      clearTimeout(ed.timer);
      dotnet.invokeMethodAsync("OnApplyShortcut", ed.ta.value);
      return;
    }
    if (ed.ta.readOnly) return;
    if (e.key === "Tab" && !e.metaKey && !e.ctrlKey && !e.altKey) {
      e.preventDefault();
      if (e.shiftKey) { outdent(ed); return; }
      document.execCommand("insertText", false, "    ");
      return;
    }
    if (e.key === "Enter" && !e.shiftKey && !e.altKey) {
      const v = ed.ta.value, at = ed.ta.selectionStart;
      const lineStart = v.lastIndexOf("\n", at - 1) + 1;
      const line = v.slice(lineStart, at);
      let indent = /^\s*/.exec(line)[0];
      if (/\{\s*$/.test(line)) indent += "    ";
      e.preventDefault();
      document.execCommand("insertText", false, "\n" + indent);
      return;
    }
    if (e.key === "}" && !e.metaKey && !e.ctrlKey) {
      const v = ed.ta.value, at = ed.ta.selectionStart;
      const lineStart = v.lastIndexOf("\n", at - 1) + 1;
      if (at === ed.ta.selectionEnd && /^ {4,}$/.test(v.slice(lineStart, at))) {
        e.preventDefault();
        ed.ta.setSelectionRange(at - 4, at);
        document.execCommand("insertText", false, "}");
      }
    }
  });
  // Ctrl/Cmd-click on a name: go to the element it declares or references.
  ed.ta.addEventListener("mouseup", e => {
    if (!(e.metaKey || e.ctrlKey) || e.button !== 0) return;
    const at = ed.ta.selectionStart;
    setTimeout(() => dotnet.invokeMethodAsync("OnNavigate", at), 0);
  });
  ed.ta.addEventListener("mousemove", e => ed.ta.classList.toggle("sx-link", e.metaKey || e.ctrlKey));
  ed.ta.addEventListener("keyup", () => ed.ta.classList.remove("sx-link"));

  const ro = new ResizeObserver(() => { metrics(); renderMarks(ed); sync(); });
  ro.observe(host);
  ed.ro = ro;
  metrics();
  ed.render();
}

function outdent(ed) {
  const v = ed.ta.value, at = ed.ta.selectionStart;
  const lineStart = v.lastIndexOf("\n", at - 1) + 1;
  const m = /^ {1,4}/.exec(v.slice(lineStart));
  if (!m) return;
  ed.ta.setSelectionRange(lineStart, lineStart + m[0].length);
  document.execCommand("delete");
  const back = Math.max(lineStart, at - m[0].length);
  ed.ta.setSelectionRange(back, back);
}

function renderGutter(ed) {
  const byLine = new Map();
  for (const d of ed.diags) {
    const cur = byLine.get(d.line);
    if (!cur || d.sev < cur.sev) byLine.set(d.line, { sev: d.sev, msg: (cur ? cur.msg + "\n" : "") + d.msg });
    else cur.msg += "\n" + d.msg;
  }
  const parts = [];
  for (let i = 1; i <= ed.lines; i++) {
    const d = byLine.get(i);
    const cls = d ? (d.sev === 0 ? " err" : d.sev === 1 ? " warn" : " info") : "";
    parts.push(`<div class="sx-ln${cls}"${d ? ` title="${esc(d.msg).replace(/"/g, "&quot;")}"` : ""}>${i}</div>`);
  }
  ed.gutter.innerHTML = parts.join("");
}

function renderMarks(ed) {
  const lines = ed.ta.value.split("\n");
  const html = [];
  for (const d of ed.diags) {
    const text = lines[d.line - 1] ?? "";
    let col = Math.max(1, d.col);
    let len = Math.max(1, Math.min(d.len, Math.max(1, text.length - col + 1)));
    if (col > text.length) { col = text.length + 1; len = 1; }
    const visualCol = text.slice(0, col - 1).replace(/\t/g, "    ").length;
    const cls = d.sev === 0 ? "err" : d.sev === 1 ? "warn" : "info";
    html.push(`<div class="sx-mark ${cls}" style="top:${ed.padTop + (d.line - 1) * ed.lh}px;left:${ed.padLeft + visualCol * ed.cw}px;width:${len * ed.cw}px;height:${ed.lh}px"></div>`);
  }
  ed.marks.innerHTML = html.join("");
}

export function setText(host, text, keepView) {
  const ed = host?._sx; if (!ed) return;
  if (ed.ta.value === text) return;
  const top = ed.ta.scrollTop, left = ed.ta.scrollLeft, sel = ed.ta.selectionStart;
  const caretLine = lineCol(ed.ta.value, sel).line;
  ed.ta.value = text;
  if (keepView) {
    const lines = text.split("\n");
    let off = 0;
    for (let i = 0; i < Math.min(caretLine - 1, lines.length - 1); i++) off += lines[i].length + 1;
    ed.ta.setSelectionRange(off, off);
    ed.ta.scrollTop = top; ed.ta.scrollLeft = left;
  } else { ed.ta.scrollTop = 0; ed.ta.setSelectionRange(0, 0); }
  ed.render();
}

export function isMac() { return /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent); }

export function getText(host) { return host?._sx?.ta.value ?? ""; }

export function setReadOnly(host, ro) { const ed = host?._sx; if (ed) ed.ta.readOnly = !!ro; }

export function setDiagnostics(host, diags) {
  const ed = host?._sx; if (!ed) return;
  ed.diags = diags || [];
  renderGutter(ed);
  renderMarks(ed);
}

/** Selects [start, end), scrolls it to the middle of the view and flashes the line. */
export function reveal(host, start, end, focus) {
  const ed = host?._sx; if (!ed) return;
  const { line } = lineCol(ed.ta.value, start);
  ed.metrics();
  const target = ed.padTop + (line - 1) * ed.lh - ed.ta.clientHeight / 2 + ed.lh;
  ed.ta.scrollTop = Math.max(0, target);
  ed.ta.scrollLeft = 0;
  if (focus) ed.ta.focus({ preventScroll: true });
  ed.ta.setSelectionRange(start, end);
  ed.flash.style.top = `${ed.padTop + (line - 1) * ed.lh}px`;
  ed.flash.style.height = `${ed.lh}px`;
  ed.flash.classList.remove("on"); void ed.flash.offsetWidth; ed.flash.classList.add("on");
}

export function revealLine(host, line, col) {
  const ed = host?._sx; if (!ed) return;
  const lines = ed.ta.value.split("\n");
  let off = 0;
  for (let i = 0; i < Math.min(line - 1, lines.length); i++) off += lines[i].length + 1;
  off += Math.max(0, col - 1);
  reveal(host, off, off, true);
}

export function dispose(host) {
  const ed = host?._sx; if (!ed) return;
  ed.ro?.disconnect();
  clearTimeout(ed.timer);
  delete host._sx;
}
