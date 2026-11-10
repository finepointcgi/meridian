// Small browser helpers for the Meridian Blazor UI.
window.meridian = (() => {
  let dirty = false;
  window.addEventListener("beforeunload", e => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });

  // Firefox only starts HTML5 drags when dataTransfer has data.
  document.addEventListener("dragstart", e => {
    const id = e.target?.closest?.("[data-drag]")?.getAttribute("data-drag");
    if (id && e.dataTransfer) { e.dataTransfer.setData("text/plain", id); e.dataTransfer.effectAllowed = "copyMove"; }
  });

  return {
    setDirty: v => { dirty = v; },
    getLast: () => { try { return localStorage.getItem("meridian.lastProject"); } catch { return null; } },
    setLast: name => { try { if (name) localStorage.setItem("meridian.lastProject", name); else localStorage.removeItem("meridian.lastProject"); } catch { } },
    rect: el => { const r = el.getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; },
    focus: el => el?.focus({ preventScroll: true }),
    select: el => { el?.focus(); el?.select?.(); },
    caretEnd: el => { if (!el) return; el.focus(); const n = el.value?.length ?? 0; try { el.setSelectionRange(n, n); } catch { } },
    click: id => document.getElementById(id)?.click(),
    // Desktop app (Photino) bridge: window.external.sendMessage reaches the native host.
    isDesktop: () => typeof window.external?.sendMessage === "function",
    desktop: (command, payload) => window.external.sendMessage(JSON.stringify({ command, ...(payload || {}) })),
    download: (name, mime, base64) => {
      // Web views cannot save blob downloads: hand the file to the desktop host's save dialog instead.
      if (typeof window.external?.sendMessage === "function") {
        window.external.sendMessage(JSON.stringify({ command: "save", name, mime, data: base64 }));
        return;
      }
      const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
      const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
      const a = Object.assign(document.createElement("a"), { href: url, download: name });
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 2000);
    },
    openHtml: html => {
      const url = URL.createObjectURL(new Blob([html], { type: "text/html" }));
      window.open(url, "_blank");
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    },
    // Canvas: non-passive wheel, keys that must not scroll/navigate, size reporting.
    installCanvas: (el, dotnet) => {
      el.addEventListener("wheel", e => e.preventDefault(), { passive: false });
      el.addEventListener("keydown", e => {
        if (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA") return;
        if (["Backspace", "Delete", " ", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(e.key) ||
            ((e.metaKey || e.ctrlKey) && ["a", "d"].includes(e.key.toLowerCase()))) e.preventDefault();
      });
      el.addEventListener("contextmenu", e => e.preventDefault());
      el.addEventListener("pointerdown", e => {
        if (e.target.closest?.(".inline-editor, .toolbox, .minimap, .selbar, .quickadd")) return;
        try { el.setPointerCapture(e.pointerId); } catch { }
        if (e.button === 1) e.preventDefault();
      });
      const ro = new ResizeObserver(() => { const r = el.getBoundingClientRect(); dotnet.invokeMethodAsync("OnResize", r.width, r.height); });
      ro.observe(el);
      el._meridianRo = ro;
    },
    // Global shortcuts (save/open/undo/redo/find) routed to .NET; text fields keep their own undo.
    installShortcuts: dotnet => {
      document.addEventListener("keydown", e => {
        if (!(e.metaKey || e.ctrlKey)) { if (e.key === "F7") { e.preventDefault(); dotnet.invokeMethodAsync("OnShortcut", "F7"); } return; }
        const k = e.key.toLowerCase();
        const inText = ["INPUT", "TEXTAREA"].includes(e.target.tagName) || e.target.isContentEditable;
        let name = null;
        if (k === "s") name = e.shiftKey ? "SaveAs" : "Save";
        else if (k === "o") name = "Open";
        else if (k === "f") name = "Find";
        else if (k === "k") name = "Palette";
        else if (k === "z" && !inText) name = e.shiftKey ? "Redo" : "Undo";
        else if (k === "y" && !inText) name = "Redo";
        if (name) { e.preventDefault(); dotnet.invokeMethodAsync("OnShortcut", name); }
      });
    },
    // Drag-to-resize panels by updating a CSS variable on the workbench grid.
    splitter: (el, cssVar, side, min, max) => {
      const host = document.querySelector(".workbench");
      el.addEventListener("pointerdown", e => {
        e.preventDefault();
        el.setPointerCapture(e.pointerId);
        const move = ev => {
          let v = side === "left" ? ev.clientX - 8 : side === "right" ? window.innerWidth - ev.clientX - 8 : window.innerHeight - 28 - 40 - ev.clientY;
          host.style.setProperty(cssVar, Math.max(min, Math.min(max, v)) + "px");
        };
        const up = () => { el.removeEventListener("pointermove", move); el.removeEventListener("pointerup", up); };
        el.addEventListener("pointermove", move);
        el.addEventListener("pointerup", up);
      });
    },
    uninstallCanvas: el => el?._meridianRo?.disconnect(),
  };
})();

// Desktop app: the host announces a newer Meridian release over the Photino bridge; show a small dismissible notice.
(() => {
  const ext = window.external;
  if (typeof ext?.sendMessage !== "function" || typeof ext?.receiveMessage !== "function") return;
  const css = "position:fixed;right:16px;bottom:16px;z-index:10000;max-width:360px;padding:10px 12px;border-radius:10px;" +
    "background:#131b2b;color:#e7ecf6;border:1px solid rgba(79,140,255,.5);box-shadow:0 8px 28px rgba(0,0,0,.35);" +
    "font:13px/1.4 Inter,-apple-system,'Segoe UI',system-ui,sans-serif;display:flex;gap:10px;align-items:center;";
  const btn = "border:none;border-radius:7px;cursor:pointer;font:inherit;";
  let box = null;
  const show = (text, action) => {
    if (!box) {
      box = document.createElement("div");
      box.setAttribute("role", "status");
      box.style.cssText = css;
      document.body.appendChild(box);
    }
    box.replaceChildren();
    const span = document.createElement("span");
    span.style.flex = "1";
    span.textContent = text;
    box.appendChild(span);
    if (action) {
      const go = Object.assign(document.createElement("button"), { textContent: action });
      go.style.cssText = btn + "background:#4f8cff;color:#fff;padding:5px 10px;font-weight:600;";
      go.onclick = () => { ext.sendMessage(JSON.stringify({ command: "update" })); show("Downloading the update…"); };
      box.appendChild(go);
    }
    const x = Object.assign(document.createElement("button"), { textContent: "×", title: "Dismiss" });
    x.style.cssText = btn + "background:none;color:#a3aec4;font-size:16px;";
    x.onclick = () => { box?.remove(); box = null; };
    box.appendChild(x);
  };
  ext.receiveMessage(raw => {
    let m; try { m = JSON.parse(raw); } catch { return; }
    if (m?.type === "update") show(`Meridian ${m.version} is available.`, "Install");
    else if (m?.type === "update-progress" && box) show(`Downloading Meridian… ${Math.round(m.percent)}%`);
    else if (m?.type === "update-error") show(`Update failed: ${m.message}`);
  });
  const hello = () => ext.sendMessage(JSON.stringify({ command: "hello" }));
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", hello); else hello();
})();
