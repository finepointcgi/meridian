// Diagram extras for the Meridian UI: images (pick / drop / paste with downscaling), PNG export and copy,
// opening links, and small helpers for the onboarding tour. Extends window.meridian from meridian.js.
(() => {
  const m = window.meridian = window.meridian || {};
  const MAX_IMAGE_BYTES = 2 * 1024 * 1024;   // DiagramExtras.MaxImageBytes
  const MAX_SIDE = 4096;
  const TYPES = /^image\/(png|jpeg|gif|webp|svg\+xml)$/;

  const isDesktop = () => typeof window.external?.sendMessage === "function";
  const dataBytes = url => Math.floor((url.length - url.indexOf(",") - 1) * 3 / 4);

  const loadImg = src => new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("The picture could not be read."));
    img.src = src;
  });

  const readAsDataUrl = file => new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });

  /** Reads an image file into a data URI no larger than 2 MB, shrinking it through a canvas when needed. */
  async function readImage(file) {
    if (!file || !TYPES.test(file.type)) throw new Error("Only PNG, JPEG, GIF, WebP and SVG pictures can be inserted.");
    const original = await readAsDataUrl(file);
    const img = await loadImg(original);
    let w = img.naturalWidth || 300, h = img.naturalHeight || 200;
    if (file.size <= MAX_IMAGE_BYTES && Math.max(w, h) <= MAX_SIDE && dataBytes(original) <= MAX_IMAGE_BYTES)
      return { data: original, w, h, name: file.name || "" };

    let scale = Math.min(1, MAX_SIDE / Math.max(w, h));
    for (let attempt = 0; attempt < 10; attempt++) {
      const cw = Math.max(1, Math.round(w * scale)), ch = Math.max(1, Math.round(h * scale));
      const canvas = Object.assign(document.createElement("canvas"), { width: cw, height: ch });
      canvas.getContext("2d").drawImage(img, 0, 0, cw, ch);
      // Keep PNG (transparency) when it fits, otherwise JPEG.
      let out = file.type === "image/png" ? canvas.toDataURL("image/png") : "";
      if (!out || dataBytes(out) > MAX_IMAGE_BYTES) out = canvas.toDataURL("image/jpeg", 0.85);
      if (dataBytes(out) <= MAX_IMAGE_BYTES) return { data: out, w: cw, h: ch, name: file.name || "" };
      scale *= 0.7;
    }
    throw new Error("That picture is too large to insert.");
  }

  async function insertFiles(dotnet, files, x, y) {
    let i = 0;
    for (const f of files) {
      if (!f.type?.startsWith("image/")) continue;
      try {
        const img = await readImage(f);
        await dotnet.invokeMethodAsync("OnImage", img.data, img.w, img.h, x == null ? null : x + i * 24, y == null ? null : y + i * 24, img.name);
      } catch (e) {
        dotnet.invokeMethodAsync("OnImageError", e?.message || String(e));
      }
      i++;
    }
  }

  const MERMAID_FILE = /\.(mmd|mermaid|md)$/i;
  const MAX_MERMAID_BYTES = 8 * 1024 * 1024;   // MermaidImport.MaxBytes

  /** Mermaid files dropped on the canvas: read as text and imported by the server (one undo step). */
  async function importMermaid(dotnet, files) {
    for (const f of files) {
      if (f.size > MAX_MERMAID_BYTES) { dotnet.invokeMethodAsync("OnImageError", `${f.name} is larger than 8 MB.`); continue; }
      try { await dotnet.invokeMethodAsync("OnMermaidFile", f.name, await f.text()); }
      catch (e) { dotnet.invokeMethodAsync("OnImageError", e?.message || String(e)); }
    }
  }

  // Diagram files read from a folder or a drop (DiagramFolderImport.Extensions / limits).
  const DIAGRAM_FILE = /\.(mmd|mermaid|md|puml|plantuml|pu|wsd|drawio|dio|drawio\.xml|drawio\.svg|drawio\.png|dio\.svg|dio\.png|gliffy|vsdx|vsdm)$/i;
  // draw.io, Gliffy, Visio and PlantUML files dropped on the canvas go through the diagram-files import (preview-free drop).
  const DRAWIO_FILE = /\.(puml|plantuml|pu|wsd|drawio|dio|drawio\.xml|drawio\.svg|drawio\.png|dio\.svg|dio\.png|gliffy|vsdx|vsdm)$/i;
  const MAX_DIAGRAM_FILE = 32 * 1024 * 1024, MAX_DIAGRAM_TOTAL = 128 * 1024 * 1024, MAX_DIAGRAM_FILES = 2000, BATCH_BYTES = 8 * 1024 * 1024;
  const SKIP_DIR = /(^|\/)(node_modules|\.git|bin|obj|dist|build|\.venv|__MACOSX)(\/|$)/;

  function toBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let s = "";
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  }

  /** Reads [path, File] pairs and sends them to .NET in batches (Add), then Done(folder, skipped). */
  async function sendDiagramFiles(dotnet, pairs, folder, skipped, add = "Add", done = "Done") {
    let batch = [], size = 0, total = 0, count = 0;
    for (const [path, f] of pairs) {
      if (SKIP_DIR.test(path) || !DIAGRAM_FILE.test(path)) { skipped++; continue; }
      if (f.size > MAX_DIAGRAM_FILE || total + f.size > MAX_DIAGRAM_TOTAL || count >= MAX_DIAGRAM_FILES) { skipped++; continue; }
      const item = /\.(png|vsdx|vsdm)$/i.test(path) ? { path, base64: toBase64(await f.arrayBuffer()) } : { path, text: await f.text() };
      batch.push(item); size += f.size; total += f.size; count++;
      if (size >= BATCH_BYTES) { await dotnet.invokeMethodAsync(add, batch); batch = []; size = 0; }
    }
    if (batch.length > 0) await dotnet.invokeMethodAsync(add, batch);
    await dotnet.invokeMethodAsync(done, folder, skipped);
  }

  /** "Import a folder of diagrams…": the browser's folder picker; only Mermaid and draw.io files are read. */
  m.pickDiagramFolder = dotnet => {
    const input = Object.assign(document.createElement("input"), { type: "file", multiple: true });
    input.setAttribute("webkitdirectory", "");
    input.setAttribute("directory", "");
    input.style.display = "none";
    let settled = false;
    input.addEventListener("change", async () => {
      settled = true;
      const files = [...input.files];
      input.remove();
      const pairs = files.map(f => [f.webkitRelativePath || f.name, f]);
      const folder = (pairs[0]?.[0] || "").split("/")[0] || "folder";
      try { await sendDiagramFiles(dotnet, pairs, folder, 0); }
      catch (e) { dotnet.invokeMethodAsync("Cancel"); console.error(e); }
    });
    input.addEventListener("cancel", () => { if (!settled) { settled = true; input.remove(); dotnet.invokeMethodAsync("Cancel"); } });
    document.body.appendChild(input);
    input.click();
  };

  /** Walks dropped folders (and takes dropped draw.io files) as [path, File] pairs. Entries must be taken during the drop event. */
  async function collectEntries(entries) {
    const out = [];
    const fileOf = entry => new Promise((resolve, reject) => entry.file(resolve, reject));
    async function walk(entry, prefix) {
      if (out.length >= MAX_DIAGRAM_FILES * 4) return;
      if (entry.isFile) { out.push([prefix + entry.name, await fileOf(entry)]); return; }
      if (!entry.isDirectory || SKIP_DIR.test(prefix + entry.name + "/")) return;
      const reader = entry.createReader();
      for (;;) {
        const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
        if (batch.length === 0) break;
        for (const child of batch) await walk(child, prefix + entry.name + "/");
      }
    }
    for (const entry of entries) await walk(entry, "");
    return out;
  }

  /** Canvas: drop picture, Mermaid (.mmd), PlantUML or draw.io files or a folder of diagrams onto it, or paste a picture while it has focus. */
  m.installCanvasExtras = (el, dotnet) => {
    const onDrop = e => {
      const all = [...(e.dataTransfer?.files || [])];
      const entries = [...(e.dataTransfer?.items || [])].map(it => it.kind === "file" && it.webkitGetAsEntry ? it.webkitGetAsEntry() : null).filter(Boolean);
      const folders = entries.filter(x => x.isDirectory);
      const drawio = entries.filter(x => x.isFile && DRAWIO_FILE.test(x.name));
      // Without entries (some browsers, synthetic drops) draw.io files still come through as plain files.
      const drawioFiles = entries.length > 0 ? [] : all.filter(f => DRAWIO_FILE.test(f.name || ""));
      if (folders.length > 0 || drawio.length > 0 || drawioFiles.length > 0) {
        e.preventDefault();
        const collected = drawioFiles.length > 0 ? Promise.resolve(drawioFiles.map(f => [f.name, f])) : collectEntries([...folders, ...drawio]);
        collected
          .then(pairs => sendDiagramFiles(dotnet, pairs, folders[0]?.name || (drawio[0] || drawioFiles[0]).name, 0, "OnDiagramFilesBatch", "OnDiagramFilesDone"))
          .catch(err => dotnet.invokeMethodAsync("OnImageError", err?.message || String(err)));
      }
      const mermaid = folders.length > 0 ? [] : all.filter(f => MERMAID_FILE.test(f.name || ""));
      if (mermaid.length > 0) { e.preventDefault(); importMermaid(dotnet, mermaid); }
      const files = all.filter(f => f.type?.startsWith("image/") && !DRAWIO_FILE.test(f.name || ""));
      if (files.length === 0) return;
      e.preventDefault();
      const r = el.getBoundingClientRect();
      insertFiles(dotnet, files, e.clientX - r.left, e.clientY - r.top);
    };
    const onPaste = e => {
      const active = document.activeElement;
      if (!(active === el || el.contains(active)) || ["INPUT", "TEXTAREA"].includes(active?.tagName)) return;
      const files = [...(e.clipboardData?.items || [])].filter(it => it.kind === "file" && it.type.startsWith("image/")).map(it => it.getAsFile()).filter(Boolean);
      if (files.length === 0) return;
      e.preventDefault();
      insertFiles(dotnet, files, null, null);
    };
    el.addEventListener("drop", onDrop);
    document.addEventListener("paste", onPaste);
    el._meridianExtras = () => { el.removeEventListener("drop", onDrop); document.removeEventListener("paste", onPaste); };
  };

  m.uninstallCanvasExtras = el => { try { el?._meridianExtras?.(); } catch { } };

  /** Shapes panel "Image" tile: choose a picture file; x/y (canvas pixels) place it, null centres it. */
  m.pickImage = (dotnet, x, y) => {
    const input = Object.assign(document.createElement("input"), { type: "file", accept: "image/png,image/jpeg,image/gif,image/webp,image/svg+xml", multiple: true });
    input.style.display = "none";
    input.addEventListener("change", () => { insertFiles(dotnet, [...input.files], x, y); input.remove(); });
    document.body.appendChild(input);
    input.click();
  };

  /** Puts text on the clipboard. Returns null on success, else a message. */
  m.copyText = async text => {
    try {
      if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(text); return null; }
      const ta = Object.assign(document.createElement("textarea"), { value: text });
      ta.style.position = "fixed"; ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand("copy");
      ta.remove();
      return ok ? null : "This browser can't put text on the clipboard. Use Export diagram as Mermaid instead.";
    } catch (e) {
      return "The clipboard is not available here. Use Export diagram as Mermaid instead.";
    }
  };

  /** Opens a web link: in a new tab, or in the system browser from the desktop app. */
  m.openUrl = url => {
    if (!/^(https?:|mailto:)/i.test(url || "")) return;
    if (isDesktop()) window.external.sendMessage(JSON.stringify({ command: "open-url", url }));
    else window.open(url, "_blank", "noopener,noreferrer");
  };

  /** Rasterises an SVG document at a scale factor (canvas size is capped so browsers can draw it). */
  async function renderPng(svg, scale) {
    const root = new DOMParser().parseFromString(svg, "image/svg+xml").documentElement;
    const w = parseFloat(root.getAttribute("width")) || 800, h = parseFloat(root.getAttribute("height")) || 600;
    let s = Math.max(0.1, scale || 1);
    s = Math.min(s, 16000 / w, 16000 / h, Math.sqrt(64e6 / (w * h)));
    const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml;charset=utf-8" }));
    try {
      const img = await loadImg(url);
      const canvas = Object.assign(document.createElement("canvas"), { width: Math.round(w * s), height: Math.round(h * s) });
      const ctx = canvas.getContext("2d");
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
      return await new Promise((resolve, reject) => canvas.toBlob(b => b ? resolve(b) : reject(new Error("The browser could not create the PNG.")), "image/png"));
    } finally {
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  }

  const blobToBase64 = blob => readAsDataUrl(blob).then(u => u.slice(u.indexOf(",") + 1));

  /** PNG bytes (base64) of an SVG, for download or zipping. */
  m.svgToPng = async (svg, scale) => blobToBase64(await renderPng(svg, scale));

  /** Copies an SVG to the clipboard as a PNG picture. Returns null on success, else a message. */
  m.copySvgAsPng = async (svg, scale) => {
    if (!navigator.clipboard?.write || typeof ClipboardItem === "undefined")
      return "This browser can't put pictures on the clipboard. Use Export as PNG instead.";
    try {
      try { await navigator.clipboard.write([new ClipboardItem({ "image/png": renderPng(svg, scale) })]); }
      catch (e) {
        if (!(e instanceof TypeError)) throw e;
        await navigator.clipboard.write([new ClipboardItem({ "image/png": await renderPng(svg, scale) })]);
      }
      return null;
    } catch (e) {
      return "Couldn't copy the picture: " + (e?.message || e);
    }
  };

  // ---------------------------------------------------------------- onboarding

  m.getFlag = key => { try { return localStorage.getItem(key); } catch { return null; } };
  m.setFlag = (key, value) => { try { if (value == null) localStorage.removeItem(key); else localStorage.setItem(key, value); } catch { } };

  /** Viewport rectangle of the first visible element matching a selector, or null. */
  m.rectOf = selector => {
    for (const el of document.querySelectorAll(selector)) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) return { left: r.left, top: r.top, width: r.width, height: r.height };
    }
    return null;
  };
  m.viewport = () => ({ left: 0, top: 0, width: window.innerWidth, height: window.innerHeight });
})();
