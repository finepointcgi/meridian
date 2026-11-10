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
   release with notes generated from the merged PRs. If the tag contains a `-` (for example `v0.6.0-rc.1`), it is marked as a pre-release.

```sh
# bump <Version> in src/Meridian.Desktop/Meridian.Desktop.csproj, commit, then:
git tag v0.6.0
git push origin v0.6.0
```

You can also start the workflow by hand (**Actions → Release → Run workflow**) to build and download the packages as
workflow artifacts. A manual run never publishes a release.

## Building locally

```sh
build/package.sh 0.6.0 osx-arm64              # one platform
build/package.sh 0.6.0                        # osx-arm64 osx-x64 win-x64 linux-x64 + server
```

Files go to `artifacts/` together with a `SHA256SUMS.txt`. Each format is built when its tool is available and skipped
with a note otherwise:

| Format | Tool | Where it exists |
|---|---|---|
| `.dmg` | `hdiutil` | macOS |
| Windows installer | Inno Setup 6 `ISCC.exe` (set `ISCC=` if it is not on PATH) | Windows |
| `.AppImage` | `appimagetool` (set `APPIMAGETOOL=`) | Linux x64 |
| `.deb` | `dpkg-deb` | Debian or Ubuntu |

The `.zip`, `.tar.gz` and server zip build anywhere the .NET 10 SDK runs, so a Mac can build every platform's portable
archive. Set `KEEP_WORK=1` to keep `artifacts/work/` (the staged `.app`, AppDir and `.deb` tree) for inspection.

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
`MACOS_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" APPLE_ID=… APPLE_APP_PASSWORD=… APPLE_TEAM_ID=… build/package.sh 0.6.0 osx-arm64`

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

The desktop app checks for a newer version when it starts, at most once a day, if **Automatically check daily** is on
(Settings ▸ Desktop, or **Check for updates** on the start page). It is on by default. **Check for updates** in
Settings ▸ Desktop checks right away. That section shows the running version, when the last check ran, and its result
in a sentence, for example:

- "You're up to date (0.6.0)."
- "0.6.0 is available — Install and restart." (with an **Install 0.6.0 and restart** button and a progress bar)
- "Couldn't reach GitHub: the repository is private (or has no releases yet) — set a token or another update source in Settings."
- "No package for this computer in release 0.6.0."

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
  (`.Meridian.meridian-update/new`, beside `Meridian/`, `meridian-0.6.0/` or `Meridian.app`), so the swap is a rename on
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
  folder keeps its name, such as `meridian-0.6.0`. On Windows, a setup-made install keeps its uninstaller.
  On the next start the app deletes the staging and `-old` folders. The data folder (`MERIDIAN_DATA`, or the
  application-data `Meridian` folder: the database, `desktop.json` and keys) is outside the install and is never moved.
  If it is inside the install folder, the app does not swap the folder.
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

The token is pasted into a password field that Blazor never reads. JavaScript sends it straight to the desktop host,
so it does not pass through a team server even when the window is showing one. The token is stored in `desktop.json`
encrypted with ASP.NET Core Data Protection, the same mechanism as the AI assistant's API key. The keys are in
`desktop-keys/` in the data folder, and on Windows they are also protected with DPAPI. The token is only ever sent to
`api.github.com`. GitHub answers asset requests with a redirect to `objects.githubusercontent.com`, and the updater
follows redirects itself, without the token.

All update traffic is HTTPS only, and a redirect to `http://` is refused. The only exception is a manifest address that
is explicitly set to `http://localhost` or `http://127.0.0.1`, for testing. Its assets may then also come from loopback
over http.

### Manifest format

```json
{
  "version": "0.6.0",
  "notes": "https://meridian.example.com/release-notes/0.6.0",
  "assets": [
    { "name": "Meridian-0.6.0-windows-x64.zip", "url": "files/Meridian-0.6.0-windows-x64.zip", "sha256": "<64 hex digits>", "size": 187654321 },
    { "name": "Meridian-0.6.0-macos-arm64.zip", "url": "https://cdn.example.com/Meridian-0.6.0-macos-arm64.zip", "sha256": "<64 hex digits>" }
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
above. Keep the archive layouts that `package.sh` produces: `Meridian/` in the Windows zip, `meridian-<version>/` in the
Linux tar.gz, and `Meridian.app/` in the macOS zip. Always publish `SHA256SUMS.txt`.

### What only a real release can verify

The unit tests cover package selection, checksums, the token rules, the manifest, safe unpacking and both scripts'
content. On macOS and Linux, `update.sh` actually runs in a temporary folder. These still need a real release on each OS:

- running `update.cmd` on Windows (including a file still locked by antivirus or the FMU host);
- the macOS `.app` swap in `/Applications` under App Management privacy settings, and Gatekeeper on the relaunched bundle;
- GitHub's real asset redirect with a fine-grained token.
