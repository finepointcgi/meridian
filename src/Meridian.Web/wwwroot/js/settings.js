// Settings & theme plumbing (Settings/ThemeService.cs). The server renders the theme into <body> on each page load from
// the stored preferences and these cookies; this script applies live changes and remembers per-device values.
window.meridianSettings = (() => {
  const year = 60 * 60 * 24 * 365;
  const setCookie = (name, value) => {
    try { document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=${year}; samesite=lax`; } catch { }
  };
  const getCookie = name => {
    try {
      const m = document.cookie.split("; ").find(c => c.startsWith(name + "="));
      return m ? decodeURIComponent(m.slice(name.length + 1)) : null;
    } catch { return null; }
  };
  const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
  const systemDark = () => media ? media.matches : true;
  // The browser's colour scheme, for the server's next render of "Match system" (also the sign-in page).
  setCookie("meridian.scheme", systemDark() ? "dark" : "light");

  let schemeListener = null;
  let shortcut = null;
  const local = key => { try { return localStorage.getItem(key); } catch { return null; } };

  return {
    /** Applies the theme to <body> (dialogs and popovers) and remembers it for the next page load on this device. */
    apply: state => {
      const body = document.body;
      if (!body || !state) return;
      [...body.classList].filter(c => c.startsWith("m-")).forEach(c => body.classList.remove(c));
      state.bodyClass.split(" ").filter(Boolean).forEach(c => body.classList.add(c));
      for (const part of (state.style || "").split(";")) {
        const i = part.indexOf(":");
        if (i > 0) body.style.setProperty(part.slice(0, i).trim(), part.slice(i + 1).trim());
      }
      setCookie("meridian.theme", state.theme);
      setCookie("meridian.accent", state.accent);
      setCookie("meridian.device", state.device);
      const meta = document.querySelector('meta[name="theme-color"]');
      if (meta) meta.setAttribute("content", state.dark ? "#0a0f19" : "#e9edf4");
    },
    /** Starts following prefers-color-scheme and returns what this browser knows (for the one-time migration). */
    init: dotnet => {
      if (media && !schemeListener) {
        schemeListener = e => {
          setCookie("meridian.scheme", e.matches ? "dark" : "light");
          dotnet.invokeMethodAsync("OnSystemSchemeChanged", e.matches).catch(() => { });
        };
        media.addEventListener ? media.addEventListener("change", schemeListener) : media.addListener(schemeListener);
      }
      return {
        systemDark: systemDark(),
        device: getCookie("meridian.device"),
        legacy: { tourSeen: local("meridian.tour.v1") != null, lastProject: local("meridian.lastProject"), theme: local("meridian.theme") },
      };
    },
    setDevice: value => setCookie("meridian.device", value),
    forgetTour: () => { try { localStorage.removeItem("meridian.tour.v1"); } catch { } },
    /** ⌘, (Ctrl+, on Windows and Linux) opens Settings. */
    installShortcut: dotnet => {
      if (shortcut) document.removeEventListener("keydown", shortcut);
      shortcut = e => {
        if ((e.metaKey || e.ctrlKey) && !e.altKey && e.key === ",") {
          e.preventDefault();
          dotnet.invokeMethodAsync("OnShortcut", "Settings").catch(() => { });
        }
      };
      document.addEventListener("keydown", shortcut);
    },
    // ---------------------------------------------------------------- desktop app (Photino bridge)
    isDesktop: () => typeof window.external?.sendMessage === "function",
    /** Asks the desktop host for its settings; resolves with { autoOpen, checkForUpdates, recentFiles } or null. */
    desktopSettings: () => new Promise(resolve => {
      const ext = window.external;
      if (typeof ext?.sendMessage !== "function" || typeof ext?.receiveMessage !== "function") { resolve(null); return; }
      let done = false;
      const timer = setTimeout(() => { if (!done) { done = true; resolve(null); } }, 3000);
      ext.receiveMessage(raw => {
        let m; try { m = JSON.parse(raw); } catch { return; }
        if (m?.type === "desktop-settings" && !done) { done = true; clearTimeout(timer); resolve(m); }
      });
      ext.sendMessage(JSON.stringify({ command: "desktopSettings" }));
    }),
    desktop: (command, payload) => {
      if (typeof window.external?.sendMessage === "function") window.external.sendMessage(JSON.stringify({ command, ...(payload || {}) }));
    },
  };
})();
