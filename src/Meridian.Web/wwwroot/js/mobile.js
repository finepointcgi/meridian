// Phone / tablet support for the Meridian workbench (loaded by Components/Mobile/MobileShell.razor).
//  - reports the viewport layout (phone < 900px, tablet < 1280px, desktop) to .NET;
//  - touch on the diagram canvas: pinch to zoom, two-finger pan, one-finger pan in view-only mode, tap to select,
//    long-press for the action menu. The gesture is previewed with a CSS transform on the SVG and committed to the
//    DiagramController once, when the fingers lift (one server round trip per gesture);
//  - an offline banner (the reconnect dialog itself is restyled as a banner in mobile.css).
// Mouse and pen input are never touched: the canvas keeps its desktop pointer handling.

const LONG_PRESS_MS = 500;
const TAP_SLOP = 8;                    // px a finger may drift and still count as a tap
const MIN_ZOOM = 0.2, MAX_ZOOM = 4;    // DiagramController.SetZoom's range
const PASS_THROUGH = ".toolbox, .selbar, .minimap, .layers-fab, .inline-editor, .quickadd, .fmt-pop, button, input, textarea, select, a, [contenteditable]";

let dotnet = null;
let phoneMax = 900, tabletMax = 1280;
let editMode = false;
let lastLayout = null;
let resizeTimer = 0;
let swallowClickUntil = 0;
let installed = false;

// Pointers this module owns, by id → { x, y } in client coordinates.
const pointers = new Map();
// The pointer handed to Blazor in edit mode (one-finger shape editing), if any.
let passthroughId = null;
let gesture = null;

const touchUi = () => window.matchMedia(`(max-width: ${phoneMax - 0.02}px), (pointer: coarse)`).matches;

// ------------------------------------------------------------------ affine helpers: p' = s·p + t

const identity = () => ({ s: 1, tx: 0, ty: 0 });
const compose = (outer, inner) => ({ s: outer.s * inner.s, tx: outer.s * inner.tx + outer.tx, ty: outer.s * inner.ty + outer.ty });

function currentZoom(svg) {
  const g = svg?.querySelector(":scope > g[transform]");
  const m = /scale\(([-\d.e]+)\)/.exec(g?.getAttribute("transform") || "");
  return m ? parseFloat(m[1]) : 1;
}

function rel(p) { return { x: p.x - gesture.rect.left, y: p.y - gesture.rect.top }; }

function twoPointers() {
  const [a, b] = [...pointers.values()];
  return { a, b, mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, dist: Math.hypot(a.x - b.x, a.y - b.y) || 1 };
}

// Starts a new phase (pan or pinch) from the current fingers, folding the previous phase into the base transform.
function rebase() {
  gesture.base = compose(gesture.phase, gesture.base);
  gesture.phase = identity();
  if (pointers.size >= 2) {
    const t = twoPointers();
    gesture.pinch = { mid: rel(t.mid), dist: t.dist };
    gesture.pan = null;
  } else if (pointers.size === 1) {
    gesture.pan = rel([...pointers.values()][0]);
    gesture.pinch = null;
  }
}

function total() { return compose(gesture.phase, gesture.base); }

function preview() {
  const svg = gesture.svg;
  if (!svg) return;
  const t = total();
  svg.style.transformOrigin = "0 0";
  svg.style.transform = `matrix(${t.s}, 0, 0, ${t.s}, ${t.tx}, ${t.ty})`;
}

function clearPreview(svg) {
  if (!svg) return;
  svg.style.transform = "";
  svg.style.transformOrigin = "";
}

// ------------------------------------------------------------------ pointer handlers (window, capture phase)

function canvasFor(e) {
  if (e.pointerType !== "touch" || !touchUi()) return null;
  const canvas = e.target?.closest?.(".diagram-host .canvas");
  if (!canvas || e.target.closest(PASS_THROUGH)) return null;
  return canvas;
}

function swallow(e) {
  e.stopPropagation();
  if (e.cancelable) e.preventDefault();
}

function onDown(e) {
  if (e.pointerType !== "touch") return;
  let canvas;
  if (gesture) {
    canvas = gesture.canvas;
    if (!canvas.contains(e.target)) return;
  } else {
    canvas = canvasFor(e);
    if (!canvas) return;
  }

  // Edit mode: a single finger goes to the canvas's own handlers (select, move, connect…).
  if (editMode && pointers.size === 0 && passthroughId === null) {
    passthroughId = e.pointerId;
    passthroughPos = { x: e.clientX, y: e.clientY };
    armPassthroughLongPress(canvas, e);
    return;
  }
  cancelPassthroughLongPress();

  swallow(e);
  try { canvas.setPointerCapture(e.pointerId); } catch { }
  if (!gesture) {
    const r = canvas.getBoundingClientRect();
    gesture = {
      canvas, svg: canvas.querySelector("svg.main"), rect: { left: r.left, top: r.top },
      zoom0: currentZoom(canvas.querySelector("svg.main")),
      base: identity(), phase: identity(), pan: null, pinch: null,
      start: { x: e.clientX, y: e.clientY }, moved: false, multi: false, longPressed: false, timer: 0,
    };
  }
  // A pinch that starts while Blazor owns the first finger takes that finger over (its final pointerup still reaches Blazor).
  if (passthroughId !== null && !pointers.has(passthroughId)) pointers.set(passthroughId, { ...passthroughPos });
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (pointers.size > 1) { gesture.multi = true; cancelLongPress(); }
  else armLongPress();
  rebase();
}

let passthroughPos = { x: 0, y: 0 };

// Edit mode: holding one finger still on a shape or connector adds it to (or takes it out of) the selection.
let passthroughTimer = 0, passthroughStartX = 0, passthroughStartY = 0;
function armPassthroughLongPress(canvas, e) {
  cancelPassthroughLongPress();
  const start = { x: e.clientX, y: e.clientY };
  passthroughStartX = start.x;
  passthroughStartY = start.y;
  passthroughTimer = setTimeout(async () => {
    passthroughTimer = 0;
    if (passthroughId === null || !dotnet || pointers.size > 0) return;
    if (Math.hypot(passthroughPos.x - start.x, passthroughPos.y - start.y) > TAP_SLOP) return;
    const r = canvas.getBoundingClientRect();
    try {
      const hit = await dotnet.invokeMethodAsync("TouchLongPressAdd", start.x - r.left, start.y - r.top);
      if (hit) navigator.vibrate?.(12);
    } catch { }
  }, LONG_PRESS_MS);
}

function cancelPassthroughLongPress() {
  if (passthroughTimer) { clearTimeout(passthroughTimer); passthroughTimer = 0; }
}

function armLongPress() {
  cancelLongPress();
  gesture.timer = setTimeout(async () => {
    if (!gesture || gesture.moved || gesture.multi || !dotnet) return;
    gesture.longPressed = true;
    const p = rel(gesture.start);
    try {
      const hit = await dotnet.invokeMethodAsync("TouchLongPress", p.x, p.y);
      if (hit) navigator.vibrate?.(12);
    } catch { }
  }, LONG_PRESS_MS);
}

function cancelLongPress() {
  if (gesture?.timer) { clearTimeout(gesture.timer); gesture.timer = 0; }
}

function onMove(e) {
  if (e.pointerType !== "touch") return;
  if (e.pointerId === passthroughId) {
    passthroughPos = { x: e.clientX, y: e.clientY };
    // Moving the finger is a drag (move, connect, rubber band), not a long press.
    if (passthroughTimer && Math.hypot(e.clientX - passthroughStartX, e.clientY - passthroughStartY) > TAP_SLOP) cancelPassthroughLongPress();
    if (!pointers.has(e.pointerId)) return;      // Blazor still owns it
  }
  if (!gesture || !pointers.has(e.pointerId)) return;
  swallow(e);
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (!gesture.moved && Math.hypot(e.clientX - gesture.start.x, e.clientY - gesture.start.y) > TAP_SLOP) {
    gesture.moved = true;
    cancelLongPress();
  }
  if (gesture.longPressed) return;
  if (pointers.size >= 2 && gesture.pinch) {
    const t = twoPointers();
    const mid = rel(t.mid);
    // Keep the previewed zoom inside the range the controller will clamp to.
    const zBefore = gesture.zoom0 * gesture.base.s;
    let s = t.dist / gesture.pinch.dist;
    s = Math.min(Math.max(s, MIN_ZOOM / zBefore), MAX_ZOOM / zBefore);
    gesture.phase = { s, tx: mid.x - s * gesture.pinch.mid.x, ty: mid.y - s * gesture.pinch.mid.y };
    gesture.anchor = mid;
  } else if (pointers.size === 1 && gesture.pan && gesture.moved && !editMode) {
    const p = rel([...pointers.values()][0]);
    gesture.phase = { s: 1, tx: p.x - gesture.pan.x, ty: p.y - gesture.pan.y };
    gesture.anchor = p;
  } else return;
  if (!gesture.raf) gesture.raf = requestAnimationFrame(() => { if (gesture) { gesture.raf = 0; preview(); } });
}

function onUp(e) {
  if (e.pointerType !== "touch") return;
  const owned = pointers.has(e.pointerId);
  if (e.pointerId === passthroughId) {
    cancelPassthroughLongPress();
    passthroughId = null;
    pointers.delete(e.pointerId);
    // Let Blazor see the end of the finger it started with, so its own gesture finishes.
    if (gesture && pointers.size === 0) finish(e, true);
    else if (gesture) rebase();
    return;
  }
  if (!gesture || !owned) return;
  swallow(e);
  pointers.delete(e.pointerId);
  if (pointers.size > 0) { rebase(); return; }
  finish(e, false);
}

function finish(e, viaPassthrough) {
  const g = gesture;
  gesture = null;
  cancelLongPress();
  if (g.raf) cancelAnimationFrame(g.raf);
  swallowClickUntil = performance.now() + 400;
  const t = compose(g.phase, g.base);
  const changed = Math.abs(t.s - 1) > 1e-3 || Math.abs(t.tx) > 0.5 || Math.abs(t.ty) > 0.5;
  if (!dotnet) { clearPreview(g.svg); return; }
  if (changed) {
    // p' = s·p + t, expressed as c1 + s·(p − c0) around the last finger position so zoom clamping keeps it fixed.
    const c0 = g.anchor ?? { x: 0, y: 0 };
    const c1 = { x: t.s * c0.x + t.tx, y: t.s * c0.y + t.ty };
    dotnet.invokeMethodAsync("TouchTransform", t.s, c0.x, c0.y, c1.x, c1.y)
      .catch(() => { })
      .finally(() => requestAnimationFrame(() => clearPreview(g.svg)));
    return;
  }
  clearPreview(g.svg);
  if (!viaPassthrough && e.type === "pointerup" && !g.moved && !g.multi && !g.longPressed) {
    const p = { x: g.start.x - g.rect.left, y: g.start.y - g.rect.top };
    dotnet.invokeMethodAsync("TouchTap", p.x, p.y).catch(() => { });
  }
}

function onLeave(e) {
  if (e.pointerType === "touch" && gesture && pointers.has(e.pointerId)) e.stopPropagation();
}

function onClick(e) {
  if (performance.now() < swallowClickUntil && e.target?.closest?.(".diagram-host .canvas") && !e.target.closest(PASS_THROUGH)) {
    e.stopPropagation();
    e.preventDefault();
  }
}

// iOS Safari: stop the page itself from pinch-zooming while a canvas gesture is running.
function onGestureStart(e) {
  if (e.target?.closest?.(".diagram-host .canvas") && touchUi()) e.preventDefault();
}

// ------------------------------------------------------------------ viewport

function layoutOf(w) { return w < phoneMax ? "phone" : w < tabletMax ? "tablet" : "desktop"; }

function report(force) {
  const w = window.innerWidth;
  const layout = layoutOf(w);
  if (!force && layout === lastLayout) return;
  lastLayout = layout;
  document.documentElement.dataset.layout = layout;
  dotnet?.invokeMethodAsync("OnViewport", w).catch(() => { });
}

function onResize() {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => report(false), 80);
}

// ------------------------------------------------------------------ offline banner

let banner = null;

function showOffline(offline) {
  if (!offline) { banner?.remove(); banner = null; return; }
  if (banner) return;
  banner = document.createElement("div");
  banner.className = "m-net-banner";
  banner.setAttribute("role", "status");
  banner.setAttribute("aria-live", "polite");
  banner.textContent = "You're offline. Meridian will reconnect when the network is back.";
  document.body.appendChild(banner);
}

const onOnline = () => showOffline(false);
const onOffline = () => showOffline(true);

// ------------------------------------------------------------------ module API

export function install(ref, phone, tablet) {
  dotnet = ref;
  phoneMax = phone || phoneMax;
  tabletMax = tablet || tabletMax;
  if (!installed) {
    installed = true;
    window.addEventListener("pointerdown", onDown, { capture: true, passive: false });
    window.addEventListener("pointermove", onMove, { capture: true, passive: false });
    window.addEventListener("pointerup", onUp, { capture: true, passive: false });
    window.addEventListener("pointercancel", onUp, { capture: true, passive: false });
    window.addEventListener("pointerleave", onLeave, true);
    window.addEventListener("pointerout", onLeave, true);
    window.addEventListener("click", onClick, true);
    document.addEventListener("gesturestart", onGestureStart, { passive: false });
    window.addEventListener("resize", onResize);
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);
  }
  report(true);
  if (navigator.onLine === false) showOffline(true);
}

export function setEditMode(on) {
  editMode = !!on;
}

export function uninstall() {
  if (!installed) return;
  installed = false;
  window.removeEventListener("pointerdown", onDown, { capture: true });
  window.removeEventListener("pointermove", onMove, { capture: true });
  window.removeEventListener("pointerup", onUp, { capture: true });
  window.removeEventListener("pointercancel", onUp, { capture: true });
  window.removeEventListener("pointerleave", onLeave, true);
  window.removeEventListener("pointerout", onLeave, true);
  window.removeEventListener("click", onClick, true);
  document.removeEventListener("gesturestart", onGestureStart);
  window.removeEventListener("resize", onResize);
  window.removeEventListener("online", onOnline);
  window.removeEventListener("offline", onOffline);
  showOffline(false);
  dotnet = null;
  gesture = null;
  pointers.clear();
  passthroughId = null;
}

// Exposed for the Playwright layout checks.
export const _test = { layoutOf, compose };
