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
   release with notes generated from the merged PRs. If the tag contains a `-` (for example `v0.3.0-rc.1`), it is marked as a pre-release.

```sh
# bump <Version> in src/Meridian.Desktop/Meridian.Desktop.csproj, commit, then:
git tag v0.3.0
git push origin v0.3.0
```

You can also start the workflow by hand (**Actions → Release → Run workflow**) to build and download the packages as
workflow artifacts. A manual run never publishes a release.

## Building locally

```sh
build/package.sh 0.3.0 osx-arm64              # one platform
build/package.sh 0.3.0                        # osx-arm64 osx-x64 win-x64 linux-x64 + server
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
`MACOS_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" APPLE_ID=… APPLE_APP_PASSWORD=… APPLE_TEAM_ID=… build/package.sh 0.3.0 osx-arm64`

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

When the app starts (if **Check for updates** on the start page is on, which is the default), it checks at most once a
day with `GET https://api.github.com/repos/finepointcgi/meridian/releases/latest`. Drafts and pre-releases are ignored.
If the tag is a newer semantic version than the app's own (`<Version>` in the csproj, passed by `package.sh -p:Version=`),
the start page shows a banner. Pages from the web app also get a notice through the Photino bridge: they send `hello`
and the host answers `{type:"update"}`.

Choosing **Install** downloads the installer for the platform to a temporary folder, with progress:

| Platform | File |
|---|---|
| macOS (arm64 or x64) | `.dmg` |
| Windows | `-setup.exe`, or an `.msi` if one is published |
| Linux | `.AppImage` |

The download's SHA-256 must match its line in the release's `SHA256SUMS.txt`, or the file is deleted. The app then
mounts the dmg, runs the setup or starts the new AppImage, and quits.

- While the repository is private, the anonymous API call returns 404. The app logs this to `desktop.log` and shows no banner.
- Set `MERIDIAN_NO_UPDATE_CHECK=1` to turn off checks entirely, for example for managed installs.
- Logic and tests: `src/Meridian.Desktop/Updater.cs` and `tests/Meridian.Desktop.Tests`.

Release asset names are part of the updater's contract. Keep the `Meridian-<version>-<os>-<arch>` prefix and the
extensions listed above, and always publish `SHA256SUMS.txt`.
