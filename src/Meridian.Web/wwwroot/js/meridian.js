// Small browser helpers for the Meridian Blazor UI.
window.meridian = (() => {
  let dirty = false;
  window.addEventListener("beforeunload", e => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });

  // Firefox only starts HTML5 drags when dataTransfer has data.
  document.addEventListener("dragstart", e => {
    const id = e.target?.closest?.("[data-drag]")?.getAttribute("data-drag");
    if (id && e.dataTransfer) { e.dataTransfer.setData("text/plain", id); e.dataTransfer.effectAllowed = "copyMove"; }
  });

  // The user guide (/guide/…, served by this app): a new browser tab, or the system browser from the desktop app.
  const openGuide = path => {
    const p = typeof path === "string" && path.startsWith("/guide") ? path : "/guide/";
    const url = new URL(p, location.origin).href;
    if (typeof window.external?.sendMessage === "function") window.external.sendMessage(JSON.stringify({ command: "open-url", url }));
    else window.open(url, "_blank", "noopener");
  };
  // "Learn more" links (<a data-guide href="/guide/…">, GuideLink.razor and tour text) go through openGuide.
  document.addEventListener("click", e => {
    const a = e.target?.closest?.("a[data-guide]");
    if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey) return;
    e.preventDefault();
    openGuide(a.getAttribute("href"));
  });

  // Keyboard for title-bar menus whose list has the class kbd-menu (the project menu and its Export ▸ submenu, a
  // kbd-submenu): ↑ / ↓ / Home / End move between items, → or Enter on a submenu opens it at its first item, ← goes back
  // to the parent item, Enter / Space run the item. Escape stays MudBlazor's (submenu first, then the menu).
  const menuItems = list => [...list.querySelectorAll(":scope > .mud-menu-item, :scope > .mud-menu > .mud-menu-item")]
    .filter(i => !i.classList.contains("mud-disabled") && i.getAttribute("aria-disabled") !== "true");
  const openMenuLists = () => [...document.querySelectorAll(".mud-popover-open .mud-list.kbd-menu")];
  const enterSubmenu = item => {
    const before = new Set(openMenuLists());
    if (!item.closest(".mud-menu")?.querySelector(":scope > .mud-menu-item[aria-expanded=true]")) item.click();
    let tries = 0;
    const focusFirst = () => {
      const sub = openMenuLists().find(l => !before.has(l) && l.classList.contains("kbd-submenu"))
        || openMenuLists().filter(l => l.classList.contains("kbd-submenu")).pop();
      if (sub && menuItems(sub)[0]) menuItems(sub)[0].focus();
      else if (tries++ < 20) setTimeout(focusFirst, 25);
    };
    focusFirst();
  };
  window.addEventListener("keydown", e => {
    if (!e.isTrusted) return;   // our own Escape below
    if (!["ArrowDown", "ArrowUp", "ArrowRight", "ArrowLeft", "Home", "End", "Enter", " ", "Escape"].includes(e.key)) return;
    const lists = openMenuLists();
    if (lists.length === 0) return;
    const active = document.activeElement;
    if (e.key === "Escape") {
      // Escape closes an open submenu first (wherever the focus is), then the menu (MudBlazor).
      const sub = lists.filter(l => l.classList.contains("kbd-submenu")).pop();
      if (!sub) return;
      const parent = lists.find(l => l !== sub && l.querySelector(".mud-menu-sub-menu-activator"))?.querySelector(".mud-menu-sub-menu-activator");
      (menuItems(sub)[0] || sub).dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", code: "Escape", bubbles: true }));
      if (parent) setTimeout(() => parent.focus(), 60);
      e.preventDefault(); e.stopImmediatePropagation();
      return;
    }
    let list = lists.find(l => l.contains(active) && active.closest(".mud-list") === l);
    if (!list) {
      if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) return;
      list = lists[lists.length - 1];
      const items = menuItems(list);
      (e.key === "ArrowUp" || e.key === "End" ? items[items.length - 1] : items[0])?.focus();
      e.preventDefault(); e.stopImmediatePropagation();
      return;
    }
    const items = menuItems(list);
    const i = items.indexOf(active);
    const isParent = active.classList.contains("mud-menu-sub-menu-activator");
    if (e.key === "ArrowDown") items[(i + 1) % items.length]?.focus();
    else if (e.key === "ArrowUp") items[(i - 1 + items.length) % items.length]?.focus();
    else if (e.key === "Home") items[0]?.focus();
    else if (e.key === "End") items[items.length - 1]?.focus();
    else if (isParent && (e.key === "ArrowRight" || e.key === "Enter" || e.key === " ")) enterSubmenu(active);
    else if (e.key === "ArrowLeft" && list.classList.contains("kbd-submenu")) {
      const parent = lists.find(l => l !== list && l.querySelector(".mud-menu-sub-menu-activator"))?.querySelector(".mud-menu-sub-menu-activator");
      // MudBlazor closes the innermost open submenu on an Escape from inside it.
      active.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", code: "Escape", bubbles: true }));
      if (parent) setTimeout(() => parent.focus(), 60);
    }
    else if (e.key === "Enter" || e.key === " ") active.click();
    else return;
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);

  return {
    openGuide,
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
    // A file the server streams (reports: GET api/reports/{id}/file). The browser downloads it directly, chunk by chunk;
    // the desktop host fetches it with the single-use ticket in the URL and writes it to the file the user picks.
    // open: the desktop host opens the saved file afterwards (a printed PDF, as its web view has no print dialog).
    downloadUrl: (name, url, open) => {
      const href = new URL(url, document.baseURI).href;
      if (typeof window.external?.sendMessage === "function") {
        window.external.sendMessage(JSON.stringify({ command: "save-url", name, url: href, open: !!open }));
        return;
      }
      const a = Object.assign(document.createElement("a"), { href, download: name });
      document.body.appendChild(a); a.click(); a.remove();
    },
    openUrl: url => window.open(new URL(url, document.baseURI).href, "_blank", "noopener"),
    // Diagram printing: the print-optimised page (@page CSS, no scripts) in a hidden, sandboxed frame, then its print dialog.
    printHtml: html => new Promise(resolve => {
      const frame = document.createElement("iframe");
      frame.setAttribute("sandbox", "allow-same-origin allow-modals");
      frame.setAttribute("aria-hidden", "true");
      frame.style.cssText = "position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden";
      frame.onload = () => {
        resolve();   // print() blocks until its dialog closes: answer the server first
        setTimeout(() => {
          try { frame.contentWindow.focus(); frame.contentWindow.print(); }
          finally { setTimeout(() => frame.remove(), 60000); }
        }, 0);
      };
      frame.srcdoc = html;
      document.body.appendChild(frame);
    }),
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
        else if (k === "f") name = e.shiftKey ? "FindReplace" : "Find";
        else if (k === "k") name = "Palette";
        else if (k === "p" && !e.shiftKey && !e.altKey) name = "Print";
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
