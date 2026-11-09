using System.Net;

namespace Meridian.Desktop;

/// <summary>The desktop start screen: work on this computer, or connect to a team server.</summary>
internal static class StartPage
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Html(DesktopSettings settings, string? error = null, bool busy = false)
    {
        var recent = string.Join("", settings.Servers.Select(s =>
            $"""<div class="recent"><button class="link" data-url="{E(s)}">{E(s.Replace("https://", "").Replace("http://", "").TrimEnd('/'))}</button><button class="x" title="Forget" data-forget="{E(s)}">×</button></div>"""));
        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Meridian</title>
<style>
:root { --bg:#0a0f19; --panel:#131b2b; --panel2:#172132; --border:rgba(148,163,184,.16); --text:#e7ecf6; --text2:#a3aec4; --text3:#6d7a93; --accent:#4f8cff; --teal:#2dd4bf; --danger:#ff6b6b; }
@media (prefers-color-scheme: light) { :root { --bg:#eef2f8; --panel:#fff; --panel2:#f5f7fb; --border:rgba(15,23,42,.12); --text:#141b2b; --text2:#4c586d; --text3:#8a94a7; } }
* { box-sizing:border-box; }
body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
  font:14px/1.45 Inter, -apple-system, "Segoe UI", system-ui, sans-serif; background-image:radial-gradient(rgba(79,140,255,.10), transparent 60%); }
.wrap { width:min(860px, 100% - 48px); }
.brand { display:flex; align-items:center; gap:12px; margin-bottom:6px; }
.logo { width:40px; height:40px; border-radius:12px; background:linear-gradient(135deg,#4f8cff,#3b6cf0 55%,#2dd4bf 140%); display:grid; place-items:center; box-shadow:0 6px 20px rgba(79,140,255,.35); }
h1 { font-size:26px; margin:0; letter-spacing:-.01em; }
.sub { color:var(--text2); margin:0 0 26px 52px; }
.cards { display:grid; grid-template-columns:1fr 1fr; gap:18px; }
@media (max-width:720px) { .cards { grid-template-columns:1fr; } }
.card { background:var(--panel); border:1px solid var(--border); border-radius:16px; padding:22px; display:flex; flex-direction:column; gap:12px; }
.card h2 { margin:0; font-size:16px; display:flex; align-items:center; gap:8px; }
.card p { margin:0; color:var(--text2); }
.card ul { margin:0; padding-left:18px; color:var(--text3); font-size:12.5px; }
button.primary { height:38px; border:none; border-radius:10px; background:linear-gradient(135deg,#4f8cff,#3b6cf0 55%,#2dd4bf 140%); color:#fff; font:600 13.5px inherit; font-family:inherit; cursor:pointer; }
button.primary:hover { filter:brightness(1.08); }
input[type=text] { height:38px; border-radius:10px; border:1px solid var(--border); background:var(--panel2); color:var(--text); padding:0 12px; font:inherit; outline:none; }
input[type=text]:focus { border-color:var(--accent); box-shadow:0 0 0 3px rgba(79,140,255,.2); }
.spacer { flex:1; }
.recent { display:flex; align-items:center; gap:6px; }
.link { background:none; border:none; color:var(--accent); cursor:pointer; padding:2px 0; font:inherit; text-align:left; flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.x { background:none; border:none; color:var(--text3); cursor:pointer; font-size:16px; }
.label { font-size:10.5px; font-weight:700; letter-spacing:.08em; text-transform:uppercase; color:var(--text3); }
.remember { margin-top:18px; color:var(--text2); font-size:13px; display:flex; gap:8px; align-items:center; }
.error { margin-bottom:16px; padding:10px 14px; border-radius:10px; background:rgba(255,107,107,.12); border:1px solid rgba(255,107,107,.4); color:var(--danger); }
.busy { text-align:center; color:var(--text2); margin-top:30px; }
.spin { width:26px; height:26px; border-radius:50%; border:3px solid var(--border); border-top-color:var(--accent); animation:s 1s linear infinite; margin:0 auto 10px; }
@keyframes s { to { transform:rotate(360deg); } }
</style></head>
<body><div class="wrap">
  <div class="brand"><div class="logo"><svg width="22" height="22" viewBox="0 0 24 24" fill="#fff"><path d="M12 2 3 7v10l9 5 9-5V7l-9-5Zm0 2.3 6.7 3.7L12 11.7 5.3 8 12 4.3ZM5 9.7l6 3.3v6.7l-6-3.3V9.7Zm8 10v-6.7l6-3.3v6.7l-6 3.3Z"/></svg></div><h1>Meridian</h1></div>
  <p class="sub">Model-based systems engineering — SysML, requirements, BPMN, ArchiMate and free drawing.</p>
  {{(error != null ? $"<div class=\"error\">{E(error)}</div>" : "")}}
  {{(busy ? "<div class=\"busy\"><div class=\"spin\"></div>Starting your local workspace…</div>" : $$"""
  <div class="cards">
    <div class="card">
      <h2>💻 This computer</h2>
      <p>Work offline in a private workspace. Projects are stored in a database file on this machine.</p>
      <ul><li>No account or network needed</li><li>Version history and comments included</li></ul>
      <div class="spacer"></div>
      <button class="primary" id="local">Open local workspace</button>
    </div>
    <div class="card">
      <h2>🌐 Team server</h2>
      <p>Connect to a Meridian server to share projects and edit together in real time.</p>
      <input type="text" id="url" placeholder="https://meridian.example.com" value="{{E(settings.Servers.FirstOrDefault() ?? "")}}" />
      <button class="primary" id="connect">Connect</button>
      {{(recent.Length > 0 ? $"<div class=\"label\">Recent servers</div>{recent}" : "")}}
    </div>
  </div>
  <label class="remember"><input type="checkbox" id="remember" {{(settings.AutoOpen ? "checked" : "")}} /> Open my choice automatically next time (switch any time from your avatar menu)</label>
  """)}}
</div>
<script>
  const send = m => window.external.sendMessage(JSON.stringify(m));
  const remember = () => document.getElementById('remember')?.checked ?? false;
  const busy = () => { document.querySelector('.wrap').insertAdjacentHTML('beforeend', '<div class="busy"><div class="spin"></div>Opening…</div>'); };
  document.getElementById('local')?.addEventListener('click', () => { busy(); send({ command: 'local', remember: remember() }); });
  const connect = url => { busy(); send({ command: 'server', url, remember: remember() }); };
  document.getElementById('connect')?.addEventListener('click', () => connect(document.getElementById('url').value));
  document.getElementById('url')?.addEventListener('keydown', e => { if (e.key === 'Enter') connect(e.target.value); });
  document.querySelectorAll('[data-url]').forEach(b => b.addEventListener('click', () => connect(b.dataset.url)));
  document.querySelectorAll('[data-forget]').forEach(b => b.addEventListener('click', () => send({ command: 'forget', url: b.dataset.forget })));
</script>
</body></html>
""";
    }
}
