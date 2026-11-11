// Relation map tab (RelationMapTab.razor): pan by dragging the background, zoom with the wheel or a trackpad pinch,
// fit and centre, and clicks, double-clicks and right-clicks on nodes routed to .NET. The view (pan and zoom) lives here
// while the user moves it and is reported to the server when a gesture ends, so re-renders keep it.
window.meridianRelationMap = (() => {
  const vp = host => host.querySelector("g.rm-vp");
  const apply = host => {
    const s = host._rm, g = vp(host);
    if (g) g.setAttribute("transform", `translate(${s.x.toFixed(2)},${s.y.toFixed(2)}) scale(${s.k.toFixed(4)})`);
  };
  const report = host => {
    clearTimeout(host._rmTimer);
    host._rmTimer = setTimeout(() => {
      const s = host._rm;
      host._rmNet?.invokeMethodAsync("OnView", s.x, s.y, s.k).catch(() => { });
    }, 150);
  };
  const call = (host, method, ...args) => host._rmNet?.invokeMethodAsync(method, ...args).catch(() => { });
  const zoomAt = (host, px, py, factor) => {
    const s = host._rm;
    const k = Math.min(4, Math.max(0.03, s.k * factor));
    s.x = px - (px - s.x) * k / s.k;
    s.y = py - (py - s.y) * k / s.k;
    s.k = k;
    apply(host);
    report(host);
  };

  return {
    install(host, net, x, y, k) {
      if (!host) return;
      host._rmNet = net;
      host._rm = { x, y, k };
      if (host._rmInstalled) return;
      host._rmInstalled = true;
      host.addEventListener("wheel", e => {
        e.preventDefault();
        const r = host.getBoundingClientRect();
        zoomAt(host, e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0015)));
      }, { passive: false });
      let drag = null;
      host.addEventListener("pointerdown", e => {
        if (e.button !== 0) return;
        drag = { sx: e.clientX, sy: e.clientY, x: host._rm.x, y: host._rm.y, moved: false, id: e.pointerId };
      });
      host.addEventListener("pointermove", e => {
        if (!drag) return;
        const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
        if (!drag.moved && Math.hypot(dx, dy) < 4) return;
        if (!drag.moved) {
          drag.moved = true;
          try { host.setPointerCapture(drag.id); } catch { }
          host.classList.add("panning");
        }
        host._rm.x = drag.x + dx;
        host._rm.y = drag.y + dy;
        apply(host);
      });
      const end = () => {
        if (drag?.moved) {
          host._rmSuppress = true;   // the click that ends a pan is not a click on a node
          setTimeout(() => { host._rmSuppress = false; }, 0);
          report(host);
        }
        host.classList.remove("panning");
        drag = null;
      };
      host.addEventListener("pointerup", end);
      host.addEventListener("pointercancel", end);
      host.addEventListener("click", e => {
        if (host._rmSuppress) return;
        const t = e.target.closest?.("[data-toggle],[data-stub],[data-node]");
        if (!t) { call(host, "OnBackground"); return; }
        if (t.hasAttribute("data-toggle")) call(host, "OnNodeToggle", t.getAttribute("data-toggle"));
        else if (t.hasAttribute("data-stub")) call(host, "OnStub", t.getAttribute("data-stub"));
        else call(host, "OnNodeClick", t.getAttribute("data-node"), e.shiftKey || e.metaKey || e.ctrlKey);
      });
      host.addEventListener("dblclick", e => {
        if (e.target.closest?.("[data-toggle],[data-stub]")) return;
        const t = e.target.closest?.("[data-node]");
        if (t) call(host, "OnNodeDouble", t.getAttribute("data-node"));
      });
      host.addEventListener("contextmenu", e => {
        e.preventDefault();
        const t = e.target.closest?.("[data-node]");
        if (t) call(host, "OnNodeMenu", t.getAttribute("data-node"), e.clientX, e.clientY);
      });
    },
    // Fits the whole map into the canvas (never above 150 %).
    fit(host) {
      const c = host?.querySelector("g.rm-content");
      if (!c || !host._rm) return;
      const b = c.getBBox(), w = host.clientWidth, h = host.clientHeight;
      if (!b.width || !b.height || !w || !h) return;
      const k = Math.min(1.5, Math.max(0.03, Math.min((w - 40) / b.width, (h - 40) / b.height)));
      host._rm = { k, x: (w - b.width * k) / 2 - b.x * k, y: (h - b.height * k) / 2 - b.y * k };
      apply(host);
      report(host);
    },
    zoomBy(host, factor) {
      if (!host?._rm) return;
      zoomAt(host, host.clientWidth / 2, host.clientHeight / 2, factor);
    },
    // Puts the map point (x, y) in the middle of the canvas (search: "find on map").
    center(host, x, y) {
      if (!host?._rm) return;
      const s = host._rm;
      s.x = host.clientWidth / 2 - x * s.k;
      s.y = host.clientHeight / 2 - y * s.k;
      apply(host);
      report(host);
    },
    uninstall(host) {
      if (!host) return;
      clearTimeout(host._rmTimer);
      host._rmNet = null;
    },
  };
})();
