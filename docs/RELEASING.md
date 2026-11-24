# Releasing Meridian

A release is a git tag. Pushing `vX.Y.Z` runs `.github/workflows/release.yml`, which:

1. **test** (ubuntu): builds the solution and runs every test.
2. **package** (matrix, each on its native runner, all running `build/package.sh`):

   | Runner | Files |
   |---|---|
   | `macos-latest` | `Meridian-X.Y.Z-macos-arm64.dmg` / `.zip`, `Meridian-X.Y.Z-macos-x64.dmg` / `.zip` |
   | `windows-latest` | `Meridian-X.Y.Z-windows-x64-setup.exe` (installer), `Meridian-X.Y.Z-windows-x64.zip` (portable) |
   | `ubuntu-latest` | `Meridian-X.Y.Z-linux-x64.AppImage`, `.deb`, `.tar.gz`, and `Meridian-Server-X.Y.Z.zip` |

3. **release** (ubuntu): collects every file, writes `SHA256SUMS.txt` (`sha256sum` format), and publishes the GitHub
   release with notes generated from the merged PRs. If the tag contains a `-` (for example `v0.10.1-rc.1`), it is marked as a pre-release.

```sh
# bump <Version> in src/Meridian.Desktop/Meridian.Desktop.csproj, commit, then:
git tag v0.10.1
git push origin v0.10.1
```

Release notes for a version that changes how keys or secrets are stored must say so. Release 0.10.1 (stable key name, key ring in
the database) signs everyone out once after the upgrade and re-encrypts stored secrets on first start; see
[DEPLOYMENT.md, Keys and secrets](DEPLOYMENT.md#keys-and-secrets).

You can also start the workflow by hand (**Actions → Release → Run workflow**) to build and download the packages as
workflow artifacts. A manual run never publishes a release.

## Releasing with GitLab

`.gitlab-ci.yml` is the GitLab twin of `.github/workflows/`. Keep the two in step: `tests/Meridian.Tests/GitLabCiTests.cs`
parses it and checks that it builds and links the same files that `release.yml` checks for.

| Job | Stage | Runs on | When | GitHub equivalent |
|---|---|---|---|---|
| `test` | test | any Linux runner, `mcr.microsoft.com/dotnet/sdk:10.0` | merge requests, default branch, `v*` tags | `ci.yml` test |
| `e2e` | test | same, plus `install-browsers.sh --with-deps`, `MERIDIAN_E2E=require` | same | `ci.yml` e2e |
| `postgres-store` | test | same, with a `postgres:17-alpine` service (`MERIDIAN_POSTGRES_CONNECTION`) | same | `ci.yml` postgres-store (Testcontainers) |
| `package-smoke` | test | same | merge requests, default branch | `ci.yml` package-smoke |
| `docs` | test | same | changes under `docs-site/` or `src/Meridian.Scripting/` | `docs.yml` build |
| `package:linux` | package | Linux runner (appimagetool downloaded, `dpkg-deb` in the image) | `v*` tags, manual pipelines | `release.yml` linux |
| `package:windows` | package | `saas-windows-medium-amd64` (Inno Setup from Chocolatey, .NET from `dotnet-install.ps1`, signtool from the Windows SDK) | same | `release.yml` windows |
| `package:macos` | package | `saas-macos-medium-m1`, image `macos-15-xcode-16` | same; **manual** unless `MACOS_RUNNER_ENABLED=true` | `release.yml` macos |
| `release:upload` | release | Linux | `v*` tags | `release.yml` release (SHA256SUMS.txt) |
| `release:publish` | release | `release-cli` image, `release:` keyword | `v*` tags | `softprops/action-gh-release` |
| `release:macos` | publish | Linux | after `package:macos` has run on a tag | (part of the release job) |
| `container` | publish | Docker-in-Docker | `v*` tags; manual pipelines build only (push the SHA tag with `PUSH_IMAGE=true`) | `container.yml` |
| `pages` | publish | Linux | manual job on the default branch, or a manual pipeline with `PUBLISH_PAGES=true` | `docs.yml` deploy |

A tag release:

1. The package jobs run `build/package.sh` with `NO_CHECKSUMS=1`, exactly as on GitHub, and check that every expected
   file exists.
2. `release:upload` writes `SHA256SUMS.txt` over the Windows and Linux files and uploads everything to the project's
   generic package registry as `meridian/<version>/<file>`
   (`$CI_API_V4_URL/projects/$CI_PROJECT_ID/packages/generic/meridian/<version>/<file>`), using `CI_JOB_TOKEN`.
3. `release:publish` creates the GitLab release for the tag with one asset link per file, pointing at the registry.
   The desktop updater's **GitLab releases** source reads these links (see [Update sources](#update-sources)).
   GitLab has no pre-release flag. The updater ignores versions with a `-` itself.
4. `release:macos` waits for `package:macos`. When that job has run, `release:macos` uploads the four macOS files,
   uploads `SHA256SUMS.txt` again with their checksums added (the registry serves the newest file with a name, so the
   existing link stays valid), and adds the macOS links to the release. If you never run `package:macos`, this job
   never runs and the release has no macOS files.
5. `container` builds `linux/amd64` and `linux/arm64` with Buildx on Docker-in-Docker and pushes them to
   `$CI_REGISTRY_IMAGE` with an SBOM and provenance. It then signs them with cosign keyless, using the job's
   `SIGSTORE_ID_TOKEN`.

```sh
git tag v0.10.1
git push gitlab v0.10.1
```

To build without releasing, use **Build ▸ Pipelines ▸ Run pipeline** with `VERSION=0.10.1-test`. The packages are
then job artifacts for 7 days.

### Runners

- **Linux jobs** run on any Docker executor (GitLab.com shared runners: `saas-linux-small-amd64`, the default).
  `package:linux` and `e2e` install packages with `apt-get`, so the job must run as root, which is the default in
  these images. `container` needs a runner that allows privileged Docker-in-Docker. GitLab.com shared runners do.
- **Windows** (`saas-windows-medium-amd64`): GitLab's hosted Windows runners are available on every GitLab.com plan.
  On self-managed GitLab, register a Windows runner with the PowerShell shell, Git for Windows and Chocolatey, and
  give it the `saas-windows-medium-amd64` tag, or change the tag in `.gitlab-ci.yml`.
- **macOS** (`saas-macos-medium-m1`): GitLab's hosted macOS runners need a **Premium or Ultimate** plan on GitLab.com.
  That is why `package:macos` is a manual job that may fail. Set `MACOS_RUNNER_ENABLED=true` (Settings ▸ CI/CD ▸
  Variables) once you have the runners, so it runs on every tag. Self-managed: register a macOS shell runner with
  Xcode and give it that tag. Apple-silicon runners build both `osx-arm64` and `osx-x64`.

### CI/CD variables

Add these under **Settings ▸ CI/CD ▸ Variables**. Mark the secrets **Masked and hidden** and **Protected**, and protect
the `v*` tags (**Settings ▸ Repository ▸ Protected tags**, wildcard `v*`), because protected variables are only
given to pipelines for protected refs. Every signing variable is optional, as on GitHub.

| Variable | Used by | Value |
|---|---|---|
| `APPLE_CERT_P12_BASE64` | `package:macos` | Developer ID Application certificate and key as `.p12`, base64. Mask it, or use a **File** variable and `$(base64 < "$APPLE_CERT_P12_FILE")` if it is too long to mask |
| `APPLE_CERT_PASSWORD` | `package:macos` | The `.p12` password |
| `APPLE_TEAM_ID` | `package:macos` | 10-character Team ID |
| `APPLE_ID` | `package:macos` | Apple ID e-mail for notarization |
| `APPLE_APP_PASSWORD` | `package:macos` | App-specific password for that Apple ID |
| `WINDOWS_CERT_PFX_BASE64` | `package:windows` | Authenticode certificate and key as `.pfx`, base64 |
| `WINDOWS_CERT_PASSWORD` | `package:windows` | The `.pfx` password |
| `WINDOWS_TIMESTAMP_URL` | `package:windows` | Optional RFC 3161 timestamp server (default DigiCert) |
| `MACOS_RUNNER_ENABLED` | `package:macos` | `true` to build macOS automatically on tags |
| `RELEASE_API_TOKEN` | `release:macos` | Optional. A project access token (Developer role, `api` scope) for the release-links API, if your GitLab version refuses `CI_JOB_TOKEN` there |
| `COSIGN_SIGN` | `container` | `false` to skip cosign, for example on a self-managed GitLab whose OIDC issuer Sigstore does not trust |
| `PUSH_IMAGE`, `PUBLISH_PAGES`, `VERSION` | manual pipelines | See the table above |

`CI_REGISTRY*`, `CI_JOB_TOKEN`, `CI_API_V4_URL` and `SIGSTORE_ID_TOKEN` (from `id_tokens:`) are provided by GitLab.
Nothing needs a personal token, except the optional `RELEASE_API_TOKEN`. `build/macos/import-cert.sh` prints
`export MACOS_SIGN_IDENTITY=… MACOS_KEYCHAIN=…` when it is not running on GitHub. The macOS job `eval`s that line, and
`after_script` deletes the temporary keychain.

### What only a real GitLab pipeline can verify

The YAML is checked by `GitLabCiTests` (structure, references, `bash -n` on every Linux and macOS script, and the
same files as `release.yml`), but not by GitLab's own linter. These still need a real run: the CI Lint page
(**Build ▸ Pipeline editor ▸ Validate**), the Windows PowerShell job, the hosted macOS image name, notarization on
GitLab's macOS runners, `release-cli` with dotenv-expanded asset links, `CI_JOB_TOKEN` on the release-links API,
Buildx multi-arch with attestations into the GitLab registry, cosign keyless with GitLab's OIDC token, and Pages
publishing.

## Building locally

```sh
build/package.sh 0.10.1 osx-arm64              # one platform
build/package.sh 0.10.1                        # osx-arm64 osx-x64 win-x64 linux-x64 + server
```

Files go to `artifacts/` together with a `SHA256SUMS.txt`. Each format is built when its tool is available and skipped
with a note otherwise:

| Format | Tool | Where it exists |
|---|---|---|
| `.dmg` | `hdiutil` | macOS |
| Velopack packages: Windows setup and zip, macOS bundle, feed files | `vpk` (pinned in `.config/dotnet-tools.json`, restored by the script) | anywhere (Windows is cross-packed) |
| `.AppImage` | `mksquashfs` for Velopack's (`brew install squashfs` on a Mac); else `appimagetool` (set `APPIMAGETOOL=`) | macOS, Linux |
| Classic Windows installer (only with `NO_VELOPACK=1`) | Inno Setup 6 `ISCC.exe` (set `ISCC=`) | Windows |
| `.deb` | `dpkg-deb` | Debian or Ubuntu |

The `.zip`, `.tar.gz` and server zip build anywhere the .NET 10 SDK runs, so a Mac can build every platform's portable
archive. Set `KEEP_WORK=1` to keep `artifacts/work/` (the staged `.app`, AppDir and `.deb` tree) for inspection.

Before the platforms, the script builds the user guide once with DocFX (`dotnet tool restore` and `dotnet docfx` in
`docs-site`, the version pinned in `docs-site/dotnet-tools.json`, with `--warningsAsErrors`) and copies it into every
desktop package and the server zip as `guide/`, which the app serves at `/guide`. `SKIP_GUIDE=1` leaves it out. The
Dockerfile does the same into `/app/guide`. The test suites never build the guide; `UserGuideTests` checks that the
script and the Dockerfile do.

## Code signing

Signing is optional. Without the secrets below, the workflow still produces working packages: macOS builds are
ad-hoc signed (users right-click, then **Open** the first time), and Windows builds are unsigned (SmartScreen asks for
confirmation). Add these under **Settings → Secrets and variables → Actions** on `finepointcgi/meridian`:

### macOS: Developer ID and notarization

| Secret | Value |
|---|---|
| `APPLE_CERT_P12_BASE64` | A **Developer ID Application** certificate and its private key, exported from Keychain Access as `.p12`, then `base64 -i cert.p12 \| pbcopy` |
| `APPLE_CERT_PASSWORD` | The password you chose when exporting the `.p12` |
| `APPLE_TEAM_ID` | Your 10-character Team ID (developer.apple.com → Membership) |
| `APPLE_ID` | The Apple ID email used for notarization |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID (appleid.apple.com → Sign-In and Security) |

What happens on the macOS runner:

1. `build/macos/import-cert.sh` creates a temporary keychain with a random password, imports the `.p12`, allows
   `codesign` to use the key, and exports `MACOS_SIGN_IDENTITY` and `MACOS_KEYCHAIN`. The keychain is deleted at the
   end of the job, even when the job fails.
2. `package.sh` signs every nested Mach-O file (native libraries, `createdump`), then the bundle, using the hardened
   runtime, a secure timestamp and `build/macos/entitlements.plist`. The entitlements are JIT and unsigned executable
   memory for .NET, library validation off for the bundled native libraries, and network client and server for the local workspace.
3. It builds the `.dmg` (Meridian.app plus an `Applications` link), signs it, submits it with `xcrun notarytool submit --wait`,
   and staples the ticket to the dmg and to the app. The `.zip` is made from the stapled app. If notarization is
   rejected, the job fails and prints the notary log.

If you have only the certificate secrets, the build is Developer ID signed but not notarized. If you have none, it is ad-hoc signed.

To sign locally, import the certificate into your login keychain and run:
`MACOS_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" APPLE_ID=… APPLE_APP_PASSWORD=… APPLE_TEAM_ID=… build/package.sh 0.10.1 osx-arm64`

### Windows: Authenticode

| Secret | Value |
|---|---|
| `WINDOWS_CERT_PFX_BASE64` | A code-signing certificate and key as `.pfx`, base64-encoded (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`) |
| `WINDOWS_CERT_PASSWORD` | The `.pfx` password |

The workflow finds `signtool.exe` in the Windows SDK. `package.sh` signs `Meridian.exe` before it is zipped and put in
the installer, and then signs the installer. Both use SHA-256 and an RFC 3161 timestamp (`WINDOWS_TIMESTAMP_URL`,
default DigiCert). The decoded `.pfx` is written to the build folder and deleted when the script exits. The uninstaller
that Inno Setup generates is not signed separately.

Certificates kept in hardware (EV certificates, Azure Trusted Signing) cannot be exported as `.pfx`. For those, replace
`windows_sign` in `package.sh` with your provider's signing command.

## Installers and file types

- **macOS:** drag Meridian.app from the `.dmg` to Applications. `Info.plist` declares the `io.meridian.project` type
  (`.meridian`, which conforms to `public.json`) as owned by the app, and `org.omg.sysml.text` (`.sysml`) as a type it can open.
  Opening a document sends an Apple Event. `MacOpenFiles.cs` adds `application:openFiles:` to Photino's app delegate to receive it.
- **Windows:** `build/windows/meridian.iss` installs per user, without administrator rights, to
  `%LOCALAPPDATA%\Programs\Meridian`. It adds a Start menu shortcut, an optional desktop shortcut and an uninstaller
  (Settings → Apps), and registers `.meridian` and `.sysml` under `HKCU\Software\Classes` (`Meridian.Project`, `Meridian.SysML`)
  to open with `Meridian.exe "%1"`.
- **Linux:**
  - `.deb`: installs to `/opt/meridian`, with `/usr/bin/meridian`, a menu entry, an icon and the
    `application/x-meridian` / `text/x-sysml` types from `build/linux/meridian-mime.xml`.
  - `.tar.gz`: run `./install.sh` (or `./install.sh --uninstall`) to register the same things for the current user.
  - AppImage: carries the `.desktop` file (with its MimeType line) and the MIME XML. AppImageLauncher or appimaged integrates them.

When the app starts with a model path (Windows, Linux) or receives an open-document event (macOS), it starts the local
workspace and POSTs the path to `/desktop/import` on the in-process server. The server accepts the request only in
local mode, from loopback, with the launch token in `X-Meridian-Launch`, and only for a full path ending in `.meridian`,
`.sysml`, `.xmi`, `.qea` or `.qeax`. It imports the file as a new project and returns its ID. The window then opens `/?project=<id>`.
The start page lists recent files. Opening an unchanged recent file reopens its project instead of importing it again.

## Auto-update

### One-click updates (Velopack)

Since 0.11 the desktop packages are built with [Velopack](https://velopack.io), as in GitForge. A copy installed from
the Windows setup or zip, the macOS `.dmg` or `.zip`, or the AppImage updates itself: **Download and install** (start
page) or **Install and restart** (Settings ▸ Desktop) downloads the new release, verifies it, quits Meridian (the local
workspace is saved first), swaps the files and starts the new version. Nothing is installed without that click.

- **Feed.** Each platform is a Velopack channel named by its runtime: `osx-arm64`, `osx-x64`, `win-x64`, `linux-x64`.
  `package.sh` writes `releases.<channel>.json` (the release index: version, file name, SHA-256 and size of each package),
  `assets.<channel>.json`, `RELEASES-<channel>` and `Meridian-<version>-<channel>-full.nupkg`. Upload them with the other
  files to the GitHub release. The app reads the same update source as before (Settings ▸ Desktop ▸ Update source):
  GitHub releases (with the token for a private repository), a GitLab project's releases, or, for **Custom URL**, the
  folder the manifest is in (a team server's `/api/desktop/latest` reads Velopack files from `/api/desktop/files/`, so
  put them in the server's updates folder too).
- **Deltas.** Before packing, the script downloads the previous release from GitHub (`vpk download github`, with
  `GITHUB_TOKEN` if set) and builds `Meridian-<version>-<channel>-delta.nupkg`, so an update is usually a few MB. The
  index then lists both versions. `NO_DELTA=1` skips this. The first Velopack release has no deltas.
- **Verification.** Velopack checks each package's SHA-256 and size against the index before applying it. The index
  comes from the same source as everything else (HTTPS only, the token only to its own host).
- **Release notes.** `RELEASE_NOTES=notes.md build/package.sh …` puts them in the index.
- **Install locations.** Windows: per user in `%LOCALAPPDATA%\Meridian` (no administrator rights), with Start menu and
  desktop shortcuts and an entry in Settings ▸ Apps. macOS: wherever the user puts Meridian.app (the bundle carries
  `UpdateMac`). Linux: the AppImage file.
- **Fallback.** When this copy wasn't installed from a Velopack package (a development build, the `.deb`, the `.tar.gz`,
  or an install from 0.10.x), or the source has no Velopack files for this channel (an older release, a hand-written
  manifest), the classic updater below does the update. Copies on 0.10.x take the classic path once: the packages it
  installs are Velopack packages, so later updates are one-click.
- **Signing.** Without `MACOS_SIGN_IDENTITY` the macOS bundle is ad-hoc signed, as before. With it, `vpk` signs the bundle
  (and its updater) with that identity and the entitlements. The Windows setup and stub are not Authenticode-signed
  when packed on a Mac, so SmartScreen warns on first run.
- **Turning it off.** `NO_VELOPACK=1` builds the classic packages only (and the Inno Setup installer where ISCC exists).

The code is in `src/Meridian.Desktop/VelopackUpdates.cs` (feed and install) and `Program.cs` (`VelopackApp.Build().Run()`
first in `Main`, and `TryVelopackUpdateAsync`). The update check and the messages are unchanged.

### Classic updater

The desktop app checks for a newer version when it starts, at most once a day, if **Automatically check daily** is on
(Settings ▸ Desktop, or **Check for updates** on the start page). It is on by default. **Check for updates** in
Settings ▸ Desktop checks right away. That section shows the running version, when the last check ran, and its result
in a sentence, for example:

- "You're up to date (0.10.1)."
- "0.10.1 is available — Install and restart." (with an **Install 0.10.1 and restart** button and a progress bar)
- "Couldn't reach GitHub: the repository is private (or has no releases yet) — set a token or another update source in Settings."
- "No package for this computer in release 0.10.1."

Drafts and pre-releases are ignored. A release counts as newer when its tag is a higher semantic version than the
app's own (`<Version>` in the csproj, passed by `package.sh -p:Version=`). Pages from the web app also get a notice
through the Photino bridge: they send `hello`, and the host answers `{type:"update"}`. Set `MERIDIAN_NO_UPDATE_CHECK=1`
to turn checks off entirely, for example for managed installs. The logic is in `src/Meridian.Desktop/Updater.cs`,
`UpdateSource.cs` and `PortableInstaller.cs`. The tests are in `tests/Meridian.Desktop.Tests`.

### Which package is installed

| Platform | Preferred | Fallback |
|---|---|---|
| macOS (arm64 or x64) | `.zip` (Meridian.app swapped in place) when the app's folder is writable, otherwise `.dmg` | the other one |
| Windows x64, installed by the setup | `-setup.exe` (or `.msi`) | `windows-x64.zip` |
| Windows x64, portable zip | `windows-x64.zip` | `-setup.exe` |
| Linux x64, from the `.tar.gz` | `linux-x64.tar.gz` | — |
| Linux x64, running as an AppImage | `.AppImage` | — |

So a release built locally with `build/package.sh` (portable archives, macOS dmg and zip, and `SHA256SUMS.txt`) updates
every platform. Every download is checked against its SHA-256 before anything is opened or unpacked: GitHub releases use
the release's `SHA256SUMS.txt`, and manifests carry a `sha256` for each file. A file that does not match is deleted, and
the update stops.

- **Installers.** The `.dmg` is mounted and the app quits, but you still drag Meridian to Applications yourself. The
  Windows setup runs and replaces the per-user install, because `CloseApplications=force` closes the running copy. A new
  AppImage is started.
- **Portable archives (in-place swap).** The archive is unpacked into a hidden staging folder next to the install
  (`.Meridian.meridian-update/new`, beside `Meridian/`, `meridian-0.10.1/` or `Meridian.app`), so the swap is a rename on
  the same disk. Unpacking uses the same rules as the repository's other archive readers (`SafeZip` and
  `SnapshotArchive`): sizes are counted on the bytes actually read, and there are caps on entries, file size and total
  size. Absolute paths, `..`, drive letters, links and more than one top-level folder are rejected. macOS `__MACOSX/`
  and `._*` metadata is skipped. The package must contain the executable for this OS (`Meridian.exe`, `Meridian` or
  `Contents/MacOS/Meridian`). The app then writes `update.cmd` (Windows) or `update.sh` (macOS and Linux) into the
  staging folder, starts it, and quits. The script:
  1. waits for the app's process ID to exit (up to 10 minutes);
  2. moves the old install to `.<name>.meridian-old`;
  3. moves the new one in. If that fails, it puts the old one back;
  4. starts Meridian again (`open Meridian.app` on macOS);
  5. logs each step to `desktop.log` in the data folder.

  The install keeps its path, so shortcuts, file associations and the `install.sh` menu entry keep working. A Linux
  folder keeps its name, such as `meridian-0.10.1`. On Windows, a setup-made install keeps its uninstaller.
  On the next start the app deletes the staging and `-old` folders. The data folder (`MERIDIAN_DATA`, or the
  application-data `Meridian` folder: the database, `desktop.json` and keys) is outside the install and is never moved.
  If it is inside the install folder, the app does not swap the folder. Stored secrets survive the swap: the Data
  Protection key ring has the stable name `Meridian` and lives in the data folder's database, not the install
  ([DEPLOYMENT.md, Keys and secrets](DEPLOYMENT.md#keys-and-secrets)).
- **Install folder can't be written** (for example Program Files, `/opt` from the `.deb`, or `/Applications` without
  admin rights): the app says so and opens the release page so you can download the update yourself. A development build
  (`dotnet run`, which has no self-contained runtime next to it) never replaces itself.

macOS: the `.zip` swap is preferred because it needs no dragging and keeps the app where it is. The bundle is replaced
as a whole, so its signature stays intact. Files the app downloads itself carry no quarantine flag, so Gatekeeper does
not move the app to a temporary location. The `.dmg` remains the fallback when the bundle's folder is not writable.

### Update sources

Settings ▸ Desktop ▸ **Update source**:

| Source | What the app requests |
|---|---|
| **GitHub releases** (default) | `GET https://api.github.com/repos/finepointcgi/meridian/releases/latest` without credentials. While the repository is private, this returns 404, and the app says to set a token or another source |
| **GitHub with a token** | The same request with `Authorization: Bearer <token>`. Assets are downloaded through the asset API (`…/releases/assets/<id>`, `Accept: application/octet-stream`). Use a fine-grained token with read-only access to the repository's **Contents** |
| **Custom URL** | A JSON manifest at an `https://` address (see below), for example your team server's `/api/desktop/latest` |
| **GitLab releases** | `GET <gitlab>/api/v4/projects/<url-encoded path or id>/releases/permalink/latest`, which redirects to the latest release. On GitLab before 15.4, or when the latest is an upcoming release or a pre-release, the app lists `…/releases?per_page=50` and picks the highest published version without a `-`. Assets are the release's **links**. A link into the same GitLab's `/api/v4/` (the generic package registry that `.gitlab-ci.yml` uploads to) is downloaded directly, and any other link through its `direct_asset_url`. The address defaults to `https://gitlab.com`. A self-managed GitLab works too, including one under a sub-path |

The token is pasted into a password field that Blazor never reads. JavaScript sends it straight to the desktop host,
so it does not pass through a team server even when the window is showing one. The token is stored in `desktop.json`
encrypted with ASP.NET Core Data Protection, the same mechanism as the AI assistant's API key. The keys are in
`desktop-keys/` in the data folder, and on Windows they are also protected with DPAPI. The token is only ever sent to
`api.github.com`. GitHub answers asset requests with a redirect to `objects.githubusercontent.com`, and the updater
follows redirects itself, without the token.

The GitLab token is optional; it is needed only for a private project. Use a personal or project access token with
`read_api`. It is pasted, stored and encrypted the same way, in its own `ProtectedGitLabToken` setting, and sent as
`PRIVATE-TOKEN` only to the configured GitLab instance: the same scheme, host and port, under its path. GitLab
redirects package downloads to object storage (or `permalink/latest` to the release), and the updater follows those
redirects without the token. Changing the GitLab address forgets a saved GitLab token, so a token is never sent to a
different instance. Downloads are verified against the release's `SHA256SUMS.txt` link, as for GitHub.

All update traffic is HTTPS only, and a redirect to `http://` is refused. The only exception is a manifest address that
is explicitly set to `http://localhost` or `http://127.0.0.1`, for testing. Its assets may then also come from loopback
over http.

### Manifest format

```json
{
  "version": "0.10.1",
  "notes": "https://meridian.example.com/release-notes/0.10.1",
  "assets": [
    { "name": "Meridian-0.10.1-windows-x64.zip", "url": "files/Meridian-0.10.1-windows-x64.zip", "sha256": "<64 hex digits>", "size": 187654321 },
    { "name": "Meridian-0.10.1-macos-arm64.zip", "url": "https://cdn.example.com/Meridian-0.10.1-macos-arm64.zip", "sha256": "<64 hex digits>" }
  ]
}
```

- `version` (required): a semantic version.
- `notes` (optional): a page to open, for example when the app cannot replace itself.
- `assets[].url` can be relative to the manifest's address.
- `size` is optional.
- Asset names follow the release naming below.
- An asset without a valid `sha256`, or with a non-https `url`, is ignored, so a manifest can never cause an unverified install.

A Meridian team server can host this manifest: see [DEPLOYMENT.md](DEPLOYMENT.md#desktop-updates-from-your-server).

### Release asset names

Asset names are part of the updater's contract. Keep the `Meridian-<version>-<os>-<arch>` prefix and the extensions
above, and the Velopack files' names (`releases.<channel>.json` and the `.nupkg` files it lists). Keep the archive layouts that `package.sh` produces: `Meridian/` in the Windows zip, `meridian-<version>/` in the
Linux tar.gz, and `Meridian.app/` in the macOS zip. Always publish `SHA256SUMS.txt`.

### What only a real release can verify

The unit tests cover package selection, checksums, the token rules, the manifest, safe unpacking and both scripts'
content. On macOS and Linux, `update.sh` actually runs in a temporary folder. These still need a real release on each OS:

- running `update.cmd` on Windows (including a file still locked by antivirus or the FMU host);
- the macOS `.app` swap in `/Applications` under App Management privacy settings, and Gatekeeper on the relaunched bundle;
- GitHub's real asset redirect with a fine-grained token;
- a real GitLab release: `permalink/latest`, package-registry links behind object storage, and a private project with a `read_api` token.
