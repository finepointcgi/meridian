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

/**
 * Creates the editor. With `shared` ({ revision }) the text is a shared document: local edits become operations sent to the
 * server (one in flight at a time, later ones buffered), remote operations are transformed against pending local ones, and
 * other editors' carets and selections are drawn in their colours.
 */
export function create(host, dotnet, text, readOnly, shared) {
  host.innerHTML = `
    <div class="sx-gutter"><div class="sx-gutter-inner"></div></div>
    <div class="sx-main">
      <div class="sx-layer"><pre class="sx-hl" aria-hidden="true"></pre><div class="sx-marks"></div><div class="sx-cursors"></div><div class="sx-flash"></div><div class="sx-measure">MMMMMMMMMM</div></div>
      <textarea class="sx-ta" spellcheck="false" autocapitalize="off" autocomplete="off" autocorrect="off" wrap="off" aria-label="SysML v2 text"></textarea>
    </div>`;
  const ed = {
    dotnet, ta: host.querySelector(".sx-ta"), hl: host.querySelector(".sx-hl"), layer: host.querySelector(".sx-layer"),
    marks: host.querySelector(".sx-marks"), flash: host.querySelector(".sx-flash"), gutter: host.querySelector(".sx-gutter-inner"),
    diags: [], timer: 0, raf: 0, lines: 0, cursorsLayer: host.querySelector(".sx-cursors"),
    shared: shared ? newShared(text, shared.revision) : null,
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
    renderCursors(ed);
    sync();
  };
  const schedule = () => { if (!ed.raf) ed.raf = requestAnimationFrame(ed.render); };

  ed.ta.addEventListener("scroll", sync);
  ed.ta.addEventListener("input", () => {
    if (ed.shared) localEdit(ed);
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

  if (ed.shared) {
    const onSel = () => scheduleCursor(ed);
    for (const ev of ["select", "keyup", "mouseup", "focus"]) ed.ta.addEventListener(ev, onSel);
    document.addEventListener("selectionchange", ed.onDocSel = () => { if (document.activeElement === ed.ta) onSel(); });
  }

  const ro = new ResizeObserver(() => { metrics(); renderMarks(ed); renderCursors(ed); sync(); });
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
  if (ed.shared) clearTimeout(ed.shared.cursorTimer);
  if (ed.onDocSel) document.removeEventListener("selectionchange", ed.onDocSel);
  delete host._sx;
}

// ================================================================== collaborative editing

// Operational transformation on plain text: the same algorithm as Meridian.Core.Collaboration.TextOperation (ot.js model).
// Components: positive int = retain, negative int = delete, string = insert.
const isRetain = o => typeof o === "number" && o > 0;
const isDelete = o => typeof o === "number" && o < 0;
const isInsert = o => typeof o === "string";

class TextOp {
  constructor() { this.ops = []; this.baseLength = 0; this.targetLength = 0; }
  static from(ops) {
    const op = new TextOp();
    for (const c of ops || []) { if (isInsert(c)) op.insert(c); else if (isRetain(c)) op.retain(c); else if (isDelete(c)) op.delete(c); }
    return op;
  }
  retain(n) {
    if (!n) return this;
    this.baseLength += n; this.targetLength += n;
    const o = this.ops, l = o.length;
    if (l && isRetain(o[l - 1])) o[l - 1] += n; else o.push(n);
    return this;
  }
  insert(s) {
    if (!s) return this;
    this.targetLength += s.length;
    const o = this.ops, l = o.length;
    if (l && isInsert(o[l - 1])) o[l - 1] += s;
    else if (l && isDelete(o[l - 1])) { if (l > 1 && isInsert(o[l - 2])) o[l - 2] += s; else { o.push(o[l - 1]); o[l - 1] = s; } }
    else o.push(s);
    return this;
  }
  delete(n) {
    if (!n) return this;
    if (n > 0) n = -n;
    this.baseLength -= n;
    const o = this.ops, l = o.length;
    if (l && isDelete(o[l - 1])) o[l - 1] += n; else o.push(n);
    return this;
  }
  isNoop() { return this.ops.length === 0 || (this.ops.length === 1 && isRetain(this.ops[0])); }
  apply(s) {
    if (s.length !== this.baseLength) throw new Error("base length mismatch");
    const out = []; let i = 0;
    for (const o of this.ops) {
      if (isRetain(o)) { out.push(s.slice(i, i + o)); i += o; }
      else if (isInsert(o)) out.push(o);
      else i -= o;
    }
    return out.join("");
  }
  compose(b) {
    if (this.targetLength !== b.baseLength) throw new Error("compose: length mismatch");
    const r = new TextOp(), o1s = this.ops, o2s = b.ops;
    let i1 = 0, i2 = 0, o1 = o1s[i1++], o2 = o2s[i2++];
    while (o1 !== undefined || o2 !== undefined) {
      if (isDelete(o1)) { r.delete(o1); o1 = o1s[i1++]; continue; }
      if (isInsert(o2)) { r.insert(o2); o2 = o2s[i2++]; continue; }
      if (o1 === undefined || o2 === undefined) throw new Error("compose: operations don't line up");
      if (isRetain(o1) && isRetain(o2)) {
        if (o1 > o2) { r.retain(o2); o1 -= o2; o2 = o2s[i2++]; }
        else if (o1 === o2) { r.retain(o1); o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { r.retain(o1); o2 -= o1; o1 = o1s[i1++]; }
      } else if (isInsert(o1) && isDelete(o2)) {
        if (o1.length > -o2) { o1 = o1.slice(-o2); o2 = o2s[i2++]; }
        else if (o1.length === -o2) { o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { o2 += o1.length; o1 = o1s[i1++]; }
      } else if (isInsert(o1) && isRetain(o2)) {
        if (o1.length > o2) { r.insert(o1.slice(0, o2)); o1 = o1.slice(o2); o2 = o2s[i2++]; }
        else if (o1.length === o2) { r.insert(o1); o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { r.insert(o1); o2 -= o1.length; o1 = o1s[i1++]; }
      } else if (isRetain(o1) && isDelete(o2)) {
        if (o1 > -o2) { r.delete(o2); o1 += o2; o2 = o2s[i2++]; }
        else if (o1 === -o2) { r.delete(o2); o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { r.delete(o1); o2 += o1; o1 = o1s[i1++]; }
      } else throw new Error("compose: invalid components");
    }
    return r;
  }
  static transform(a, b) {
    if (a.baseLength !== b.baseLength) throw new Error("transform: length mismatch");
    const ap = new TextOp(), bp = new TextOp(), o1s = a.ops, o2s = b.ops;
    let i1 = 0, i2 = 0, o1 = o1s[i1++], o2 = o2s[i2++];
    while (o1 !== undefined || o2 !== undefined) {
      if (isInsert(o1)) { ap.insert(o1); bp.retain(o1.length); o1 = o1s[i1++]; continue; }
      if (isInsert(o2)) { ap.retain(o2.length); bp.insert(o2); o2 = o2s[i2++]; continue; }
      if (o1 === undefined || o2 === undefined) throw new Error("transform: operations don't line up");
      let min;
      if (isRetain(o1) && isRetain(o2)) {
        if (o1 > o2) { min = o2; o1 -= o2; o2 = o2s[i2++]; }
        else if (o1 === o2) { min = o2; o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { min = o1; o2 -= o1; o1 = o1s[i1++]; }
        ap.retain(min); bp.retain(min);
      } else if (isDelete(o1) && isDelete(o2)) {
        if (-o1 > -o2) { o1 -= o2; o2 = o2s[i2++]; }
        else if (o1 === o2) { o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { o2 -= o1; o1 = o1s[i1++]; }
      } else if (isDelete(o1) && isRetain(o2)) {
        if (-o1 > o2) { min = o2; o1 += o2; o2 = o2s[i2++]; }
        else if (-o1 === o2) { min = o2; o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { min = -o1; o2 += o1; o1 = o1s[i1++]; }
        ap.delete(min);
      } else {
        if (o1 > -o2) { min = -o2; o1 += o2; o2 = o2s[i2++]; }
        else if (o1 === -o2) { min = o1; o1 = o1s[i1++]; o2 = o2s[i2++]; }
        else { min = o1; o2 += o1; o1 = o1s[i1++]; }
        bp.delete(min);
      }
    }
    return [ap, bp];
  }
  transformIndex(index) {
    let newIndex = index;
    for (const o of this.ops) {
      if (isRetain(o)) index -= o;
      else if (isInsert(o)) newIndex += o.length;
      else { newIndex -= Math.min(index, -o); index += o; }
      if (index < 0) break;
    }
    return newIndex;
  }
}

function newShared(text, revision) {
  return { revision: revision || 0, outstanding: null, buffer: null, prev: text || "", cursors: [], cursorTimer: 0, cursorPending: false, waiters: [] };
}

/** The operation turning `a` into `b` (common prefix and suffix retained). */
function diffOp(a, b) {
  let p = 0; const max = Math.min(a.length, b.length);
  while (p < max && a.charCodeAt(p) === b.charCodeAt(p)) p++;
  let q = 0;
  while (q < max - p && a.charCodeAt(a.length - 1 - q) === b.charCodeAt(b.length - 1 - q)) q++;
  return new TextOp().retain(p).delete(a.length - p - q).insert(b.slice(p, b.length - q)).retain(q);
}

function send(ed, op) {
  ed.dotnet.invokeMethodAsync("OnTextOp", ed.shared.revision, op.ops).catch(() => { });
}

/** Turns what the user just typed into an operation: send it, or buffer it while one is in flight. */
function localEdit(ed) {
  const s = ed.shared, v = ed.ta.value;
  if (v === s.prev) return;
  const op = diffOp(s.prev, v);
  s.prev = v;
  for (const c of s.cursors) { c.anchor = op.transformIndex(c.anchor); c.head = op.transformIndex(c.head); }
  if (!s.outstanding) { s.outstanding = op; send(ed, op); }
  else s.buffer = s.buffer ? s.buffer.compose(op) : op;
  scheduleCursor(ed);
}

function scheduleCursor(ed) {
  const s = ed.shared; if (!s) return;
  clearTimeout(s.cursorTimer);
  s.cursorTimer = setTimeout(() => sendCursor(ed), 60);
}

function sendCursor(ed) {
  const s = ed.shared;
  if (s.outstanding) { s.cursorPending = true; return; }   // positions are only meaningful against an acknowledged revision
  s.cursorPending = false;
  const ta = ed.ta, back = ta.selectionDirection === "backward";
  const anchor = back ? ta.selectionEnd : ta.selectionStart, head = back ? ta.selectionStart : ta.selectionEnd;
  if (s.lastSent && s.lastSent[0] === s.revision && s.lastSent[1] === anchor && s.lastSent[2] === head) return;
  s.lastSent = [s.revision, anchor, head];
  ed.dotnet.invokeMethodAsync("OnCursor", s.revision, anchor, head).catch(() => { });
}

/** Applies an operation to the textarea, keeping the local selection and scroll position. */
function applyToTextarea(ed, op) {
  const ta = ed.ta;
  const start = op.transformIndex(ta.selectionStart), end = op.transformIndex(ta.selectionEnd), dir = ta.selectionDirection;
  const top = ta.scrollTop, left = ta.scrollLeft, focused = document.activeElement === ta;
  ta.value = op.apply(ta.value);
  if (focused) ta.setSelectionRange(start, end, dir); else { ta.selectionStart = start; ta.selectionEnd = end; }
  ta.scrollTop = top; ta.scrollLeft = left;
}

/** The server applied our outstanding operation. */
export function ack(host) {
  const ed = host?._sx, s = ed?.shared; if (!s || !s.outstanding) return;
  s.revision++;
  if (s.buffer) { s.outstanding = s.buffer; s.buffer = null; send(ed, s.outstanding); }
  else {
    s.outstanding = null;
    if (s.cursorPending) sendCursor(ed);
    const w = s.waiters.splice(0); for (const f of w) f();
  }
}

/** Someone else's operation (in server order). */
export function remoteOp(host, ops) {
  const ed = host?._sx, s = ed?.shared; if (!s) return;
  if (ed.ta.value !== s.prev) localEdit(ed);   // never lose a keystroke that hasn't been turned into an operation yet
  let op = TextOp.from(ops);
  s.revision++;
  try {
    if (s.outstanding) {
      [s.outstanding, op] = TextOp.transform(s.outstanding, op);
      if (s.buffer) [s.buffer, op] = TextOp.transform(s.buffer, op);
    }
    applyToTextarea(ed, op);
  } catch (e) {
    ed.dotnet.invokeMethodAsync("OnTextOutOfStep").catch(() => { });
    return;
  }
  s.prev = ed.ta.value;
  for (const c of s.cursors) { c.anchor = op.transformIndex(c.anchor); c.head = op.transformIndex(c.head); }
  ed.render();
  clearTimeout(ed.timer);
  ed.timer = setTimeout(() => ed.dotnet.invokeMethodAsync("OnEdit", ed.ta.value), 280);
}

/** Other editors' carets / selections, in server coordinates at `revision`. */
export function setCursors(host, revision, cursors) {
  const ed = host?._sx, s = ed?.shared; if (!s) return;
  const local = i => {
    if (s.outstanding) i = s.outstanding.transformIndex(i);
    if (s.buffer) i = s.buffer.transformIndex(i);
    return Math.max(0, Math.min(i, ed.ta.value.length));
  };
  s.cursors = (cursors || []).map(c => ({ ...c, anchor: local(c.anchor), head: local(c.head) }));
  renderCursors(ed);
}

/** Starts over from the server's text (scope switch, or after falling out of step). */
export function resetShared(host, text, revision) {
  const ed = host?._sx; if (!ed) return;
  const keep = ed.shared?.waiters ?? [];
  ed.shared = newShared(text, revision);
  ed.shared.waiters = keep;
  ed.ta.value = text;
  ed.ta.setSelectionRange(0, 0);
  ed.render();
  const w = keep.splice(0); for (const f of w) f();
}

/** Resolves with the text and its revision once no local operation is in flight (for Apply). */
export function syncedText(host) {
  const ed = host?._sx, s = ed?.shared;
  if (!s) return Promise.resolve({ text: ed?.ta.value ?? "", revision: -1 });
  if (ed.ta.value !== s.prev) localEdit(ed);
  if (!s.outstanding) return Promise.resolve({ text: ed.ta.value, revision: s.revision });
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error("Edits are still being sent")), 4000);
    s.waiters.push(() => { clearTimeout(t); resolve({ text: ed.ta.value, revision: s.revision }); });
  });
}

function renderCursors(ed) {
  const layer = ed.cursorsLayer, s = ed.shared;
  if (!layer) return;
  if (!s || s.cursors.length === 0) { layer.innerHTML = ""; return; }
  const v = ed.ta.value, lines = v.split("\n");
  const pos = i => { const { line, col } = lineCol(v, i); const text = lines[line - 1] ?? ""; return { line, x: text.slice(0, col - 1).replace(/\t/g, "    ").length }; };
  const html = [];
  for (const c of s.cursors) {
    const color = /^#[0-9a-fA-F]{3,8}$/.test(c.color || "") ? c.color : "#4f8cff";
    const from = Math.min(c.anchor, c.head), to = Math.max(c.anchor, c.head);
    if (to > from) {
      const a = pos(from), b = pos(to);
      for (let l = a.line; l <= b.line; l++) {
        const x0 = l === a.line ? a.x : 0;
        const x1 = l === b.line ? b.x : (lines[l - 1] ?? "").replace(/\t/g, "    ").length + 1;
        html.push(`<div class="sx-rsel" style="top:${ed.padTop + (l - 1) * ed.lh}px;left:${ed.padLeft + x0 * ed.cw}px;width:${Math.max(2, (x1 - x0) * ed.cw)}px;height:${ed.lh}px;background:${color}"></div>`);
      }
    }
    const h = pos(c.head);
    const top = ed.padTop + (h.line - 1) * ed.lh, left = ed.padLeft + h.x * ed.cw;
    html.push(`<div class="sx-rcaret" style="top:${top}px;left:${left}px;height:${ed.lh}px;border-color:${color}">` +
      `<span class="sx-rname${h.line === 1 ? " below" : ""}" style="background:${color}">${esc(c.name || "")}</span></div>`);
  }
  layer.innerHTML = html.join("");
}
