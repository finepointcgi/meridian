# Integrations

Meridian connects to issue trackers, notifies other systems through webhooks, follows code repositories, syncs
requirements with IBM DOORS Next and Siemens Polarion, reads live database schemas, and exposes requirements and model
elements to ALM tools through OSLC. The code lives in `src/Meridian.Web/Integrations/`, the UI in
`Components/Workbench/{IntegrationsDialog,LinkedIssuesSection,ApiTokensDialog}.razor`, and storage in
`src/Meridian.Data/*Store.Integrations.cs`.

Server URLs for issue trackers, code repositories and requirements tools may be `http://` or `https://`, so servers on
an internal network without TLS work. Over plain `http://` the token or password is sent unencrypted, so use it only on
a network you trust.

## 1. Issue trackers

Open the project menu → **Integrations (issue trackers, webhooks, code)…** → *Issue trackers* (project owners only).

| Tracker | Base URL | Project field | Credentials |
|---|---|---|---|
| Jira Cloud (REST v3) | `https://your-site.atlassian.net` | project key, e.g. `MER` | Atlassian account email + API token (HTTP Basic) |
| Jira Server / Data Center (REST v2) | `https://jira.example.com` | project key | personal access token (Bearer) |
| Azure DevOps (work items) | `https://dev.azure.com/your-org` | project name | PAT with *Work Items: read & write* (Basic, empty user) |
| GitHub Issues | `https://api.github.com` or `https://ghe.example.com/api/v3` | `owner/repo` | fine-grained token with *Issues: read & write* (Bearer) |
| GitLab Issues (REST v4) | `https://gitlab.com` or `https://gitlab.example.com` (the `/api/v4` suffix is optional) | `group/subgroup/project` or numeric project id | project, group or personal access token with `api` (`PRIVATE-TOKEN`), or an OAuth access token (Bearer, tick the option) |

Optionally set the issue / work item type for new issues (default `Task`). **Test connection** checks the
credentials and that the project/repository is reachable. Base URLs may be `http://` or `https://`.

**GitLab Issues** (gitlab.com and self-managed, `GitLabTracker` in `IssueTrackers.cs`):

- Setup: in the project, *Settings ▸ Access tokens ▸ Add new token* with role Reporter (Developer to close issues
  from GitLab itself) and scope `api`, or a personal access token with `api`. The project is its full path as in the
  browser address (`acme/vehicles/car`) or the numeric *Project ID* from the project overview.
- Keys are `#<iid>`. **Link issue** accepts `#12`, `12`, the browser address (`…/-/issues/12`, `…/-/work_items/12`,
  the older `…/issues/12`) or the API address, on the configured GitLab host only.
- Search uses `GET /projects/:id/issues?search=…&in=title,description&scope=all`, newest first, up to 50 results
  over pages of 20. Paging follows GitLab's `x-next-page` header, or a `Link: rel="next"` address on the same scheme,
  host and port (a next link to another host is ignored, so the token never leaves the GitLab instance).
- Created issues get the description, the deep link back to Meridian and the configured **labels**
  (comma-separated); set the issue type to `issue`, `incident`, `test_case` or `task` if you need one.
- Status is GitLab's `opened` / `closed`; the assignee is every assignee's username; labels are shown under the linked
  issue and refreshed by **Sync status**. Bulk *Create issues for requirements* works as for the other trackers.

### Secrets

Tokens and webhook secrets are encrypted with ASP.NET Core Data Protection (purpose
`Meridian.Integrations.Secrets.v1`) before they are stored, are decrypted only to call the remote service, are
never logged (HTTP client header logging is redacted) and are never sent back to the browser — the dialog shows
"••• set"; leave the field empty to keep the stored value. Persist the key ring (`Meridian:KeysPath`, a mounted
volume in containers); if the keys are lost the secrets must be re-entered.

### Which addresses the server will call

Tracker base URLs and webhook URLs are chosen by project owners, so on a team server Meridian checks every outbound
connection (redirects included) against the address it actually connects to. Link-local addresses (169.254.0.0/16,
fe80::/10 — where cloud metadata services live), known metadata addresses, unspecified and multicast addresses are
always refused. Loopback and private networks (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7) are refused unless
an administrator sets `Meridian:Integrations:AllowPrivateNetworks=true` — needed for an on-premises Jira, GitHub
Enterprise or a self-managed GitLab. The desktop app's local mode does not restrict addresses.

### Linked issues on elements

The Inspector shows a **Linked issues** section for every element:

* **Link issue** — type an issue key (`MER-12`, `#12`, `4711`), paste its browser URL, or search by text.
* **Create issue from this element** — creates an issue whose title is the element name (requirements: `REQ-001 Name`)
  and whose description is the requirement text, documentation, qualified name and a deep link back to Meridian
  (`https://meridian/?project=<id>&element=<id>`; opening it selects the element).
* Each link shows key, title, status, assignee and when it was last refreshed; the ↻ button refreshes it.
* Selecting a **package** (or the model) adds **Push N requirements as issues…**, which creates an issue for every
  requirement below it that has no issue in the chosen tracker yet (one undo step).

Links are stored in the element field `issueLinks` (a JSON array of `{connection, key, url, title, status,
assignee, closed, synced}`), so they are saved, versioned, exported in `.meridian` files and replicated to
collaborators like any other edit. Title/status/assignee are a cache.

**Sync status** (Integrations dialog) refreshes every link in the project in one undoable step. Viewers can refresh
an element's links for display but cannot write them into the model. Set `Meridian:PublicUrl` when Meridian runs
behind a proxy so deep links use the public address.

## 2. Outgoing webhooks

Integrations dialog → *Webhooks*. Each webhook has a payload URL, a secret (generated by default; shown only until
saved), an optional event filter (none selected = all) and an enabled switch. **Send** posts a `ping`.

| Event | When | `data` |
|---|---|---|
| `version.committed` | a version is committed | `number`, `message`, `author {id, username, displayName}` |
| `element.changed` | model edits, batched: sent after 5 s without edits or 30 s after the first | `count`, `elements [{id, change: added/updated/moved/removed/diagram, name, kind}]`, `userIds`, `since` |
| `comment.changed` | a comment is added, resolved/reopened or deleted | `{}` |
| `ping` | the Send button | `message` |

Request: `POST` with `Content-Type: application/json` and body

```json
{ "id": "<delivery id>", "event": "version.committed", "timestamp": "2026-11-09T12:00:00Z",
  "project": { "id": "…", "name": "Vehicle" }, "data": { … } }
```

Headers: `X-Meridian-Event`, `X-Meridian-Delivery` (same for every retry) and
`X-Meridian-Signature-256: sha256=<hex HMAC-SHA256(secret, raw body)>`. Verify it with a constant-time compare, e.g.

```python
expected = "sha256=" + hmac.new(secret.encode(), request.body, hashlib.sha256).hexdigest()
ok = hmac.compare_digest(expected, request.headers["X-Meridian-Signature-256"])
```

Delivery runs on a background queue (`WebhookDispatcher`). A 2xx response is success; network errors, timeouts
(10 s), 408, 429 and 5xx are retried after 10 s, 1 min and 5 min (4 attempts); other 4xx are not retried. Every
attempt is recorded and the latest 50 are shown under *Recent deliveries*. Pending element batches are flushed on
shutdown; queued retries are not persisted across restarts.

## 3. OSLC 2.0 provider (server mode)

Available on team servers (not the desktop app's private local server). Code: `Integrations/Oslc.cs` (documents),
`OslcEndpoints.cs` (routes), `OslcQuery.cs` (query syntax), `OslcPages.cs` (delegated UI), `Rdf.cs` (RDF toolkit).

| Resource | URL | Access |
|---|---|---|
| Root services | `/oslc/rootservices` → `oslc_rm:rmServiceProviders` and `oslc_am:amServiceProviders` | public |
| Resource shapes | `/oslc/shapes/requirement`, `/oslc/shapes/resource` | public |
| Service provider catalog | `/oslc/catalog` (one provider per project you can read) | read |
| Service provider | `/oslc/projects/{projectId}/provider` | read |
| RM query capability | `GET /oslc/projects/{projectId}/requirements` | read |
| RM creation factory | `POST /oslc/projects/{projectId}/requirements` | editor, token or Basic header |
| Requirement | `/oslc/projects/{projectId}/requirements/{elementId}` | read |
| AM query capability | `/oslc/projects/{projectId}/resources` (blocks, parts, test cases… as `oslc_am:Resource`) | read |
| AM resource | `/oslc/projects/{projectId}/resources/{elementId}` | read |
| Selection dialog | `/oslc/projects/{projectId}/selector` (requirements), `?type=resource` (model elements) | read |
| Creation dialog | `/oslc/projects/{projectId}/creator` | editor |
| UI preview | `/oslc/projects/{projectId}/preview/{elementId}?size=small` or `large` | read |

The service provider has two services. **RM** (`oslc_rm:` domain): query capability and creation factory (both with
`oslc:resourceShape`), selection and creation dialogs. **AM** (`oslc_am:` domain): query capability and selection dialog.

**Representations.** Every RDF resource is served as RDF/XML (the default), Turtle (`text/turtle`) or JSON-LD
(`application/ld+json` or `application/json`), chosen by `Accept` with q-values. Turtle and JSON-LD are produced from
the RDF/XML by Meridian's own small RDF toolkit, so there is no package dependency. Responses carry
`OSLC-Core-Version: 2.0` and `Vary: Accept`. Single resources carry an `ETag` and answer `If-None-Match` with 304.
Browsers that ask for `text/html` are redirected to the element in Meridian. Errors are `oslc:Error` documents
(`oslc:statusCode`, `oslc:message`).

**Requirement properties.** `dcterms:identifier` (requirement id), `dcterms:title`, `dcterms:description` (text and
documentation), `oslc_rm:satisfiedBy` (sources of «satisfy»), `oslc_rm:validatedBy` (sources of «verify»),
`oslc_rm:elaboratedBy` (other end of «trace») and `oslc:serviceProvider`. Link targets are Meridian's OSLC URLs of
those elements (AM resources or requirements). AM resources have `dcterms:identifier` (element id), `dcterms:title`,
`dcterms:description`, `dcterms:type` (SysML kind), `mrd:kind`, `mrd:satisfies` and `mrd:verifies`
(`mrd:` is `https://meridian.dev/ns/oslc#`).

**Query parameters** (both query capabilities):

* `oslc.where`: terms joined by `and`. Each term is `prop op value` with `= != < > <= >=`, or `prop in [v1, v2]`.
  Values are `"strings"` (an optional `^^type` or `@lang` is ignored), numbers, `true`/`false`, `<uri>` or a prefixed
  name. Strings compare case-insensitively; `<` and `>` compare numbers numerically. `or` and nested `prop{…}` terms
  are refused with 400.
* `oslc.select` (and `oslc.properties` on single resources): a property list, `*` for all; `prop{…}` selects `prop`.
* `oslc.orderBy`: `+prop` or `-prop`, comma separated.
* `oslc.prefix`: `p=<uri>,…`, added to the standard prefixes (`rdf rdfs xsd dcterms oslc oslc_rm oslc_am mrd`).
* `oslc.searchTerms`: every term must match the identifier, title or description.
* `oslc.paging=true` or `oslc.pageSize=N`, with `page=K` (`oslc:nextPage` and `oslc:totalCount` in `oslc:ResponseInfo`).

Results reflect the live model, including edits in open sessions that are not saved yet.

```sh
curl -H "Authorization: Bearer mrd_…" -H "Accept: text/turtle" \
  "https://meridian.example.com/oslc/projects/<id>/requirements?oslc.where=dcterms:identifier%3D%22REQ-002%22&oslc.select=dcterms:title"
```

**Creation.** `POST` an `oslc_rm:Requirement` as RDF/XML or JSON(-LD) with `dcterms:title` (required) and optional
`dcterms:identifier` and `dcterms:description`. `?package=<element id>` chooses the package (default: the first
package named "Requirements", else the model root). The answer is `201 Created` with `Location` and the new resource,
or 409 if the identifier is taken. The factory requires an `Authorization` header (token or Basic). A browser cookie
alone is refused, so other sites cannot forge requests. The change goes through the project's collaboration session:
open tabs see it at once and it is saved like any other edit.

**Delegated dialogs.** The selection and creation dialogs implement OSLC Delegated Dialogs with the
`#oslc-core-postMessage-1.0` protocol (posts `oslc-response:` + `{"oslc:results":[{"oslc:label","rdf:resource"}]}` to
the parent or opener) and the `#oslc-core-windowName-1.0` protocol. The creation form carries an anti-forgery token
bound to the user and project (Data Protection, valid 2 hours). Dialog pages run under a restrictive CSP and may be
framed (all of `/oslc` is exempt from `X-Frame-Options`). The session cookie is `SameSite=Lax`, so a tool on another
site should open dialogs in a window, or the user signs in at the Basic prompt (username and password or token).

**Compact and UI preview.** A requirement or AM resource requested with `Accept: application/x-oslc-compact+xml`
returns `oslc:Compact` with `dcterms:title`, `oslc:shortTitle` (requirement id or SysML kind), `oslc:icon`, and
`oslc:smallPreview` / `oslc:largePreview` (`oslc:document`, `oslc:hintWidth`, `oslc:hintHeight`) pointing at the
preview pages. Link hovers in ELM, Polarion and other tools then show the text and the satisfied-by / verified-by links.

**Authentication.** Personal access tokens (section 5), as `Authorization: Bearer mrd_…` or as the Basic password;
the browser session cookie for dialogs and previews; Basic username and password unless the server is single
sign-on only. **OAuth 1.0a (Jazz "friends") is not implemented.** ELM normally connects friends with OAuth 1.0a
consumer keys. Meridian's root services document has no `jfs:oauth*` URLs, so ELM cannot add Meridian as a friend in
the usual way. Use the steps below instead.

### Registering Meridian in IBM ELM

1. Set `Meridian:PublicUrl` to the address ELM users reach Meridian at (for example `https://meridian.example.com`),
   so every OSLC URL is absolute and public. Serve it over HTTPS with a certificate ELM trusts.
2. Each ELM user who will create links makes a personal access token in Meridian (avatar menu → **API tokens…**).
3. In DOORS Next, add links to Meridian requirements or model elements by URL: *Links → Add link → Satisfied By /
   Validated By / Link To*, then paste the element's OSLC URL
   (`https://meridian.example.com/oslc/projects/<project id>/resources/<element id>` or `…/requirements/<id>`).
   On hover ELM fetches the Compact document; the browser asks for Basic credentials once (username and token).
4. Some ELM versions let an administrator register a service provider catalog for a custom link type without the
   OAuth friend step. There, register `https://meridian.example.com/oslc/catalog` to pick elements from Meridian's
   selection dialog. Otherwise linking by URL (step 3) is the supported path.
5. Other OSLC consumers (Polarion OSLC links, codebeamer, scripts) need the root services URL
   `https://meridian.example.com/oslc/rootservices` and a token.

## 4. Requirements tools: IBM DOORS Next and Siemens Polarion

Project menu → **Integrations…** → *DOORS & Polarion*. Project owners manage connections; editors can use
**Sync now**. Code: `src/Meridian.Web/Integrations/Requirements/`; UI:
`Components/Workbench/{RequirementsToolsPanel,RequirementConflictDialog}.razor`.

Connections are stored as integration kind `requirements-tool`. The password or token is encrypted like every other
integration secret and is never shown again. All HTTP goes through the guarded client `meridian-requirements-tools`.
It applies the same address rules as issue trackers. Meridian follows redirects itself and checks each one, never
sends credentials to another origin, and keeps each sync's cookies in a private jar.

### DOORS Next (ELM / Jazz RM)

| Field | Value |
|---|---|
| Server URL | the RM application root, e.g. `https://jazz.example.com/rm` (Meridian reads `{url}/rootservices`) |
| Sign in with | **Jazz form login** (username and password via `j_security_check`; the session cookie lasts only for that sync), **HTTP Basic**, or **App password** (a Jazz Authorization Server app password, sent as Basic) |
| Login URL | optional, for form login when `j_security_check` is on the JTS: `https://jazz.example.com/jts` |
| Project | **Browse projects** follows `oslc_rm:rmServiceProviders` to the service provider catalog (project areas) |
| Module | optional. **Browse modules** queries `rdf:type=<http://jazz.net/ns/rm#Module>`; a module is read through its `oslc_rm:uses` members |
| Extra filter | optional `oslc.where` clause, e.g. `dcterms:title="Braking"` |

Without a module, Meridian uses the project's RM query capability with `oslc.paging=true&oslc.pageSize=100`, follows
`oslc:nextPage`, and asks for
`oslc.select=dcterms:identifier,dcterms:title,dcterms:description,dcterms:modified,jazz_rm:primaryText,rdf:type`.
Every request sends `OSLC-Core-Version: 2.0`. Configuration-managed projects can set `configurationContext` in the
connection's config JSON, which is sent as `Configuration-Context` (there is no UI field for it yet).

### Polarion

| Field | Value |
|---|---|
| Server URL | `https://polarion.example.com` (Meridian calls `/polarion/rest/v1`) |
| Token | a Polarion personal access token (Bearer) for an account that can read the work items, and write links if publishing is on |
| Project | the project id. **Browse projects** lists `/projects` |
| Document | optional. **Browse documents** lists the documents that hold work items of the type (from their `module` relationship) |
| Work item type | default `requirement` |
| Extra filter | optional Lucene query, combined with `type:<type>` by AND, e.g. `status:approved` |

Work items are read from `/projects/{id}/workitems?query=…&fields[workitems]=title,description,type,status,updated,module`
with `page[size]=100&page[number]=N`, following `links.next`.

### Mapping

| Meridian | DOORS Next | Polarion |
|---|---|---|
| requirement id | `dcterms:identifier` | work item id (`DP-12`) |
| name | `dcterms:title` | `title` |
| text | `jazz_rm:primaryText` (XHTML → plain text), else `dcterms:description` | `description` (HTML → plain text) |
| attribute *External ID* | `dcterms:identifier` | work item id |
| attribute *External URL* | resource URL | `{server}/polarion/#/project/{p}/workitem?id={id}` |
| attribute *External revision* | `dcterms:modified` | `updated` |
| field `externalSync` (JSON) | connection, stable external id (the resource URL), revision, the title and text both sides agreed on at the last sync, links already published | same, with external id `project/WI-ID` |

New requirements go to the chosen package, or to a package named `Imported from <connection name>`. If a Meridian
requirement with the same requirement id has no link yet, Meridian links it instead of creating a duplicate.
Requirements that disappeared from the tool are counted ("no longer in the tool") but never deleted.

### Sync, change detection and conflicts

**Sync now** (⟳) fetches every requirement and applies the result to the model as one undoable step. For each linked
requirement Meridian compares the tool's values and Meridian's values with the base from the last sync:

* **Only the tool changed it.** Meridian takes the new name and text. This uses the normal edit path, so the
  requirement's «satisfy», «verify», «trace», «refine», «deriveReqt» and «allocate» links **become suspect** through
  the existing suspect-link mechanism (rule TRC-001; review them in the Traceability panel). A new revision with the
  same content only updates the stored revision.
* **Only Meridian changed it.** Meridian's version stays and is reported as "changed only in Meridian". Meridian does
  not write requirement text back to the tool.
* **Both changed it, differently.** This is a **conflict**: nothing is applied, and the conflict dialog shows both
  versions as word diffs against the last synced text. *Take the tool's version* updates Meridian (its links become
  suspect). *Keep Meridian's* keeps it and stops reporting the conflict.

**Automatic sync**: manual (the default), every 15 minutes, hourly, every 4 hours or daily. A scheduled sync edits the
live project through its collaboration session as the owner who saved the connection. If that account can no longer
edit the project, the sync is skipped with a note. Scheduled syncs apply everything except conflicts, which wait for
**Sync now**. The connection shows the result of its last sync: time, summary, conflicts, or the error.

### Publishing links back (optional, off by default)

With **Publish Meridian links** on (team servers only), each sync then publishes the links of imported requirements
that have not been published yet (remembered in `externalSync.published`):

| Meridian | DOORS Next | Polarion |
|---|---|---|
| «satisfy» from X | `oslc_rm:satisfiedBy` → X | linked OSLC resource, role `http://open-services.net/ns/rm#satisfiedBy` |
| «verify» from X | `oslc_rm:validatedBy` → X | linked OSLC resource, role `…#validatedBy` |
| «trace» with X | `oslc_rm:elaboratedBy` → X | linked OSLC resource, role `…#elaboratedBy` |
| any of these between two requirements imported from the same Polarion project | (as above) | linked work item, role `implements`, `verifies` or `relates_to` |

X is Meridian's OSLC URL of the element (section 3), or the tool's URL when X was imported too. DOORS Next is updated
by GET and PUT of the requirement's RDF/XML with `If-Match`; a 412 is retried once, and a link that is already there
is not written again. Polarion links are POSTed to `…/workitems/{id}/linkedworkitems` or `…/linkedoslcresources`; a
409 counts as already there. The Polarion roles can be changed in the connection's config JSON (`linkRoles`). Set
`Meridian:PublicUrl` so the published URLs are public; scheduled syncs publish links only when it is set.

### Not verified against real servers

The connectors are tested only against fake servers with hand-written, recorded-style responses. These have not been
checked against live systems yet: form login through a Jazz Authorization Server (OIDC), whether DOORS Next accepts
external link targets in `satisfiedBy`/`validatedBy` on PUT, reading very large modules, Polarion's role names for
`linkedoslcresources`, and listing documents through `include=module` on older Polarion versions.

## 5. Authentication for /api and /oslc — personal access tokens

Both APIs accept the browser session cookie, HTTP Basic with username/password, or a **personal access token**:
avatar menu → **API tokens…** (server mode) to create (shown once) and revoke tokens.

* Send it as `Authorization: Bearer mrd_…`, or as the Basic password (`username:mrd_…`) for tools that only speak Basic.
* Tokens are 256-bit random values; only their SHA-256 hash and a 12-character display prefix are stored
  (`api_tokens` table); revoked tokens stop working immediately. A token has its owner's access to every project.
* Tokens are also how AI clients connect to the MCP server at `/api/mcp` (read and write tools, filtered by package
  permissions, audited as `mcp.tool`; `src/Meridian.Web/Integrations/McpEndpoints.cs`, user guide `docs-site/docs/mcp.md`).
  It accepts tokens only: no cookie, no Basic password.

## 6. Code repositories (code sync)

A project can follow a GitHub, GitLab or Bitbucket repository (cloud or self-hosted). When code is pushed to the chosen
branch, Meridian reads the C# code at that commit, updates the model on a review branch, highlights what changed on the
diagrams, and posts a `meridian/model` commit status back to the commit. Pull requests into the branch get a preview
branch, a status and one comment with the model changes (see [CODE-SYNC.md](CODE-SYNC.md#pull-request-previews)). Host
code lives in `src/Meridian.Web/CodeSync/Hosts/`, the UI in `Components/Workbench/CodeRepositoriesPanel.razor`.

### Connect a repository

Project menu → **Integrations…** → *Code repositories* → **Add repository** (project owners only).

| Field | GitHub | GitLab | Bitbucket Cloud | Bitbucket Data Center / Server |
|---|---|---|---|---|
| Server URL | `https://api.github.com`, or `https://ghe.example.com/api/v3` | `https://gitlab.com`, or your server | `https://api.bitbucket.org/2.0` (any `bitbucket.org` URL works) | your server, e.g. `https://bitbucket.example.com` |
| Repository | owner and repository name | project path (`group/project`) or numeric id | workspace and repository slug | project key and repository slug |
| Access token | fine-grained token: *Contents: read*, *Commit statuses: write*, *Pull requests: write* (for comments) | project or personal token: `read_repository`, plus `api` to post statuses and comments | repository or workspace access token (*Repositories: read*, *Pull requests: write*), or `username:app-password` | HTTP access token with *Repository write* (build statuses and comments), or `username:password` |

A token that contains `:` is sent with basic authentication (Bitbucket app passwords); every other token as a bearer token.

* **Branch** (default `main`): pushes to other branches, and pull requests into other branches, are ignored.
* **Folders to include**: comma separated, e.g. `src/Domain, src/Shared`. Empty means the whole repository. They also
  apply inside submodules (`libs/core/src`).
* **Include git submodules** (on by default) and **Submodules to skip** (comma separated paths): read the code of git
  submodules, which archives leave out, at the commit the repository pins them to. See
  [CODE-SYNC.md](CODE-SYNC.md#git-submodules) and *Submodules* below.
* **Model**: *Model package* is where the code goes (defaults to the repository name). *Create diagrams for new packages
  and classes* (on by default) runs the auto-diagrammer for new namespaces and blocks. The model root's
  `codeSync.rootPackage` and `codeSync.autoDiagrams` fields override these when set; the form says when they do.
* **Sync on every push**: turn off to sync only with the sync button.
* **Post a commit status**: `pending` while Meridian works, then `success` (no changes, or changes waiting on the
  review branch, with a link to the review) or `failure`. Bitbucket shows it as a build status (`INPROGRESS`,
  `SUCCESSFUL`, `FAILED`, key `meridian-model`); Bitbucket needs a link, so the commit itself is used until there is a
  review to link to.
* **Preview pull requests** (on by default): sync each pull / merge request into the branch into
  `code-sync/<repo>/pr-<n>`. Previews follow this switch, not *Sync on every push*.
* **Comment on pull requests** (on by default): keep one comment on each previewed pull request with the counts, the
  first 10 changes and a link to the preview, edited in place on later pushes.

**Test connection** checks the token, the repository and the branch. The sync button (⟳) fetches the branch head
and syncs it straight away. The connection lists its open pull request previews (*PR #n*, status, link to the preview).

### Set up the webhook

Saving a new connection shows the **payload URL** (`https://<meridian>/api/code-sync/<connection id>/webhook`) and a
**secret**. Copy the secret then: it is stored encrypted and not shown again (the webhook button can create a new one).

* **GitHub**: *Settings → Webhooks → Add webhook*. Paste the URL, content type `application/json`, paste the secret,
  choose *Let me select individual events*: **Pushes** and **Pull requests**. GitHub signs each delivery with
  `X-Hub-Signature-256` (HMAC-SHA256).
* **GitLab**: *Settings → Webhooks → Add new webhook*. Paste the URL, paste the secret as *Secret token*, tick
  *Push events* and *Merge request events*. GitLab sends the secret back in `X-Gitlab-Token`.
* **Bitbucket Cloud**: *Repository settings → Webhooks → Add webhook*. Bitbucket Cloud does not sign deliveries unless a
  secret is set there, so the URL Meridian shows carries the secret: `…/webhook?token=<secret>` (compared in constant
  time). Treat that URL like a password; it can appear in proxy access logs, so regenerate the secret if it leaks.
  Triggers: *Repository: Push* and *Pull request: Created, Updated, Merged, Declined*. If you also paste the secret into
  Bitbucket's *Secret* field, a delivery signed with a valid `X-Hub-Signature` is accepted without the token.
* **Bitbucket Data Center / Server**: *Repository settings → Webhooks → Create webhook*. Paste the URL and the secret.
  Deliveries are signed with `X-Hub-Signature: sha256=<HMAC-SHA256>`, which Meridian requires. Events:
  *Repository: Push* and *Pull request: Opened, Source branch updated, Merged, Declined, Deleted*. *Test connection*
  in Bitbucket sends `diagnostics:ping`, which Meridian answers with `200`.

The endpoint answers `401` for a missing or wrong signature/token, `404` for an unknown connection, `200` to a ping
(GitHub, Bitbucket Data Center), `202` for pushes and pull request events it queued or ignored (other branch, deleted
branch, pull request into another branch or without new commits, other events, automatic sync or previews off), `400`
for a payload it cannot read and `429` when one connection sends more than 30 requests a minute. Set
`Meridian:PublicUrl` so the URL shown and the review links in commit statuses and comments use your public address.
The desktop app cannot receive webhooks; use the sync button.

| Host | Push event | Pull request events (opened / updated → preview; merged / closed → remove preview) |
|---|---|---|
| GitHub | `push` | `pull_request`: `opened`, `reopened`, `ready_for_review`, `synchronize`; `closed` (merged or not) |
| GitLab | `Push Hook` | `Merge Request Hook`: `open`, `reopen`, `update` with new commits; `merge`, `close` |
| Bitbucket Cloud | `repo:push` | `pullrequest:created`, `pullrequest:updated`; `pullrequest:fulfilled`, `pullrequest:rejected` |
| Bitbucket Data Center | `repo:refs_changed` | `pr:opened`, `pr:from_ref_updated`; `pr:merged`, `pr:declined`, `pr:deleted` |

### How a sync runs

Webhooks never sync inside the request: the push is queued and a background worker runs syncs one at a time. If
several pushes arrive for one connection while it waits, only the latest commit is synced (every sync reads a full
snapshot). Each pull request waits in its own slot, so it never replaces a push to the branch; closing a pull request
replaces its waiting preview sync. For each sync Meridian downloads the repository archive at the commit (GitHub
tarball, GitLab `archive.tar.gz`, Bitbucket Cloud `…/get/<sha>.tar.gz`, Bitbucket Data Center
`/rest/api/1.0/…/archive?format=tar.gz`), streams it without writing to disk, and keeps the C# files in the watched
folders. Limits: 200 MB archive, 4 GB unpacked, 20,000 source files, 500 MB of source in total; source files over 2 MB
are skipped. Archives with absolute or `..` paths are rejected; symlinks and hard links are ignored.

For a pull request, Meridian posts the status on the PR head, syncs into the preview branch, and writes or edits its
comment (GitHub issue comments, GitLab merge request notes, Bitbucket pull request comments; Data Center edits send the
comment's current version). A failed comment is logged and does not fail the sync.

All calls to the git host use the same address checks as issue trackers (see *Which addresses the server will
call*): allow private networks for an on-premises GitHub Enterprise, GitLab or Bitbucket Data Center. The access token
and the webhook secret are encrypted like other integration secrets and never written to the log.

### Submodules

Archives leave out the code of git submodules. After the archive, Meridian reads `.gitmodules`, asks the host for each
submodule's pinned commit (GitHub contents API, GitLab repository tree, Bitbucket Cloud `src` listing, Data Center
`browse`), downloads the submodule's archive at that commit and merges its files in under the submodule path, nested up
to three levels, with cycles cut. The archive limits above apply to the repository and its submodules **together**.

Which credentials read a submodule:

| Submodule is… | Read with |
|---|---|
| a repository another code-repo connection **of the same project** follows | that connection's token and server URL |
| on the connection's own host (`../lib.git`, or an absolute URL on the same host) | the connection's token |
| on `github.com`, `gitlab.com` or `bitbucket.org` | anonymously (public repositories) — also the fallback when a token above is refused |
| anywhere else | not read: *no access* |

Tokens are only sent to the API address of their own connection, and `.gitmodules` URLs never pick an address: a
submodule on an unknown host costs no request at all. The sync goes on without a submodule it cannot read and says so in
its summary (“1 submodule not read”); the connection lists each submodule of the last sync with its pinned commit and
status (*fetched*, *skipped*, *no access*, *not found*). To read a private submodule on another account or host, add a
connection for it in the same project (its own sync can stay manual).

The token needs read access to the submodule repositories as well: GitHub fine-grained tokens list each repository
(*Contents: read*), GitLab project tokens only see their own project (use a group or personal token, or a connection per
submodule), Bitbucket repository access tokens likewise.

## 7. Databases (live schema import)

Integrations ▸ **Databases** (also project menu ▸ **Connect to a database…**) keeps connections to databases whose
schema is imported into the model and refreshed later with a review. Code: `src/Meridian.Web/Integrations/Databases`
(`DatabaseConnectionService`), `src/Meridian.Data/Databases/LiveSchemaReader.cs`, UI
`Components/Workbench/Database/DatabasesPanel.razor`. The modelling side is described in [DATABASE.md](DATABASE.md).

| Engine | Driver | Reads |
|---|---|---|
| PostgreSQL | Npgsql | `pg_catalog`: schemas, tables, views, materialized views, columns, keys, indexes, triggers, sequences, enum / domain types, routines with bodies, comments |
| SQL Server / Azure SQL | Microsoft.Data.SqlClient 7.1 (MIT) | `sys.*` catalog views: schemas, tables, views, columns, keys, defaults (with their names), indexes, triggers, sequences, alias types, procedures and functions with bodies (`sys.sql_modules`), `MS_Description` |
| MySQL / MariaDB | MySqlConnector 2.6 (MIT) | `information_schema`, routine and trigger bodies |
| Oracle | Oracle.ManagedDataAccess.Core 23.26 (Oracle Free Use Terms) | `ALL_*` views of the user's schema (or the listed ones): tables, views, materialized views, columns, keys, indexes, sequences, procedures, functions and triggers with their source, comments |
| SQLite (desktop / local mode only) | Microsoft.Data.Sqlite | the DDL in `sqlite_master` |

Microsoft.Data.SqlClient 7 no longer depends on Azure.Identity or MSAL (that moved to a separate package Meridian does
not ship); its native Windows SNI library is excluded and the managed network layer is used everywhere. Oracle's driver
may be redistributed unmodified with its license text, which ships in `licenses/`. Sizes and the full license
discussion are in [DATABASE.md](DATABASE.md#drivers-and-licenses).

A connection is a `project_integrations` row of kind `database`: the config JSON holds the provider, a display host,
the schemas to read, a timeout (5–600 s) and the database element it fills; the **connection string is the secret**,
encrypted like the other integration secrets, never returned to the browser, never logged (driver errors have
passwords masked).

* **Read-only.** Meridian only runs catalog queries inside a read-only transaction (SQLite opens the file read-only;
  SQL Server, which has none, gets only SELECTs in a transaction that is rolled back) and never executes DDL. Still,
  connect with a **read-only account**: PostgreSQL — a role with `CONNECT` and `USAGE` on the schemas; SQL Server — a
  database user with `VIEW DEFINITION`; MySQL — `SELECT` and `SHOW VIEW`; Oracle — `CREATE SESSION` (its own schema)
  or dictionary access for other schemas.
* **Who.** On a team server only **server administrators** can add, change, test or delete connections; project
  members can import and refresh through saved ones. In the desktop app's local mode anyone can.
* **Where.** The host is resolved once and checked like webhook URLs (*Which addresses the server will call*):
  link-local and metadata addresses are always refused; loopback and private networks need
  `Meridian:Integrations:AllowPrivateNetworks=true`. The connection then goes to the checked address (with PostgreSQL
  `SSL Mode=VerifyFull` and Oracle TCPS the host name is kept for certificate validation; SQL Server validates the
  certificate against the name through `HostNameInCertificate`). SQLite files and Unix sockets are refused on a team
  server (they are paths on the server), as are SQL Server named pipes / shared memory / LocalDB and Oracle TNS aliases;
  multi-host connection strings (a SQL Server failover partner, an Oracle descriptor with several addresses) are refused.
* **Time.** Connect and command timeouts follow the connection's timeout, and the whole read is cancelled when it
  runs out.

## Storage

New tables (created by `MigrateAsync`, in the integrations migration block of each store):
`project_integrations` (id, project, kind `issue-tracker`/`webhook`/`code-repo`/`requirements-tool`/`database`, name, config JSON, encrypted secret),
`webhook_deliveries` (delivery log) and `api_tokens`.
