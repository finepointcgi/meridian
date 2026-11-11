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
    rect: el => { if (!el?.getBoundingClientRect) return null; const r = el.getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; },
    focus: el => el?.focus({ preventScroll: true }),
    // Context menu: keep it inside the window, place open submenus beside their entry (flipped left at the right edge),
    // and take the focus so Escape and the arrow keys work.
    fitMenu: root => {
      if (!root) return;
      const fit = (el, left, top) => {
        const r = el.getBoundingClientRect();
        el.style.left = Math.max(4, Math.min(left, innerWidth - r.width - 4)) + "px";
        el.style.top = Math.max(4, Math.min(top, innerHeight - r.height - 4)) + "px";
      };
      const r = root.getBoundingClientRect();
      fit(root, r.left, r.top);
      const place = () => root.querySelectorAll(".ctx-sub.open > .ctx-submenu").forEach(sub => {
        const p = sub.parentElement.getBoundingClientRect();
        sub.style.position = "fixed";
        const w = sub.getBoundingClientRect().width;
        fit(sub, p.right + w + 4 > innerWidth ? p.left - w - 4 : p.right + 4, p.top - 6);
      });
      root._menuObserver?.disconnect();
      root._menuObserver = new MutationObserver(place);
      root._menuObserver.observe(root, { subtree: true, attributes: true, attributeFilter: ["class"] });
      place();
      root.focus({ preventScroll: true });
    },
    // Arrow keys move between the visible entries of the menu that has the focus; Enter, Space and → open or run one.
    menuKey: (root, key) => {
      const active = document.activeElement;
      const menu = active?.closest?.(".ctx-menu") || root;
      const items = [...menu.querySelectorAll(":scope > .ctx-item, :scope > .ctx-sub > .ctx-item")];
      const i = items.indexOf(active);
      if (key === "ArrowDown" || key === "ArrowUp") {
        const n = items.length;
        if (n === 0) return;
        const next = i < 0 ? (key === "ArrowDown" ? 0 : n - 1) : (i + (key === "ArrowDown" ? 1 : n - 1)) % n;
        items[next].focus();
      } else if (key === "ArrowLeft") {
        const parent = menu.closest(".ctx-sub");
        if (parent) { const item = parent.querySelector(":scope > .ctx-item"); item.click(); item.focus(); }
      } else if (i >= 0 && (key !== "ArrowRight" || active.classList.contains("ctx-parent"))) {
        active.click();
        if (active.classList.contains("ctx-parent")) setTimeout(() => active.parentElement.querySelector(".ctx-submenu .ctx-item")?.focus(), 30);
      }
    },
    select: el => { el?.focus(); el?.select?.(); },
    caretEnd: el => { if (!el) return; el.focus(); const n = el.value?.length ?? 0; try { el.setSelectionRange(n, n); } catch { } },
    // Opens the inline label editor. Keys typed on the canvas before it had focus were recorded in order by installCanvas
    // (the server may see them later, or in the middle of opening the editor): start the editor with all of them, so
    // nothing typed quickly is lost or reordered. `seed` is the first key when typing on a selected shape started the edit;
    // without typed keys the current label is selected, ready to be replaced. The editor's value is set here, never
    // rendered by the server: a re-render lagging behind fast typing would overwrite newer characters.
    beginEdit: (el, canvas, seed, label) => {
      if (!el) return;
      const typed = canvas?._typed || "", enter = !!canvas?._typedEnter;
      if (canvas) { canvas._typed = ""; canvas._typedEnter = false; }
      const value = typed && (!seed || typed.startsWith(seed)) ? typed : seed;
      el._typing = true;
      el.focus();
      if (value == null) { el.value = label ?? ""; el.select?.(); return; }
      el.value = value;
      try { el.setSelectionRange(value.length, value.length); } catch { }
      el.dispatchEvent(new Event("input", { bubbles: true }));
      // Enter was already pressed too: commit, as it would have in the editor (leaving the editor commits it; doing it
      // right away means no later blur can arrive for an editor the server has already removed).
      if (enter && value === typed) canvas?.focus({ preventScroll: true });
    },
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
        // The label editor is open but lost focus (e.g. a re-render focused the canvas): keep typing into it.
        const editor = el.querySelector(".inline-editor input, .inline-editor textarea");
        if (editor?._typing && e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
          e.preventDefault();
          e.stopPropagation();
          const at = editor.selectionStart ?? editor.value.length, end = editor.selectionEnd ?? at;
          editor.value = editor.value.slice(0, at) + e.key + editor.value.slice(end);
          editor.focus();
          try { editor.setSelectionRange(at + 1, at + 1); } catch { }
          editor.dispatchEvent(new Event("input", { bubbles: true }));
          return;
        }
        // Remember printable keys typed on the canvas, for beginTyping (type-to-rename).
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) el._typed = (el._typedEnter ? "" : el._typed || "") + e.key, el._typedEnter = false;
        else if (e.key === "Enter" && el._typed) el._typedEnter = true;
        else if (e.key !== "Shift") { el._typed = ""; el._typedEnter = false; }
        if (["Backspace", "Delete", " ", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(e.key) ||
            ((e.metaKey || e.ctrlKey) && ["a", "d"].includes(e.key.toLowerCase()))) e.preventDefault();
      });
      el.addEventListener("contextmenu", e => e.preventDefault());
      el.addEventListener("pointerdown", e => {
        el._typed = "";
        if (e.target.closest?.(".inline-editor, .toolbox, .minimap, .selbar, .quickadd, .conn-chip, .dv-legends")) return;
        try { el.setPointerCapture(e.pointerId); } catch { }
        if (e.button === 1) e.preventDefault();
      });
      // The canvas may be leaving the page (e.g. a project without diagrams opened): its .NET side is then already disposed.
      const ro = new ResizeObserver(() => {
        if (!el.isConnected) return;
        const r = el.getBoundingClientRect();
        dotnet.invokeMethodAsync("OnResize", r.width, r.height).catch(() => { });
      });
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
