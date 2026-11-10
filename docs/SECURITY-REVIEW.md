# Meridian security review

Scope: the merged code base at Meridian 0.2.0 — the Blazor Server team server (PostgreSQL or SQLite), the desktop host
(Photino, in-process server on 127.0.0.1 in local mode) and the deployment files. Method: source review of every
endpoint, Blazor handler and JS-invokable method, the parsers and exporters, the scripting and plugin hosts and the
desktop bridge, plus local probing of the app in SQLite server mode and in local mode (`curl`), and a regression test
per fix (`tests/Meridian.Tests/SecurityRegressionTests.cs`, `tests/Meridian.Web.Tests/SecurityRegressionTests.cs`).

## Threat model

| Actor | Can do | Must not be able to |
| --- | --- | --- |
| Anonymous network client (team server) | Reach `/login`, `/account/*`, `/api`, `/oslc`, the Blazor hub | Read or change any project, enumerate users, take over accounts, exhaust the server |
| Signed-in user (self-registration is on by default) | Create and own projects, edit projects shared with them, upload files, configure integrations on projects they own | Read or change projects they are not a member of, act above their role (viewer → edit/commit), run code on or crash the server, reach the server's internal network, attack other users' browsers |
| Project member with a malicious model | Put arbitrary strings in names, styles, ids, links (by editing, or by importing a crafted .meridian / XMI / SysML / ReqIF file) | Run script in other members' browsers (the canvas, report and exports are shared with owners and administrators) |
| Server administrator | Manage users, projects, backups, the audit log; opt in to scripting and plugins | — (trusted; scripting and plugins are admin-only code execution by design) |
| Other local processes / web pages on the desktop | Reach `127.0.0.1:<port>`, DNS-rebind a hostname to it | Use the local workspace without the per-launch token, import files, read models |
| A team server the desktop app connects to | Run its pages in the desktop window and post bridge messages | Read local files, start programs, install unverified updates |

Assets: model data and version history, account credentials and sessions, integration tokens and webhook secrets
(Data Protection), API tokens (hashed), the audit log, server availability.

## Findings

Severity is for a default team-server deployment unless stated. "Fixed" commits are on this branch; every fix has a
regression test named after its id (`SR01_…`, `SR03_…`).

| Id | Severity | Area | Finding | Resolution |
| --- | --- | --- | --- | --- |
| SR-01 | High | Output / XSS | Diagram SVG wrote shape `fill`/`stroke`/`text` colours, connector colours and stereotype colours unescaped into attributes, and node ids unescaped into `data-node`. The live canvas inserts this markup with `MarkupString`, so any editor (the colour input's value is client-controlled) or any imported project file could inject event-handler attributes that run in every member's session, including owners and administrators. Exported SVG link anchors also used the raw link value, and a `javascript:` value passed `IsExternalLink`. | Fixed in `fe3db09`: colours pass an allow-list (`SvgRenderer.SafeColor`: hex, named, `rgb()`/`hsl()`), ids are encoded, anchors use the normalised http(s) URL with `rel="noopener noreferrer"`. |
| SR-02 | High | Output / XSS | The HTML model report wrote element, diagram and rule ids unescaped and embeds the SVG above; it is opened from a `blob:` URL, which runs with the app's origin. | Fixed in `fe3db09`: ids encoded; the report carries `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:` so it cannot run script even if a future field is missed. |
| SR-03 | High | DoS / parsing | Unbounded recursion in the SysML v2 parser (run on every keystroke of the SysML text panel, for viewers too, via a JS-invokable method), the expression evaluator (guards, parametrics), the model query language, feature expressions and the XMI importer. Deep nesting overflowed the stack, which terminates the whole server process (not catchable). Confirmed by test: the original parser crashed the test host. | Fixed in `25710a6`: depth limits (SysML bodies 100 plus an execution-stack check; expressions/queries 200; XMI 200) reported as each parser's normal error. |
| SR-04 | High (when scripting is enabled) | Code execution | In server mode the script console was gated on "owner of the open project", but every user owns the projects they create (and self-registration is on by default). The script policy is documented as not a sandbox; at minimum any script can crash the process (unbounded recursion), so enabling scripting gave every account code execution / DoS. | Fixed in `5ac7553`: server mode requires a server administrator (state re-read before each run). `Meridian:Scripting:AllowProjectOwners=true` restores the old rule for deployments that want it. Docs updated. |
| SR-05 | Medium | SSRF | Webhook URLs (any http/https) and issue-tracker base URLs (https, or http on loopback) are set by project owners; the server POSTs/GETs them and reports status codes and errors back (an internal port scanner and a path to cloud metadata, admin ports and internal services; redirects were followed). | Fixed in `0e8cbea`: on team servers every connection, including redirects, resolves the host and connects only to an allowed address — link-local/metadata, unspecified and multicast always refused; loopback and private ranges refused unless `Meridian:Integrations:AllowPrivateNetworks=true`. Documented in docs/INTEGRATIONS.md. |
| SR-06 | Medium | Authorization | Viewers could commit versions (Ctrl/Cmd+S and the command palette call `Save`, which had no role check; the toolbar button was only `disabled`), which also fires `version.committed` webhooks. The collaboration session accepted model operations from any participant id without checking the role (the viewer's own replica was the only barrier), and several history/baseline/branch handlers relied only on disabled buttons. | Fixed in `5ac7553`: `VersionControl.CommitAsync` checks the stored project role; the session refuses operations from viewers and unknown participants and resyncs the tab; the dialog handlers check the role. |
| SR-07 | Medium | DoS / parsing | Uploaded archives were inflated without limits: `.fmu` (modelDescription.xml), `.reqifz`, zipped C# sources and `.xlsx` parts. A small zip bomb could exhaust server memory for all users. | Fixed in `db260c0` and `5ac7553`: entries are read through a size-limited stream (`SafeZip`, 64 MB per entry; 64 MB total for C# sources) and OpenXml parts are capped. |
| SR-08 | Low | Headers | No anti-framing (outside Blazor's own pages), `nosniff` or referrer policy on most responses (API, OSLC, account and admin endpoints). | Fixed in `793979f`: `X-Frame-Options: SAMEORIGIN`, `frame-ancestors 'self'`, `nosniff`, `Referrer-Policy: same-origin`; OSLC delegated dialogs stay frameable by design; endpoint CSPs (images, OSLC) are kept. |
| SR-09 | Low | Export | Requirements, traceability and table CSV exports wrote cells starting with `=`, `+`, `-`, `@` as-is (spreadsheet formula injection). Diff and audit CSVs were already guarded. | Fixed in `793979f`: such cells get a leading apostrophe; CSV import strips it again so round trips are unchanged. |
| SR-10 | Low | Local mode | After a valid launch token the redirect used the request path as-is, so a protocol-relative path sent the window to another site. | Fixed in `793979f` (`MeridianServer.LocalRedirectPath`). |
| SR-11 | Low | Deployment | `Meridian:Security:TrustForwardedHeaders` clears the known-proxy list, so a client that can reach Kestrel directly can spoof `X-Forwarded-For` (per-IP sign-in rate limit, audit IPs) and `X-Forwarded-Proto/Host`. | Accepted: intended for deployments where only the reverse proxy can reach the app (compose binds to 127.0.0.1). Recommendation: add a known-proxies setting. |
| SR-12 | Low | Accounts | `Meridian:Admins` names usernames that become administrators whenever such an account exists; if a listed name has not been created yet, the first person to self-register (or an SSO user whose claim-derived username matches) gets it. On a fresh server the first registered account becomes administrator. | Accepted (documented behaviour). Recommendation: create listed admins before opening registration, use `AdminClaim` for SSO, set `AllowRegistration=false` on internet-facing servers. |
| SR-13 | Low | CSRF | `/account/login` and `/account/register` are plain form posts without antiforgery tokens (login CSRF), and `/account/logout` accepts GET. Session cookies are `HttpOnly`, `SameSite=Lax`. | Accepted: impact limited to signing a victim in/out. |
| SR-14 | Info | Parsing | XML importers use `XDocument.Parse/Load` defaults: DTDs are parsed but external entities are not resolved and entity expansion is capped at 10 M characters. | Accepted; verified by the `SR14_…` test (`0a467dd`) so a future change of reader settings is caught. |
| SR-15 | Low | Auth | HTTP Basic with username/password on `/api` and `/oslc` is not covered by the per-IP sign-in rate limit (per-account lockout still applies, so password spraying across accounts is possible). | Accepted. Recommendation: disable password Basic auth (`DisableLocalPasswords`, or a dedicated switch) and use personal access tokens. |
| SR-16 | Low | DoS | SignalR `MaximumReceiveMessageSize` is 64 MB (needed for pasted images and uploads), so a signed-in client can make the server buffer large messages. | Accepted; rely on reverse-proxy limits. |
| SR-17 | Low | Secrets / deployment | The Data Protection key ring (protects session cookies, integration tokens, webhook secrets) is stored unencrypted in `Meridian:KeysPath`; `docker-compose.yml` uses a fixed development database password; the container serves plain HTTP (TLS is expected at a proxy). | Accepted for the dev stack. Recommendation: `ProtectKeysWith…` (certificate / cloud KMS), secrets from the environment, TLS at the proxy with `TrustForwardedHeaders`. |
| SR-18 | Low | Desktop updater | The installer is verified against `SHA256SUMS.txt` from the same GitHub release (integrity over TLS, not authenticity: whoever can publish a release can ship both). Asset names are matched; downloads go to a fresh temp folder. | Accepted. Recommendation: sign installers (Authenticode / notarisation) or sign the checksum file. |
| SR-19 | Low | Local mode | The local-mode sign-in gate lets through any request whose path has a file extension (for static assets), which also admits routes with a dotted last segment. Every such endpoint authenticates itself (verified: `/api/…/elements/a.b` answers 401). Host filtering (`AllowedHosts=127.0.0.1;localhost`) rejects DNS-rebinding requests (verified: foreign `Host` answers 400), and the session cookie is bound to 127.0.0.1. | Accepted. Recommendation: only bypass for `MapStaticAssets` paths. |
| SR-20 | Info | Scripting | With scripting enabled, the compile-time policy is a guard rail: a script can still exhaust CPU/memory or crash the process (recursion, `Task.Run`, large allocations). Reflection, `dynamic`, `Type`, `Activator`, `AppDomain`, `Assembly`, `InteropServices`, aliased usings and `extern alias` were checked and are blocked; only six assemblies are referenced. | Accepted (documented "not a sandbox"); mitigated by SR-04. Recommendation: run scripts out of process if non-admins must use them. |
| SR-21 | Low | Parsing | `.qea` (Enterprise Architect) imports open an uploaded SQLite file; a crafted database can make the import slow (views, large tables). Extension loading is off and the file is a private temp copy. | Accepted. |
| SR-22 | Low | Data freshness | A user's project role is read when they open a project; a demotion takes effect on their next open (server-side checks added in SR-06 re-read the role for commits). Admin pages check the role when they render. | Accepted. |
| SR-23 | Info | Dependencies | `AngleSharp` 1.2.0 (moderate advisory) is pulled in by bUnit in the test project only. | Accepted (not shipped). |
| SR-24 | Low | Auth | `/oslc` accepted username/password HTTP Basic even with `Meridian:Sso:DisableLocalPasswords=true` ("single sign-on only"); `/api` already refused it. | Fixed in `0a467dd`. |

### Areas reviewed with no finding

- **Authentication**: PBKDF2-SHA256 (210k), constant-time comparison, timing-equalised unknown users, lockout, per-IP
  rate limit on password and SSO start, disabled accounts lose cookie and token access immediately (validated per
  request with a 15 s cache).
- **SSO**: accounts are linked by issuer + subject only (never by e-mail/username), PKCE, code flow, the middleware's
  correlation and nonce checks, fixed post-login redirect, auto-provisioning and admin-claim mapping as documented.
- **Object-level authorization**: `/api` (SysML v2) and `/oslc` check membership per project and scope versions by
  project; admin endpoints check the live admin state; scheduled backup downloads only list files the scheduler wrote;
  `/_img/<sha>` requires sign-in and is content-addressed with a sandbox CSP. Reviews, comments, mentions and
  notifications are always looked up through the open project or the current user (reviews opened by id check the
  project). Blazor Server only dispatches events for rendered handlers, and MudBlazor buttons ignore clicks while
  disabled, so UI-only gating was reviewed handler by handler (SR-06 lists the gaps).
- **SQL**: both stores use parameters; the only interpolations are constant column lists, enum values and
  server-built filter fragments.
- **Secrets**: integration tokens and webhook secrets are Data-Protection encrypted, never returned to the browser;
  HTTP client header logging is redacted; API tokens are 256-bit random, stored as SHA-256; error pages show no
  details outside Development.
- **Desktop**: the import endpoint needs loopback + the launch token; the bridge opens only http(s) URLs, reopens only
  files already in the recent list and saves only through the native save dialog.
- **Not present in this code base**: an AI assistant, FMU co-simulation / an FMU host process (FMI import reads
  `modelDescription.xml` only), OpenTelemetry/request logging, and a Helm chart.

## Remaining recommendations

1. Set `Meridian:Security:AllowRegistration=false` (or SSO with `DisableLocalPasswords`) on internet-facing servers and
   create the configured administrators before opening the server.
2. Terminate TLS at a reverse proxy, keep Kestrel unreachable from clients when `TrustForwardedHeaders` is on, and set
   `Meridian:PublicUrl` and `AllowedHosts` (OSLC URLs otherwise follow the request's `Host`).
3. Protect the Data Protection key ring at rest and back it up with the database.
4. Keep scripting off on shared servers; if needed, keep it admin-only and consider an out-of-process runner with
   CPU/memory limits.
5. Add a full Content-Security-Policy for the app pages (Blazor Server works with `script-src 'self'`; MudBlazor and
   the Google Fonts stylesheet need `style-src` allowances) as defence in depth for SR-01-type bugs.
6. Sign desktop installers and the checksum file.

## Second pass

Scope: code the first pass did not cover: the AI assistant (`src/Meridian.Assistant`, `AssistantServices`,
`AssistantPanel`), FMI co-simulation (`src/Meridian.FmuHost`, `FmuRuntime`, `CoSimulation`, `CoSimTab`), collaborative
SysML v2 text (`ProjectSession.SharedText`, `TextOperation`, `SysmlTextPanel`, `sysml-editor.js`), the MagicDraw
.mdzip/.mdxml importer, report generation and the template editor, diagram images (`/_img/<sha>`), the deployment files
(Dockerfile, `docker-compose.prod.yml`, `deploy/compose`, `deploy/helm/meridian`, `OperationsHosting`), the mobile shell
and PWA manifest, view tabs and the desktop `open-url` bridge. Method as before: source review, local probing of the
app in SQLite server mode (`curl`: health endpoints, `/_img`, request log), and a regression test per fix
(`tests/Meridian.Tests/SecondPassSecurityTests.cs`, `tests/Meridian.Web.Tests/SecondPassSecurityTests.cs`). The real
Anthropic API was never called (tests use a fake HTTP handler).

| Id | Severity | Area | Finding | Resolution |
| --- | --- | --- | --- | --- |
| SR-25 | High | DoS / parsing | The MagicDraw importer (any signed-in user can import a project) walks the XML recursively: nested packages (`StreamContainer`), nested elements (`Handle` → `HandleChildren`) and diagram views (`ParseViews`). A small crafted .mdxml/.mdzip with deep nesting overflowed the stack, which ends the whole server process. Confirmed by test (the test host crashed with "Stack overflow"). | Fixed: every MagicDraw XML read goes through `DepthLimitedXmlReader` (256 levels), reported as the importer's normal "malformed" error (model) or a skipped-diagram warning (symbol data). DTDs stay prohibited, no resolver. |
| SR-26 | Medium | DoS / parsing | MagicDraw archives were inflated without limits: model parts, diagram symbol entries, and gzip / nested-zip symbol data up to three levels deep (a multi-layer zip bomb). | Fixed: model parts are read through `SafeZip` (1 GB each), symbol data (also after gunzip / inner zip) through a 64 MB limit. |
| SR-27 | Medium | DoS | Report templates are model data (any editor can save one) and loops nest without bound: a few nested `forEach` blocks over every element multiply into billions of iterations and an unbounded document, in the full report and in the template editor's live preview (which runs whenever a member opens the template). This could hold server threads and exhaust memory. | Fixed: `ResolveOptions.MaxIterations` (1 M loop iterations in all) and `MaxNodes` (500 k paragraphs/tables/figures); the preview uses 5 000 / 20 000. Exceeding a budget is reported as "The document is too large". |
| SR-28 | Medium | Collaboration | Shared SysML text: (a) the document size and the operation history were unbounded, so an editor could grow server memory without limit (each history entry keeps its inserted text, and unapplied documents outlive their editors); (b) operation lengths from the browser were added without overflow checks; (c) a viewer could discard every editor's unapplied edits (Reload calls `ResetText`) and dismiss the "model changed" flag, and the session relied on the panel alone to refuse viewers' operations. | Fixed: client operations may not grow the text past 8 M characters; the history keeps the last 1 000 operations / 16 M inserted characters (older base revisions are refused and the client resynchronises); lengths use checked arithmetic and fractional components are rejected; the session refuses operations, resets, applies and stale-dismissals from participants without edit rights (a viewer's Reload only resynchronises their own tab). |
| SR-29 | Medium (High when FMI is enabled) | Code execution | With `Meridian:Fmi:Enabled=true` on a server, running FMUs (native code in a child process, documented as not a sandbox) and attaching `.fmu` files were gated on "owner of the open project". Every account owns the projects it creates, so enabling FMI gave every user code execution on the server (the SR-04 pattern). | Fixed: on servers only server administrators may run or attach FMUs (admin state re-read before each run/attach); `Meridian:Fmi:AllowProjectOwners=true` restores the owner rule. The desktop app keeps the owner rule. `Enabled` still defaults to false on servers. docs/FMI.md updated. |
| SR-30 | Low | FMI | `modelIdentifier` from the FMU's modelDescription was joined into the library path, so `../../…` could load a library outside `binaries/`; extraction trusted the zip header's sizes; host output lines were read without a length limit (an FMU printing a huge line without a newline grew server memory). | Fixed: the identifier must be a C identifier (as the standard requires); extraction counts inflated bytes against the 2 GB budget; host lines are cut at 4 MB, log lines at 2 000 characters. Zip-slip and the 64-hex hash check of the FMU store were verified (test). |
| SR-31 | Low | AI assistant / cost | On servers the administrator's API key pays for every request, but a prompt (or pasted specification) and a conversation could grow without limit (each turn resends the whole history). `AssistantSettings.RemoveKey` lacked the local-mode check `SaveKey` has (the UI only offers it in local mode). | Fixed: prompts over 200 000 characters and conversations over 400 API messages are refused with a notice, before any request; `RemoveKey` is local-mode only. Output is already capped (`MaxTokens`, 24 tool rounds per turn). |
| SR-32 | Low | Deployment | `.env.example` ships `POSTGRES_PASSWORD=change-me`; compose only checks that it is set. PostgreSQL is reachable only on the internal `backend` network. Plugins default to enabled in the production compose file and Helm values (the plugin folder is a read-only volume, empty unless an administrator adds assemblies). | Accepted. Recommendation: generate the password (`openssl rand -base64 24`) and set `MERIDIAN_PLUGINS_ENABLED=false` unless plugins are used. |
| SR-33 | Low | FMI | The FMU store is server-wide and content-addressed; a project editor can point a block's `fmi.sha256` tag at any stored FMU (for example one an administrator attached in another project), which then runs when an administrator runs that system. | Accepted: only administrators can store and run FMUs (SR-29). |
| SR-34 | Info | Telemetry | OpenTelemetry (opt-in) uses ASP.NET Core / HttpClient instrumentation 1.20, which redacts query-string values by default; `RecordException` records exception messages; `ExportLogs` (off by default) exports formatted log messages. The bundled collector config also drops `url.query`, cookie and authorization attributes. The assistant's SDK client has its own telemetry turned off. | Accepted. |
| SR-35 | Info | Scripting UI | The script console and plugins dialog still said "owners only" after SR-04 made server scripting administrator-only. | Fixed (wording). |

### Areas reviewed with no finding (second pass)

- **AI assistant**: the key is read from configuration/environment on servers and only saved (Data Protection,
  purpose `Meridian.Assistant.ApiKey.v1`) in local mode; it is never rendered (password input, no echo), logged or put
  in error text, and the SDK's telemetry is off. The enabled/key checks are re-evaluated server-side on every turn.
  Write tools only add proposals to a change set; nothing touches the model until the user clicks Apply on the review
  card, and Apply checks the role (`Workspace.ReadOnly`, `History.ReadOnly`, element locks) while the session refuses
  viewers' operations (SR-06), so prompt injection through element text can at most produce proposals the user sees.
  Answers are parsed by `MarkdownLite` into a tree rendered by Blazor (no `MarkupString`); only http(s) links become
  anchors (`noopener noreferrer`) and `el:` mentions resolve only to elements of the open model. Conversations are
  keyed by user + project + branch on the server.
- **Collaborative text**: caret updates are accepted only from participants that joined that text and are clamped to
  the text; display names and colours are escaped / validated in `sysml-editor.js`.
- **Reports**: every model string in the HTML writer is HTML-encoded, figures come from the SR-01-hardened SVG
  renderer, the preview iframe has `sandbox="allow-same-origin"` (no scripts) and the generated report carries the SR-02
  CSP. Template JSON import is limited to 4 MB and System.Text.Json's default depth of 64.
- **Images**: data URIs must be PNG/JPEG/GIF/WebP/SVG under 2 MB; `/_img/<sha>` requires sign-in, is content-addressed
  and serves with a `sandbox` CSP and `nosniff` (verified: anonymous requests are redirected to sign-in). SVG pictures
  are only drawn through `<image>`.
- **Deployment**: the image runs as a non-root user with a read-only root filesystem; compose drops all capabilities
  and sets `no-new-privileges`; only Caddy publishes ports; Caddy's admin API is off and it does not log credentials;
  Helm uses `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: [ALL]`, `RuntimeDefault` seccomp, no service-account token,
  secrets in a Secret (or an existing one) and `required` for the database password. Health endpoints answer only
  `Healthy`/`Unhealthy` (readiness hides the exception); the request log writes method, path, status and duration only
  (verified: query strings are not logged).
- **Mobile shell and PWA**: the mobile components only navigate, select and comment; edit mode is refused for
  viewers and every model change goes through the same workspace and session checks as the desktop UI. The manifest
  has no service worker.
- **View tabs and desktop bridge**: view tabs are per-circuit state (nothing crosses users); the desktop `open-url`
  message opens only absolute http(s) URLs with a host through the shell.
