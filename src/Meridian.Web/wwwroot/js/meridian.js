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
