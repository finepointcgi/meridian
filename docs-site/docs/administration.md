# Administration

This page is for people who run a Meridian **team server**. None of it applies to the desktop app's local workspace.

## Administrators

The first account registered on a server, and any username listed in `Meridian:Admins`, is a **server
administrator**. Administrators get **Administration** in the avatar menu (`/admin`) with these tabs:

| Tab | What you can do |
| --- | --- |
| **Users** | create, disable, reset passwords, unlock locked-out accounts, make or revoke administrators |
| **Organizations** and **teams** | group users; give a team a role on any project (a member's effective role is the higher of their direct and team roles) |
| **Projects** | every project with owner and size: rename, transfer ownership, delete |
| **Audit log** | a filterable, append-only log with CSV export |
| **Backups** | download and restore backups |

> [!NOTE]
> **TODO (screenshot):** the Administration page, Users tab.

## Configuration

Settings come from `appsettings.json`, environment variables (`Meridian__Section__Key`) or any other ASP.NET Core
configuration source.

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Meridian` | PostgreSQL connection string |
| `Meridian:Storage=sqlite`, `Meridian:DataPath` | run on a single SQLite file instead of PostgreSQL |
| `Meridian:KeysPath` | where ASP.NET Core Data Protection keys are kept (mount a volume in containers; integration secrets depend on it) |
| `Meridian:PublicUrl` | public address used in links (issue deep links, OSLC) behind a proxy |
| `Meridian:Admins` | usernames that are always administrators |
| `Meridian:Scripting:Enabled`, `:TimeoutSeconds`, `:Unrestricted` | the [script console](scripting.md) (off by default on servers) |
| `Meridian:Plugins:Path`, `:Enabled` | where [plugins](plugins.md) load from |

## Single sign-on (OpenID Connect)

Meridian signs in with OpenID Connect (authorization code + PKCE). Register Meridian with your identity provider using
the redirect URI `https://<server>/signin-oidc-<name>`, then configure the provider:

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
      { "Name": "okta", "Authority": "https://<org>.okta.com/oauth2/default", "ClientId": "…", "ClientSecret": "…" },
      { "Name": "keycloak", "Authority": "https://sso.example.com/realms/<realm>", "ClientId": "…", "ClientSecret": "…" },
      { "Name": "gitlab", "Authority": "https://gitlab.com", "ClientId": "<application-id>", "ClientSecret": "…" }
    ]
  }
}
```

- The sign-in page shows a *Sign in with …* button per provider.
- A first sign-in creates a Meridian account linked by issuer and subject; it is never matched to an existing account
  by name or e-mail. Set `AutoProvision: false` on a provider to refuse unknown identities.
- `AdminClaim` / `AdminValue` make users with that claim value administrators.
- **GitLab** (gitlab.com or self-managed): a provider named `gitlab`, or one with `"Preset": "gitlab"`, defaults to
  the authority `https://gitlab.com`, the scopes `openid profile email`, the button text *GitLab*, and the GitLab
  username from `preferred_username` (or `nickname`). For a self-managed GitLab, set `Authority` to its root address.
  Create the application in GitLab under *User settings* (or *Group settings*, or *Admin area*) ▸ *Applications*, with
  the redirect URI `https://<server>/signin-oidc-gitlab` and the scopes `openid`, `profile` and `email`. To make a GitLab
  group's members administrators, use `"AdminClaim": "groups", "AdminValue": "<group path>"`.
- `DisableLocalPasswords` turns off password sign-in, registration and HTTP Basic API authentication.
- SSO needs HTTPS. Behind a TLS-terminating reverse proxy, set `Meridian:Security:TrustForwardedHeaders` to `true`.
- Keep client secrets out of files, for example `Meridian__Sso__Providers__0__ClientSecret=…`.

## Sign-in security

Under `Meridian`:

```json
"Security": { "MaxFailedLogins": 5, "LockoutMinutes": 15, "LoginAttemptsPerMinute": 10,
              "AllowRegistration": true, "TrustForwardedHeaders": false }
```

Password sign-in is rate limited per client IP. An account is locked for `LockoutMinutes` after `MaxFailedLogins`
wrong passwords in a row (an administrator can unlock it). Set `AllowRegistration` to `false` to create accounts only
from the Users tab. Disabled accounts are signed out on their next request.

## Audit log

The audit log (`audit_log` table; the database rejects updates and deletes) records sign-ins and failures, SSO
provisioning, user, role, team and member changes, project create / rename / delete / transfer, version commits and
restores, exports, backups and restores. Filter it in the Audit tab and export it as CSV.

## Backups

**Administration → Backups** downloads a zip with every project's latest model, its full version history and members,
and (optionally) the user directory without password hashes. Uploading a backup there restores the selected projects
with their history; a project whose id still exists comes back as a copy.

Scheduled backups (under `Meridian`):

```json
"Backup": { "Folder": "/var/backups/meridian", "IntervalHours": 24, "Retain": 7, "IncludeUsers": true }
```

The server then writes a backup every `IntervalHours` and keeps the newest `Retain` files.

## Project roles

| Role | Can |
| --- | --- |
| owner | everything, including members, integrations, running scripts (when enabled) and breaking locks |
| editor | edit the model, commit, branch, merge, request reviews |
| viewer | read, comment, vote in reviews they are asked to |

Owners manage members with **Share & members…** in the project menu.

## API tokens

Users create personal access tokens in **API tokens…** (avatar menu) for the REST and OSLC APIs. See
[Integrations](integrations.md#api-tokens).
