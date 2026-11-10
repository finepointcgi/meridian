# Meridian

Model-based systems engineering that is as easy to draw with as a whiteboard. Meridian is SysML block,
internal block, requirement, activity, state machine, sequence, use case and parametric diagrams, all
linked to requirements and traceability. It also covers BPMN, ArchiMate, ER/database diagrams and free
drawing, with live multi-user editing. It is built with C#, Blazor and MudBlazor.

It runs as a **web app** (a team server backed by PostgreSQL) and as a **desktop app** for Windows, macOS
and Linux. The desktop app works offline on your computer or connects to a team server.

## Download

Get the latest build from [Releases](../../releases):

| Platform | File | Notes |
| --- | --- | --- |
| macOS (Apple silicon) | `Meridian-<ver>-macos-arm64.zip` | Unzip, then drag **Meridian.app** to Applications. Not notarized yet: right-click → Open the first time. |
| macOS (Intel) | `Meridian-<ver>-macos-x64.zip` | Same as above. |
| Windows 10/11 x64 | `Meridian-<ver>-windows-x64.zip` | Unzip and run `Meridian.exe`. Needs the WebView2 runtime, which Windows 11 already has. |
| Linux x64 | `Meridian-<ver>-linux-x64.tar.gz` | Needs WebKitGTK 4.1. Run `./Meridian` or `./install.sh`. |
| Server (web version) | `Meridian-Server-<ver>.zip` | Needs the ASP.NET Core 10 runtime. You can also use Docker (below). |

When it first starts, the desktop app asks where to work:

- **This computer**: a private offline workspace. Data lives in a SQLite file in your user data folder
  (`~/Library/Application Support/Meridian`, `%APPDATA%\Meridian` or `~/.config/Meridian`). There is
  no account to create. Version history and comments still work.
- **Team server**: enter the address of a Meridian server to sign in, share projects and edit together
  in real time.

You can change the choice later from your avatar menu (**Switch workspace or server…**).

## Features

- **Drawing as easy as draw.io**
  - A searchable shapes panel: click to add, drag to place.
  - Hover arrows that add connected shapes.
  - Alignment guides and resize handles.
  - Double-click empty canvas to add a shape; start typing to label one.
  - A format bar for fill, line and text colors and line width, with a format painter.
- **SysML and UML**
  - Block definition, internal block (ports, item flows), parametric (with an equation solver), activity
    (swimlanes, pins, call behavior), state machine (composite states), sequence (fragments), use case,
    package and class diagrams.
  - Profiles and stereotypes, including UAF.
- **Requirements**
  - A requirements table, traceability matrix and impact analysis.
  - Satisfy, verify, refine, derive and trace links, plus allocation.
- **Other languages**: BPMN 2.0, ArchiMate 3, ER/database diagrams (DDL in and out for PostgreSQL, MySQL,
  SQL Server and SQLite), C# reverse engineering, and a free-drawing whiteboard.
- **Simulation**: step through activities and state machines.
- **Exchange**
  - XMI 2.5.1 (with diagrams), SysML v2 text, ReqIF, BPMN XML and ArchiMate exchange files.
  - Enterprise Architect `.qea` import.
  - A read-only SysML v2 REST API.
- **Reports**: Word and PDF from templates, plus SVG export.
- **Collaboration** (server)
  - Accounts and project roles (owner, editor, viewer).
  - Live co-editing with presence.
  - Version history with diff and restore, and comments.

## Run the server

With Docker (PostgreSQL included):

```bash
docker compose --profile app up -d --build    # → http://localhost:8180
```

From source (needs the .NET 10 SDK):

```bash
docker compose up -d db                                   # PostgreSQL on 127.0.0.1:5433
cp src/Meridian.Web/appsettings.Development.example.json src/Meridian.Web/appsettings.Development.json
dotnet run --project src/Meridian.Web --urls http://localhost:5180
```

In development, the users listed under `DevSeed` are created when the server starts. Change their passwords
in your local `appsettings.Development.json`, which git ignores.

To run with a single SQLite file instead of PostgreSQL, set `Meridian__Storage=sqlite` and
`Meridian__DataPath=<folder>`.

## Scripting and plugins

- **Script console** (dock tab *Script*, or ⌘K → "Script console"): C# scripts against a stable model API
  (`Model`, `Selection`, `Log(...)`), run as one undo step with a time limit and Cancel. See [docs/SCRIPTING.md](docs/SCRIPTING.md).
- **Plugins**: .NET assemblies implementing `IMeridianPlugin` add commands, validation rules, exporters and importers.
  See [docs/PLUGINS.md](docs/PLUGINS.md) and the sample in `samples/HelloPlugin`.

> **Security.** Scripts and plugins are arbitrary .NET code running inside the Meridian process with its permissions.
> The script policy (no file system, process, network or reflection APIs by default) is a guard rail, **not a sandbox**.
> The console is on by default only in the desktop app (local mode). On a server it is off unless an administrator sets
> `Meridian:Scripting:Enabled=true`, and even then only server administrators can run scripts (or also project owners with
> `Meridian:Scripting:AllowProjectOwners=true`). Plugins load only from
> `Meridian:Plugins:Path` on a server, so only someone with access to the server's disk can install them.
### Administration, single sign-on and backups (server mode)

The first account registered on a server (or any username listed in `Meridian:Admins`) is a server
administrator. Administrators get an **Administration** entry in the avatar menu (`/admin`) with tabs for
users (create, disable, reset password, unlock, make admin), organizations and teams (a team can be given a
role on any project; a member's effective role is the higher of their direct and team roles), all projects
(owner, size, rename, transfer, delete), a filterable audit log with CSV export, and backups. None of this
appears in the desktop app's local mode.

Single sign-on uses OpenID Connect (authorization code + PKCE). Register Meridian with your identity provider
using the redirect URI `https://<server>/signin-oidc-<name>`, then configure it, e.g. in `appsettings.json` or
environment variables (`Meridian__Sso__Providers__0__ClientSecret=…`):

```json
"Meridian": {
  "Admins": [ "alice" ],
  "Sso": {
    "DisableLocalPasswords": false,
    "Providers": [
      { "Name": "entra", "DisplayName": "Microsoft", "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ClientId": "<app-id>", "ClientSecret": "<secret>", "Scopes": [ "openid", "profile", "email" ],
        "ClaimMappings": { "Username": "preferred_username", "DisplayName": "name", "Email": "email",
                           "AdminClaim": "roles", "AdminValue": "Meridian.Admin" } },
      { "Name": "okta",     "Authority": "https://<org>.okta.com/oauth2/default",        "ClientId": "…", "ClientSecret": "…" },
      { "Name": "google",   "DisplayName": "Google", "Authority": "https://accounts.google.com", "ClientId": "…", "ClientSecret": "…" },
      { "Name": "keycloak", "Authority": "https://sso.example.com/realms/<realm>",      "ClientId": "…", "ClientSecret": "…" }
    ]
  },
  "Security": { "MaxFailedLogins": 5, "LockoutMinutes": 15, "LoginAttemptsPerMinute": 10,
                "AllowRegistration": true, "TrustForwardedHeaders": false },
  "Backup": { "Folder": "/var/backups/meridian", "IntervalHours": 24, "Retain": 7, "IncludeUsers": true }
}
```

- The sign-in page shows a "Sign in with …" button per provider. A first sign-in creates a Meridian account
  linked by issuer + subject (never matched to an existing account by name or e-mail); set `AutoProvision: false`
  on a provider to refuse unknown identities. `DisableLocalPasswords` turns off password sign-in, registration
  and HTTP Basic API auth.
- SSO needs HTTPS. Behind a TLS-terminating reverse proxy set `Security:TrustForwardedHeaders` to `true`.
- Password sign-in is rate limited per client IP, and an account is locked for `LockoutMinutes` after
  `MaxFailedLogins` wrong passwords in a row (an administrator can unlock it). Disabled accounts are signed out
  on their next request.
- The audit log (`audit_log` table, append-only — the database rejects updates and deletes) records sign-ins
  and failures, SSO provisioning, user, role, team and member changes, project create/rename/delete/transfer,
  version commits and restores, exports, backups and restores.
- **Backups**: *Administration → Backups* downloads a zip with every project's latest model, its full version
  history and members, plus (optionally) the user directory without password hashes. Uploading a backup there
  restores selected projects with their history; a project whose id still exists comes back as a copy. With
  `Backup:Folder` set, the server also writes a backup every `IntervalHours` and keeps the newest `Retain` files.

## Build the desktop apps

```bash
build/package.sh 0.2.0                 # all platforms → artifacts/
build/package.sh 0.2.0 osx-arm64       # just one
```

Pushing a tag such as `v0.2.0` runs the release workflow, which builds every platform and publishes a
GitHub release.

## Project layout

| Path | Contents |
| --- | --- |
| `src/Meridian.Core` | Model repository, metamodel and notation, layout, analysis, simulation, validation |
| `src/Meridian.Serialization` | Project files, SVG rendering, and all import/export formats |
| `src/Meridian.Data` | Storage: `PostgresStore` (server) and `SqliteStore` (desktop local mode) |
| `src/Meridian.Scripting` | Scripting/plugin API (`IModelApi`), Roslyn script engine, plugin loader |
| `src/Meridian.Web` | Blazor Server app (MudBlazor UI, live collaboration, SysML v2 API) |
| `src/Meridian.Desktop` | Photino desktop host: start screen, in-process local server, native save dialogs |
| `samples/HelloPlugin` | Sample plugin |
| `tests/` | xUnit and bUnit tests |
| `build/` | Packaging script, icons and platform files |
