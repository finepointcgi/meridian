# Deploying Meridian

This runbook covers running the Meridian server in production: with Docker Compose on one host, or with Helm on
Kubernetes. The desktop app's local mode needs none of this.

- [Architecture](#architecture)
- [Install with Docker Compose](#install-with-docker-compose)
- [Install with Helm](#install-with-helm)
- [Configuration reference](#configuration-reference)
- [TLS](#tls)
- [First administrator and SSO](#first-administrator-and-sso)
- [Backups and restore](#backups-and-restore)
- [Upgrades, migrations and rollback](#upgrades-migrations-and-rollback)
- [Scaling](#scaling)
- [Monitoring](#monitoring)
- [Troubleshooting](#troubleshooting)
- [Hardening checklist](#hardening-checklist)

## Architecture

```
browser ──HTTPS/WebSocket──▶ reverse proxy (Caddy / ingress) ──HTTP──▶ Meridian :8080 ──▶ PostgreSQL 17
                                  TLS, X-Forwarded-*                       │
                                                                           ├─ /data/keys     Data Protection keys (sign-in cookies)
                                                                           ├─ /data/backups  in-app scheduled backups
                                                                           └─ /data/plugins  server plugins (read-only)
```

- Meridian is a Blazor Server app. Every open browser tab keeps a WebSocket ("circuit") to one server process.
- PostgreSQL is the system of record. The schema is created and upgraded automatically at startup.
- Live collaboration on a project runs inside one server process. It holds the authoritative model in memory and
  writes it to the database shortly after each change and on shutdown.

### The container image

The image is `ghcr.io/finepointcgi/meridian:<version>`. It is published for linux/amd64 and linux/arm64 on every
`v*` tag by `.github/workflows/container.yml`. Each image has an SBOM, SLSA provenance, a GitHub attestation and a
cosign keyless signature. Verify an image before you deploy it:

```bash
cosign verify ghcr.io/finepointcgi/meridian:0.7.0 \
  --certificate-identity-regexp '^https://github.com/finepointcgi/meridian/\.github/workflows/container\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
gh attestation verify oci://ghcr.io/finepointcgi/meridian:0.7.0 --repo finepointcgi/meridian
```

The repository is private, so pulling needs `docker login ghcr.io` with a token that has `read:packages`
(or a Kubernetes `imagePullSecret`).

#### From the GitLab Container Registry

When Meridian is built on GitLab, the `container` job of `.gitlab-ci.yml` publishes the same multi-arch image, with
an SBOM and SLSA provenance, to the project's registry: `registry.gitlab.com/<group>/<project>:<version>`, or
`<your-registry-host>/<group>/<project>` on self-managed GitLab. Tags are `X.Y.Z`, `X.Y`, `X` (not for 0.x),
`latest` (not for pre-releases) and the commit SHA. On GitLab.com it is signed with cosign keyless through GitLab's
OIDC token:

```bash
cosign verify registry.gitlab.com/<group>/<project>:0.7.0 \
  --certificate-identity "https://gitlab.com/<group>/<project>//.gitlab-ci.yml@refs/tags/v0.7.0" \
  --certificate-oidc-issuer https://gitlab.com
```

To use it:

- **Compose:** set `MERIDIAN_IMAGE=registry.gitlab.com/<group>/<project>` in `.env`, and run `docker login
  registry.gitlab.com` with a deploy token or personal access token that has `read_registry`.
- **Helm:** `--set image.repository=registry.gitlab.com/<group>/<project>` and an `imagePullSecrets` entry for a
  `kubernetes.io/dockerconfigjson` Secret created from a deploy token
  (`kubectl create secret docker-registry gitlab-registry --docker-server=registry.gitlab.com --docker-username=<token name> --docker-password=<token>`).

What the image provides:

- Ubuntu 24.04 ASP.NET 10 runtime with fontconfig and DejaVu fonts for PDF/Word export, and nothing else added.
  Chiseled and Alpine bases were ruled out: QuestPDF needs fontconfig and has no linux-musl-arm64 build.
- Runs as the non-root user `app` (UID/GID 1654).
- Works with a read-only root filesystem. Mount a writable tmpfs on `/tmp`.
- Listens on port 8080 over HTTP. TLS is the reverse proxy's job.
- Has a `HEALTHCHECK` that runs `dotnet Meridian.Web.dll --healthcheck`, so the image needs no curl.
- Defines volumes at `/data/keys`, `/data/backups` and `/data/plugins`.
- Build it yourself with:
  `docker build --build-arg VERSION=0.7.0 --build-arg REVISION=$(git rev-parse HEAD) -t meridian:0.7.0 .`

## Install with Docker Compose

`docker-compose.prod.yml` runs four kinds of service:

- **caddy**: automatic HTTPS. This is the only service that publishes ports.
- **meridian**: the server. It runs read-only, with all capabilities dropped and `no-new-privileges` set.
- **db**: PostgreSQL 17. It is on an internal network and has no published port.
- **Optional profiles**: `backup` (scheduled `pg_dump`) and `otel` (an OpenTelemetry collector).

Requirements:

- A Linux host with Docker Engine 24+ and the Compose v2 plugin, 2 vCPU and 4 GB RAM.
- A DNS name pointing at the host.
- Ports 80 and 443 open (Let's Encrypt uses port 80 for the HTTP challenge).

```bash
git clone https://github.com/finepointcgi/meridian.git && cd meridian   # or copy just docker-compose.prod.yml, .env.example and deploy/compose/
cp .env.example .env && chmod 600 .env
# Edit .env: MERIDIAN_DOMAIN, ACME_EMAIL, POSTGRES_PASSWORD (openssl rand -base64 32 | tr -d '/+='), MERIDIAN_VERSION
docker login ghcr.io                                   # private registry
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
docker compose -f docker-compose.prod.yml ps           # meridian should become "healthy"
curl -fsS https://$MERIDIAN_DOMAIN/readyz              # → Healthy
```

To add the optional services:

```bash
docker compose -f docker-compose.prod.yml --profile backup up -d     # pg_dump every PG_BACKUP_INTERVAL_SECONDS
docker compose -f docker-compose.prod.yml --profile otel up -d       # + set MERIDIAN_OTLP_ENDPOINT=http://otel-collector:4317
```

To build the image on the host instead of pulling it, use
`docker compose -f docker-compose.prod.yml up -d --build`.

To install plugins, copy them into the read-only plugins volume, then restart:

```bash
docker run --rm -v meridian_meridian-plugins:/p -v "$PWD/my-plugins:/src:ro" alpine sh -c 'cp -r /src/. /p/ && chown -R 1654:1654 /p'
docker compose -f docker-compose.prod.yml restart meridian
```

Volumes are named `<project>_<volume>`, and the default project is `meridian`:

| Volume | Contents | Back up? |
| --- | --- | --- |
| `pg-data` | PostgreSQL data directory | Use `pg_dump` (below), not a file copy of a running database |
| `meridian-keys` | Data Protection key ring | Yes. If you lose it, everyone is signed out and personal access tokens may stop working |
| `meridian-backups` | In-app scheduled backup zips | Copy off-host |
| `pg-backups` | `pg_dump` files from the `backup` profile | Copy off-host |
| `meridian-plugins` | Server plugins | Keep the plugin sources in version control instead |
| `caddy-data` | Certificates and ACME account | Optional. Caddy will reissue certificates, within rate limits |

## Install with Helm

The chart is in `deploy/helm/meridian`. Its defaults are:

- one replica
- a Recreate update strategy
- ingress-nginx with cert-manager
- an external PostgreSQL

```bash
cd deploy/helm/meridian && helm dependency build && cd -    # fetches the optional Bitnami PostgreSQL chart
kubectl create namespace meridian
kubectl -n meridian create secret docker-registry ghcr --docker-server=ghcr.io --docker-username=<user> --docker-password=<token>

# Recommended: keep the connection string in a Secret you manage (External Secrets, Sealed Secrets, SOPS…)
kubectl -n meridian create secret generic meridian-env \
  --from-literal='ConnectionStrings__Meridian=Host=pg.internal;Database=meridian;Username=meridian;Password=<pw>;SSL Mode=Require'

helm upgrade --install meridian deploy/helm/meridian -n meridian \
  --set image.tag=0.7.0 \
  --set 'imagePullSecrets[0].name=ghcr' \
  --set ingress.host=meridian.example.com \
  --set ingress.annotations."cert-manager\.io/cluster-issuer"=letsencrypt-prod \
  --set secrets.existingSecret=meridian-env
```

The chart has three ways to connect to the database:

| Mode | Values |
| --- | --- |
| External database with your own Secret (recommended) | `secrets.existingSecret=<name>`. The Secret holds `ConnectionStrings__Meridian`, plus any `Meridian__Sso__Providers__N__ClientSecret` keys |
| External database, chart-managed Secret | `externalDatabase.host`, `.password`, `.database`, `.username`, `.options` |
| Bundled Bitnami PostgreSQL 17 (evaluation and small installs) | `postgresql.enabled=true`, `postgresql.auth.password=<pw>` (or `postgresql.auth.existingSecret`) |

Bitnami moved its free images out of `docker.io/bitnami` in 2025. If the bundled database image cannot be pulled,
see the comment under `postgresql:` in `values.yaml`, or use an operator such as CloudNativePG instead.

What the chart creates:

- **Deployment**:
  - startup, liveness and readiness probes (`/healthz`, `/healthz`, `/readyz`)
  - requests and limits
  - a restricted pod security context (non-root, read-only root filesystem, `RuntimeDefault` seccomp, all capabilities dropped)
  - an `emptyDir` on `/tmp`
  - no service-account token
- **Service** (ClusterIP).
- **Ingress** with TLS from cert-manager, plus sticky-session and WebSocket timeout annotations for ingress-nginx.
- **ConfigMap** for settings and a **Secret** for passwords. Pods restart when either changes (checksum annotations).
- **PVCs** for keys, backups and plugins. They are annotated `helm.sh/resource-policy: keep`, so they survive
  `helm uninstall`.
- **PodDisruptionBudget**, only rendered when more than one replica can run.
- **HorizontalPodAutoscaler**, disabled by default. Read [Scaling](#scaling) before turning it on.
- **NetworkPolicy**, optional (`networkPolicy.enabled`). It allows traffic only from the ingress controller namespace.

To check the chart, run `helm lint deploy/helm/meridian`, then
`helm template m deploy/helm/meridian --set secrets.existingSecret=x`.

## Configuration reference

Settings use ASP.NET Core configuration, so you can set them in `appsettings.json`, as environment variables
(`:` becomes `__`, for example `Meridian__Security__AllowRegistration=false`) or as command-line arguments. In the
compose `.env` file, every setting has a short variable that `docker-compose.prod.yml` maps to the variable below.

| Setting (environment variable) | Default | Description |
| --- | --- | --- |
| `ConnectionStrings__Meridian` | none | Npgsql connection string. Required unless `Meridian__Storage=sqlite`. Add `SSL Mode=Require` for remote databases |
| `Meridian__Storage` | postgres | `sqlite` for a single-file database in `DataPath` (trials only) |
| `Meridian__DataPath` | `/data` in the image | Base folder (SQLite file, defaults for local mode) |
| `Meridian__KeysPath` | `/data/keys` in the image | Data Protection key ring folder. Must be persistent, and shared between replicas |
| `Meridian__PublicUrl` | from the request | External base URL, such as `https://meridian.example.com`, used in OSLC and integration links |
| `Meridian__Security__TrustForwardedHeaders` | false | Honour `X-Forwarded-For/-Proto/-Host`. Set it to **true** behind Caddy or an ingress, and make sure nothing else can reach port 8080 |
| `Meridian__Security__AllowRegistration` | true | Self sign-up from the login page. Set it to **false** on the internet |
| `Meridian__Admins` / `Meridian__Security__Admins` | none | Usernames that are always administrators (comma-separated) |
| `Meridian__Security__MaxFailedLogins` | 5 | Failed passwords before lockout (0 = never) |
| `Meridian__Security__LockoutMinutes` | 15 | Lockout duration |
| `Meridian__Security__LoginAttemptsPerMinute` | 10 | Sign-in rate limit per client IP |
| `Meridian__Sso__Providers__N__{Name,DisplayName,Authority,ClientId,ClientSecret}` | none | OpenID Connect providers ([SSO](#first-administrator-and-sso)) |
| `Meridian__Sso__Providers__N__Scopes__i`, `…__ClaimMappings__{Username,UsernameFallback,DisplayName,Email,AdminClaim,AdminValue}`, `…__AutoProvision`, `…__Preset` | see `Admin/AdminOptions.cs` | Provider details. `Preset=gitlab` (or the name `gitlab`) fills in GitLab defaults ([Sign in with GitLab](#sign-in-with-gitlab)) |
| `Meridian__Sso__DisableLocalPasswords` | false | SSO only (no passwords, registration or HTTP Basic API auth) |
| `Meridian__Backup__Folder` | none (off) | Folder for in-app scheduled backups (`/data/backups`) |
| `Meridian__Backup__IntervalHours` / `__Retain` / `__IncludeUsers` | 24 / 7 / true | Backup schedule and retention |
| `Meridian__Scripting__Enabled` | false on servers | Script console ([SCRIPTING.md](SCRIPTING.md)). Scripts run arbitrary .NET code in the server |
| `Meridian__Scripting__TimeoutSeconds` / `__Unrestricted` | 30 / false | Script limits |
| `Meridian__Plugins__Path` / `__Enabled` | none / true | Server plugin folder ([PLUGINS.md](PLUGINS.md)) |
| `Meridian__AI__Enabled` / `__ApiKey` / `__Model` | true / none / `claude-opus-5-5` | AI assistant with Claude ([AI.md](AI.md)). `ANTHROPIC_API_KEY` also works for the key |
| `Meridian__AI__Provider` | Anthropic | `Anthropic`, or `Local` for a self-hosted OpenAI-compatible model server (Ollama, LM Studio, llama.cpp, vLLM, LocalAI). Only administrators set it; users can't change the provider or the address |
| `Meridian__AI__Local__BaseUrl` / `__Model` / `__ApiKey` | none | The model server's base URL (such as `http://ollama:11434/v1`), model name and optional key. Requests only ever go to this URL's host; private addresses are allowed |
| `Meridian__AI__Local__ContextWindow` / `__MaxTokens` / `__Temperature` / `__ToolCalling` / `__TimeoutSeconds` | 32768 / 4096 / 0.2 / Auto / 300 | Local model tuning ([AI.md](AI.md#using-a-local-model)) |
| `Meridian__Desktop__UpdatesPath` | none (off) | Folder of desktop release files, or a `latest.json` manifest, served to desktop apps at `/api/desktop/latest` ([below](#desktop-updates-from-your-server)) |
| `Meridian__Desktop__NotesUrl` | none | Release notes page listed in the generated manifest |
| `Meridian__Logging__Format` | text | `json` for one JSON object per line |
| `Meridian__Logging__Requests` | false | Log one line per request: method, path, status and duration. Never query strings, headers, cookies or bodies |
| `Meridian__Telemetry__OtlpEndpoint` (or `OTEL_EXPORTER_OTLP_ENDPOINT`) | none (off) | Export traces and metrics over OTLP |
| `Meridian__Telemetry__Protocol` | grpc | `grpc` or `http/protobuf` |
| `Meridian__Telemetry__ServiceName` (or `OTEL_SERVICE_NAME`) | meridian | `service.name` resource attribute |
| `Meridian__Telemetry__ExportLogs` | false | Also send logs over OTLP |
| `Logging__LogLevel__Default` | Information | Standard ASP.NET Core log levels |
| `ASPNETCORE_HTTP_PORTS` | 8080 | Listening port inside the container |

## TLS

- **Compose with Caddy**: set `MERIDIAN_DOMAIN` and `ACME_EMAIL`. Caddy gets and renews Let's Encrypt
  certificates on its own, redirects HTTP to HTTPS, and serves HTTP/2 and HTTP/3. `deploy/compose/Caddyfile` also
  sets HSTS and basic security headers.
- **Internal or air-gapped networks**: set `CADDY_TLS=internal`. Caddy then issues certificates from its own local
  CA. Distribute its root certificate to clients (export it with
  `docker compose -f docker-compose.prod.yml cp caddy:/data/caddy/pki/authorities/local/root.crt .`).
- **Your own certificate**: change `tls {$CADDY_TLS}` in the Caddyfile to
  `tls /certs/fullchain.pem /certs/privkey.pem` and mount the files.
- **Kubernetes**: cert-manager issues the certificate named in `ingress.tls.secretName`. Set the
  `cert-manager.io/cluster-issuer` annotation to your issuer.
- **Other proxies (nginx, Traefik, a cloud load balancer)**:
  - Forward `Host`, `X-Forwarded-For` and `X-Forwarded-Proto`.
  - Allow WebSocket upgrades on all paths.
  - Raise read timeouts to at least one hour.
  - Set `Meridian__Security__TrustForwardedHeaders=true`.

## First administrator and SSO

The first account registered on a server becomes an administrator. With registration turned off (the production
default), choose one of these methods:

1. List the admin username in `MERIDIAN_ADMINS` (`meridian.security.admins` in Helm). Start once with
   `MERIDIAN_ALLOW_REGISTRATION=true`, register that user, then set registration back to false.
2. With SSO, put the admin's SSO username in `MERIDIAN_ADMINS`, or map an IdP group with
   `ClaimMappings__AdminClaim` and `ClaimMappings__AdminValue`.

To set up SSO with any OpenID Connect provider (Entra ID, Okta, Google, Keycloak, Auth0, …):

1. Register a web application at the provider.
   - Redirect URI: `https://<domain>/signin-oidc-<name>`.
   - Sign-out callback: `https://<domain>/signout-callback-oidc-<name>`.
   - Grant type: authorization code.
2. Set `MERIDIAN_SSO_NAME`, `_AUTHORITY`, `_CLIENT_ID` and `_CLIENT_SECRET` in `.env`, or add an entry to
   `meridian.sso.providers` and `secrets.ssoClientSecrets` in Helm.
3. Keep `TrustForwardedHeaders=true` behind the proxy. Without it, Meridian builds `http://` redirect URIs and the
   provider rejects them.
4. Once SSO works and an SSO administrator exists, consider `DisableLocalPasswords=true`.

### Sign in with GitLab

GitLab (gitlab.com or a self-managed instance) is an OpenID Connect provider, and Meridian has a preset for it. A
provider named `gitlab`, or any provider with `Preset=gitlab`, gets these defaults, and anything you set explicitly
wins:

| Setting | Preset default |
| --- | --- |
| `Authority` | `https://gitlab.com`. For self-managed, the instance root, e.g. `https://gitlab.example.com` (it is also the issuer) |
| `DisplayName` | `GitLab` |
| `Scopes` | `openid profile email` |
| `ClaimMappings__Username` | `preferred_username`, falling back to GitLab's `nickname` claim (`ClaimMappings__UsernameFallback`). Both hold the GitLab username |
| `ClaimMappings__DisplayName` | `name` |

To set it up:

1. In GitLab, create an application. For gitlab.com use **User settings ▸ Applications** or **Group ▸ Settings ▸
   Applications**. For a self-managed instance, an administrator can use **Admin area ▸ Applications** to make it
   available to every user.
   - Redirect URI: `https://<domain>/signin-oidc-gitlab`. Use the provider's name in place of `gitlab` if you chose
     another one.
   - Confidential: yes. Scopes: `openid`, `profile` and `email`. Add `read_user` only if your other tools need it.
2. Copy the Application ID and Secret into the provider settings:

   ```bash
   # .env (docker-compose.prod.yml)
   MERIDIAN_SSO_NAME=gitlab
   MERIDIAN_SSO_AUTHORITY=https://gitlab.com          # or https://gitlab.example.com
   MERIDIAN_SSO_CLIENT_ID=<Application ID>
   MERIDIAN_SSO_CLIENT_SECRET=<Secret>
   ```

   ```yaml
   # Helm values (the secret goes in secrets.ssoClientSecrets)
   meridian:
     sso:
       providers:
         - name: gitlab
           authority: https://gitlab.example.com
           clientId: <Application ID>
           claimMappings: { adminClaim: groups, adminValue: my-group/meridian-admins }
   ```

3. Optional: to make members of a GitLab group Meridian administrators, set `ClaimMappings__AdminClaim=groups` and
   `ClaimMappings__AdminValue=<full group path>`. GitLab lists the user's groups in the `groups` claim of the userinfo
   response.

Accounts are linked by issuer and subject. The subject is GitLab's numeric user id, so renaming a GitLab user keeps the
same Meridian account. A self-managed instance with a private CA must be trusted by the container, for example by
mounting the CA certificate and setting `SSL_CERT_FILE`.

Provider-specific notes and the issue-tracker and API integrations are in [INTEGRATIONS.md](INTEGRATIONS.md).

## Backups and restore

Use both kinds of backup. They protect against different failures.

| | `pg_dump` (database) | In-app backup (Admin → Backups) |
| --- | --- | --- |
| Contains | Everything: users and password hashes, projects, history, comments, reviews, audit log, tokens, integrations | Projects with full version history, members, and users (without password hashes) |
| Restores to | The same or newer Meridian version, as a whole server | Any Meridian server, selected projects, restored side by side (an id still in use comes back as a copy) |
| Use for | Disaster recovery | Moving projects between servers, undoing a deleted project |

### pg_dump

The `backup` compose profile writes `/backups/meridian-<UTC timestamp>.dump` (custom format) every
`PG_BACKUP_INTERVAL_SECONDS` and deletes dumps older than `PG_BACKUP_RETAIN_DAYS`. Copy the `pg-backups` volume
off the host (restic, borg, rclone, or a cloud snapshot), because a backup on the same disk is not a backup. To
take a dump by hand:

```bash
docker compose -f docker-compose.prod.yml exec -T db pg_dump -U meridian -d meridian -Fc > meridian-$(date -u +%Y%m%dT%H%M%SZ).dump
```

To restore a dump:

```bash
docker compose -f docker-compose.prod.yml stop meridian
docker compose -f docker-compose.prod.yml exec -T db dropdb -U meridian --if-exists meridian
docker compose -f docker-compose.prod.yml exec -T db createdb -U meridian meridian
docker compose -f docker-compose.prod.yml exec -T db pg_restore -U meridian -d meridian --no-owner < meridian-20261109T020000Z.dump
docker compose -f docker-compose.prod.yml start meridian
```

On Kubernetes, use your database's own backups (managed-service snapshots or point-in-time recovery, CloudNativePG
backups) or a CronJob running `pg_dump`. Back up the `meridian-keys` PVC or Secret too. Test a restore at least
once a quarter.

### In-app backup and restore

- **Download**: Admin → Backups → *Download backup*, or `GET /admin/api/backup?users=true` as an administrator.
  This is a zip of JSON files.
- **Schedule**: `Meridian__Backup__Folder=/data/backups` (set by compose and Helm), with `IntervalHours` and
  `Retain`. Scheduled files are listed and downloadable on the same page.
- **Restore**: Admin → Backups → *Choose backup…*, pick the projects, then *Restore*. Projects are recreated with
  their history. Users can optionally be recreated, and SSO users link again on their next sign-in.

## Desktop updates from your server

The desktop app normally gets updates from GitHub releases. A team can host them on its own Meridian server instead.
This is useful when the repository is private, when desktops cannot reach GitHub, or when you want to control rollout.

1. Put the release files in a folder the server can read, for example a volume mounted at `/data/desktop-updates`:
   - the files `build/package.sh` writes to `artifacts/`, such as `Meridian-0.7.0-windows-x64.zip`,
     `Meridian-0.7.0-linux-x64.tar.gz` and `Meridian-0.7.0-macos-arm64.zip`;
   - the release's `SHA256SUMS.txt`, if you have it.
2. Set `Meridian__Desktop__UpdatesPath=/data/desktop-updates` and restart.
3. In each desktop app, go to **Settings ▸ Desktop ▸ Update source ▸ Custom URL** and enter
   `https://meridian.example.com/api/desktop/latest`.

`GET /api/desktop/latest` lists the newest version in the folder with every package's name, `url`
(`files/<name>`, relative to the manifest), `size` and `sha256`. The checksums come from the folder's `SHA256SUMS.txt`
when it lists a file; otherwise the server computes them once and caches them. `GET /api/desktop/files/<name>` serves the
files with range support. Only plain file names directly inside the folder are served: no paths, hidden files or links.
If you would rather write the manifest yourself, put a `latest.json` in the folder; it is served as is. The format is in
[RELEASING.md](RELEASING.md#manifest-format).

Both endpoints are anonymous, because the desktop updater has no session. Only put files there that every user who can
reach the server may download. Without `UpdatesPath`, they answer 404. The desktop app checks every file's SHA-256
before installing it and only accepts https addresses, so terminate TLS in front of the server as usual. How the app
installs the update (installer, or an in-place swap of the portable archive) is described in
[RELEASING.md](RELEASING.md#auto-update).

## Upgrades, migrations and rollback

Meridian upgrades its database schema in place at startup. The upgrade is idempotent and forward-only: there are no
"down" migrations. To upgrade:

1. Read the release notes.
2. **Take a `pg_dump`** and copy `meridian-keys`.
3. Compose: set `MERIDIAN_VERSION=<new>` in `.env`, then run
   `docker compose -f docker-compose.prod.yml pull meridian && docker compose -f docker-compose.prod.yml up -d meridian`.
   Helm: run `helm upgrade meridian deploy/helm/meridian -n meridian --reuse-values --set image.tag=<new>`.
4. Watch `docker compose logs -f meridian` or `kubectl logs -f deploy/meridian`, and check that `/readyz` returns 200.

Connected users see "reconnecting" for a few seconds. On shutdown, live edits are written to the database. Allow
the configured 30-second grace period.

There are two ways to roll back:

- **No schema change between the versions** (the usual case for patch releases): deploy the previous tag
  (`MERIDIAN_VERSION=<old>`, or `helm rollback meridian <revision>`).
- **Schema changed**: older versions may not understand new tables or columns. Stop Meridian, restore the
  `pg_dump` taken before the upgrade, then deploy the old tag. Edits made after the upgrade are lost unless you
  export those projects first with an in-app backup.

Pin exact versions (or digests) in production. Never run `latest`.

## Scaling

One Meridian instance handles a typical engineering organisation: hundreds of users and dozens of people editing
at the same time. Scale up (more CPU and memory) before scaling out. Blazor Server and live collaboration add
constraints when you run more than one replica:

1. **Sticky sessions are required.** A Blazor circuit lives in one process, and any request routed to another
   replica breaks it. The Helm chart sets cookie affinity for ingress-nginx
   (`nginx.ingress.kubernetes.io/affinity: cookie`, `affinity-mode: persistent`). For Traefik use
   `traefik.ingress.kubernetes.io/service.sticky.cookie: "true"`. Cloud load balancers need their own
   session-affinity setting. Use `service.sessionAffinity: ClientIP` only when there is no ingress.
2. **A shared Data Protection key store is required.** Sign-in cookies and antiforgery tokens are encrypted with
   the key ring in `Meridian__KeysPath`. All replicas must read the same keys. Use a `ReadWriteMany` volume (NFS,
   EFS, Azure Files, CephFS) or an `existingClaim` on shared storage. The chart refuses to render more than one
   replica with a `ReadWriteOnce` key volume.
3. **Live collaboration is per process.** Each project's live session is held by the replica that opened it. Two
   people on different replicas editing the same project at the same time do not see each other's changes, and
   the later save wins. Sticky sessions keep a *user* on one replica, not a *project*. Until Meridian has
   cross-node collaboration, scale out only when teams rarely co-edit the same project from different
   replicas. Otherwise stay on one larger replica.
4. **In-app scheduled backups run on every replica.** Either point `Backup:Folder` at a shared volume and accept
   duplicates, or disable it with `meridian.backup.intervalHours: 0` and rely on `pg_dump`.
5. **Pod churn disconnects circuits.** The HPA in the chart scales in slowly (one pod per 10 minutes, after
   30 minutes of stability). Removing a pod forces its users to reconnect, and unsaved dialog state is lost.

## Monitoring

- **Health**:
  - `GET /healthz` is liveness: the process serves HTTP.
  - `GET /readyz` is readiness: a database query succeeds within 5 seconds.
  - Both return `Healthy` or `Unhealthy` with no details, and neither needs authentication.
  - Point an external uptime check at `https://<domain>/readyz`.
- **Logs**: with `Meridian__Logging__Format=json` (the default in compose and Helm), each log line is a JSON
  object with `Timestamp`, `LogLevel`, `Category`, `Message`, structured `State` and scopes (`TraceId`, `SpanId`,
  `RequestPath`). Ship stdout with Loki/Promtail, Fluent Bit, Vector or the Datadog agent.
  - Request lines use the category `Meridian.Requests`.
  - Security events (sign-in success, failure and lockout, admin actions) use `Meridian.Web.Admin.AuditLog`, and
    the full audit log is under Admin → Audit, exportable as CSV.
  - Request logs never include query strings, headers, cookies, request bodies or passwords.
- **Traces and metrics (OpenTelemetry)**: set `Meridian__Telemetry__OtlpEndpoint`. Meridian then exports:
  - traces for ASP.NET Core requests (health probes excluded, query strings redacted), outgoing HttpClient calls
    and Npgsql database commands
  - metrics for ASP.NET Core and Kestrel (request duration, active connections), SignalR connections (Blazor
    circuits), HttpClient, the .NET runtime (GC, thread pool, CPU) and Npgsql
  - Logs as well when `ExportLogs=true`

  With compose, the `otel` profile starts a collector (`deploy/compose/otel-collector.yaml`). Replace its `debug`
  exporter with your backend. The OpenTelemetry packages are pinned in `src/Meridian.Web/Meridian.Web.csproj`.
  Audit them with `dotnet list src/Meridian.Web package --vulnerable --include-transitive`.
- **What to alert on**:
  - `/readyz` failing for more than 2 minutes
  - container restarts
  - p95 `http.server.request.duration` above 1 s
  - `kestrel.active_connections` near your limit
  - PostgreSQL disk above 80%
  - the newest backup file older than 26 hours
  - TLS certificate expiry under 14 days (Caddy and cert-manager renew at 30 days)

## Troubleshooting

| Symptom | Likely cause / fix |
| --- | --- |
| Container exits at start with `Missing connection string 'Meridian'` | `ConnectionStrings__Meridian` is not set. Check `.env` or the Secret |
| Exits with a Npgsql `28P01` / connection refused error | Wrong `POSTGRES_PASSWORD` (the database keeps the password from its *first* start; change it with `ALTER USER`), or the database is not ready or reachable |
| `/readyz` returns 503 | Database down, slow (over 5 s) or out of connections. Check `db` logs and `Maximum Pool Size` |
| Everyone is signed out after a restart; antiforgery or "key not found" errors | Key ring not persistent or not shared. Check the `/data/keys` volume, and that replicas share it |
| Page loads, then "Attempting to reconnect" loops | WebSockets blocked or proxy timeout too short, or no sticky sessions with more than one replica |
| SSO error `redirect_uri mismatch`, or an `http://` redirect | `TrustForwardedHeaders` is not true, or the proxy does not send `X-Forwarded-Proto`. Check the redirect URI registered at the IdP |
| Audit log shows the proxy's IP for every user | Same cause: forwarded headers are not trusted or not sent |
| Caddy cannot get a certificate | DNS does not point at the host, port 80 or 443 is closed, or you hit Let's Encrypt rate limits. See `docker compose logs caddy` |
| `Read-only file system` errors | Something writes outside `/tmp` and `/data/*`. Make sure the `/tmp` tmpfs or emptyDir is mounted. Report the path as a bug |
| PDF export has boxes instead of text | Custom image without fonts. Use the official image or install `fonts-dejavu-core` and `libfontconfig1` |
| Plugin not loaded | Plugin missing from `/data/plugins`, `Meridian__Plugins__Enabled=false`, or the DLL targets the wrong API. Check startup logs |
| Login rate-limited for everyone | All requests appear to come from one IP (the proxy). Enable `TrustForwardedHeaders` |

To debug a running container:

- Run `docker compose -f docker-compose.prod.yml exec meridian dotnet /app/Meridian.Web.dll --healthcheck http://127.0.0.1:8080/readyz; echo $?`.
- Temporarily set `MERIDIAN_LOG_LEVEL=Debug`.

## Hardening checklist

- [ ] Pin the image by version or digest, and verify its cosign signature and attestation before rollout.
- [ ] Use a long random `POSTGRES_PASSWORD`. Keep `.env` at mode 600, out of git, and in a secret manager.
- [ ] Set `AllowRegistration=false` and name explicit admins. Use SSO with MFA at the IdP, and consider
      `DisableLocalPasswords=true`.
- [ ] Keep the script console off (`Scripting:Enabled=false`) unless every project owner is trusted to run code
      on the server. Install only reviewed plugins, and mount the plugin volume read-only.
- [ ] Set `TrustForwardedHeaders=true` **only** when port 8080 cannot be reached except through the proxy. Compose
      does this with an internal network and no published port, and Helm with ClusterIP plus
      `networkPolicy.enabled`.
- [ ] Use TLS everywhere. HSTS is on (Caddy) and the database connection uses `SSL Mode=Require` when it leaves the
      host or cluster.
- [ ] Run the container non-root with a read-only root filesystem, `cap_drop: ALL`, `no-new-privileges` and the
      seccomp `RuntimeDefault` profile (all are the defaults in compose and Helm).
- [ ] PostgreSQL is not published to the network. Its data volume is encrypted at rest by the host or cloud.
- [ ] The Data Protection keys are on persistent, backed-up and access-restricted storage. On Kubernetes, consider
      encrypting them at rest with a KMS-backed StorageClass. Meridian logs a warning that the keys are not
      encrypted by an `IXmlEncryptor`.
- [ ] Back up with `pg_dump` and the in-app backup, store copies off-host, and test restores.
- [ ] Ship JSON logs to central storage and keep the audit log. Alert on repeated `login.failure` and `login.locked`.
- [ ] Rebuild or pull a new patch release regularly for base-image (Ubuntu and .NET) security updates. Scan
      images with `docker scout cves` or `trivy image`.
- [ ] Watch the OpenTelemetry collector's export endpoint and credentials. The collector config drops
      authorization and cookie attributes as a second safeguard.
