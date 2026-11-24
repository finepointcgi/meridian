#!/usr/bin/env bash
# Builds Meridian release artifacts into ./artifacts:
#   Meridian-<ver>-macos-<arch>.dmg / .zip          Meridian.app (Developer ID signed + notarized when configured, else ad-hoc)
#   Meridian-<ver>-windows-x64-setup.exe / .zip     per-user installer (Inno Setup, when ISCC is available) and a portable zip
#   Meridian-<ver>-linux-x64.AppImage / .deb / .tar.gz   (AppImage needs appimagetool, .deb needs dpkg-deb)
#   Meridian-Server-<ver>.zip                       web server, needs the .NET 10 runtime (Docker image via Dockerfile)
#   SHA256SUMS.txt                                  checksums of all of the above (the in-app updater verifies against it)
#   releases.<rid>.json, assets.<rid>.json, RELEASES-<rid>, Meridian-<ver>-<rid>-full.nupkg (and -delta.nupkg)
#                                                   the Velopack feed: installed copies update in one click (docs/RELEASING.md)
#
# The .dmg / macOS .zip, the Windows setup and .zip, and the AppImage are Velopack packages (vpk, pinned in
# .config/dotnet-tools.json), so every copy installed from them can update itself. The Windows setup is built on any OS;
# the AppImage needs mksquashfs (brew install squashfs). The .deb and tar.gz stay classic installs.
#
# Usage: build/package.sh [version] [rid ...]     e.g. build/package.sh 0.10.1 osx-arm64
#
# Optional environment (all signing is skipped when unset; see docs/RELEASING.md):
#   MACOS_SIGN_IDENTITY   "Developer ID Application: Name (TEAMID)"; otherwise ad-hoc signing
#   MACOS_KEYCHAIN        keychain holding that identity (build/macos/import-cert.sh sets both)
#   APPLE_ID, APPLE_APP_PASSWORD, APPLE_TEAM_ID   notarize and staple with xcrun notarytool (needs MACOS_SIGN_IDENTITY)
#   WINDOWS_CERT_PFX_BASE64, WINDOWS_CERT_PASSWORD   Authenticode-sign Meridian.exe and the installer with signtool
#   WINDOWS_TIMESTAMP_URL default http://timestamp.digicert.com
#   SIGNTOOL, ISCC, APPIMAGETOOL   tool paths when they are not on PATH
#   SKIP_SERVER=1         do not build the server zip
#   SKIP_GUIDE=1          do not build the user guide (docs-site with DocFX) into the packages' guide/ folder
#   NO_CHECKSUMS=1        do not write SHA256SUMS.txt (CI writes one over every job's files)
#   NO_VELOPACK=1         classic packages only (no vpk): copies installed from them update through the classic updater
#   NO_DELTA=1            do not download the previous release to build delta packages
#   GITHUB_TOKEN          used to download the previous release (deltas) when set; anonymous otherwise
#   RELEASE_NOTES         Markdown file shown in the update prompt (default: none)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-0.10.1}"
shift || true
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(osx-arm64 osx-x64 win-x64 linux-x64)
OUT="$ROOT/artifacts"
WORK="$ROOT/artifacts/work"
NUMERIC_VERSION="${VERSION%%-*}"
mkdir -p "$OUT" "$WORK"
trap 'rm -f "$WORK/signing.pfx"' EXIT

note() { echo "    $*"; }

# ---------------------------------------------------------------- Velopack

REPO_URL="https://github.com/finepointcgi/meridian"
VPK_OUT="$WORK/velopack"

use_velopack() { [ "${NO_VELOPACK:-}" != "1" ]; }

vpk() { (cd "$ROOT" && dotnet tool run vpk -- "$@" --skip-updates); }

# velopack_pack <rid> <directive or ""> <pack dir> <main exe> [extra vpk args...]: packs one platform's release into
# $VPK_OUT/<rid> (with a delta against the previous GitHub release when it can be downloaded) and copies this version's feed
# files to $OUT. Returns non-zero when vpk failed, so the caller keeps its classic package.
velopack_pack() {
  local rid="$1" directive="$2" packdir="$3" mainexe="$4"; shift 4
  local dir="$VPK_OUT/$rid" args=()
  [ -n "$directive" ] && args+=("$directive")
  rm -rf "$dir" && mkdir -p "$dir"
  (cd "$ROOT" && dotnet tool restore >/dev/null)
  if [ "${NO_DELTA:-}" != "1" ]; then
    vpk download github --repoUrl "$REPO_URL" --channel "$rid" -o "$dir" ${GITHUB_TOKEN:+--token "$GITHUB_TOKEN"} >/dev/null 2>&1 \
      && note "previous $rid release downloaded: building a delta package" \
      || note "no previous Velopack release for $rid: full package only"
  fi
  local notes=()
  [ -n "${RELEASE_NOTES:-}" ] && [ -f "$RELEASE_NOTES" ] && notes=(--releaseNotes "$RELEASE_NOTES")
  note "vpk pack $rid"
  if ! vpk ${args[@]+"${args[@]}"} pack --packId Meridian --packVersion "$VERSION" --packDir "$packdir" --mainExe "$mainexe" \
      --channel "$rid" --runtime "$rid" --packTitle Meridian --packAuthors Meridian --outputDir "$dir" ${notes[@]+"${notes[@]}"} "$@" >"$dir.log" 2>&1; then
    note "vpk pack failed for $rid (see below); keeping the classic package"
    tail -5 "$dir.log" | sed 's/^/      /'
    return 1
  fi
  local f
  for f in "$dir/releases.$rid.json" "$dir/assets.$rid.json" "$dir/RELEASES-$rid" "$dir"/Meridian-"$VERSION"-"$rid"-*.nupkg; do
    [ -f "$f" ] && cp "$f" "$OUT/"
  done
  return 0
}

# ---------------------------------------------------------------- user guide
# The user guide (docs-site, DocFX, pinned in docs-site/dotnet-tools.json) is built once and copied into every package as
# guide/, which the app serves at /guide (src/Meridian.Web/Hosting/UserGuide.cs): offline, in the desktop app too.
GUIDE="$WORK/guide"

build_guide() {
  if [ "${SKIP_GUIDE:-}" = "1" ]; then note "SKIP_GUIDE=1: the packages have no user guide"; return 0; fi
  echo "==> user guide"
  (
    cd "$ROOT/docs-site"
    dotnet tool restore >/dev/null
    dotnet docfx metadata docfx.json >/dev/null
    dotnet docfx build docfx.json --warningsAsErrors >/dev/null
  )
  rm -rf "$GUIDE" && mkdir -p "$GUIDE"
  cp -R "$ROOT/docs-site/_site/." "$GUIDE/"
  [ -f "$GUIDE/index.html" ] || { echo "The user guide build produced no index.html" >&2; exit 1; }
}

# add_guide <app dir>: the built guide as <app dir>/guide.
add_guide() {
  [ -d "$GUIDE" ] || return 0
  rm -rf "$1/guide"
  cp -R "$GUIDE" "$1/guide"
}

publish_desktop() {
  local rid="$1" dir="$2"
  rm -rf "$dir"
  # The web app first (its static assets and endpoint manifest), then the desktop host on top: one self-contained folder.
  dotnet publish "$ROOT/src/Meridian.Web" -c Release -r "$rid" --self-contained -p:Version="$VERSION" -o "$dir" --nologo -v q
  dotnet publish "$ROOT/src/Meridian.Desktop" -c Release -r "$rid" --self-contained -p:Version="$VERSION" -o "$dir" --nologo -v q
  # The FMU host (co-simulation runs each FMU in this separate process; docs/FMI.md), self-contained next to the app.
  dotnet publish "$ROOT/src/Meridian.FmuHost" -c Release -r "$rid" --self-contained -p:Version="$VERSION" -o "$dir" --nologo -v q
  # The server executable is not needed inside the desktop app (the desktop host runs it in-process).
  rm -f "$dir/Meridian.Web" "$dir/Meridian.Web.exe" "$dir"/*.pdb "$dir"/appsettings.Development*.json
  add_guide "$dir"
}

# make_zip <dir> <entry> <zip>: zip an entry of a directory (zip, else 7-Zip, else PowerShell on Windows runners).
make_zip() {
  local dir="$1" entry="$2" zipfile="$3"
  rm -f "$zipfile"
  if command -v zip >/dev/null 2>&1; then (cd "$dir" && zip -qry "$zipfile" "$entry")
  elif command -v 7z >/dev/null 2>&1; then (cd "$dir" && 7z a -tzip -bso0 -bsp0 "$(win_path "$zipfile")" "$entry")
  else (cd "$dir" && powershell.exe -NoProfile -Command "Compress-Archive -Path '$entry' -DestinationPath '$(win_path "$zipfile")'")
  fi
}

win_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# ---------------------------------------------------------------- macOS

macos_sign_app() {
  local app="$1" ent="$ROOT/build/macos/entitlements.plist"
  command -v codesign >/dev/null 2>&1 || { note "codesign not available: $app left unsigned"; return 0; }
  if [ -z "${MACOS_SIGN_IDENTITY:-}" ]; then
    note "ad-hoc signing (no MACOS_SIGN_IDENTITY)"
    codesign --force --deep --sign - --entitlements "$ent" --options runtime "$app"
    return 0
  fi
  note "signing with $MACOS_SIGN_IDENTITY (hardened runtime)"
  local kc=()
  [ -n "${MACOS_KEYCHAIN:-}" ] && kc=(--keychain "$MACOS_KEYCHAIN")
  # Inside out: every nested Mach-O (native libraries, createdump), then the bundle (main executable) with the entitlements.
  while IFS= read -r -d '' f; do
    if [ "$(basename "$f")" = "Meridian.FmuHost" ]; then
      # The FMU host is a .NET executable that loads FMUs' (unsigned) native libraries: same entitlements as the app.
      codesign --force --timestamp --options runtime --entitlements "$ent" ${kc[@]+"${kc[@]}"} --sign "$MACOS_SIGN_IDENTITY" "$f"
    elif file -b "$f" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime ${kc[@]+"${kc[@]}"} --sign "$MACOS_SIGN_IDENTITY" "$f"
    fi
  done < <(find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Meridian" -print0)
  codesign --force --timestamp --options runtime --entitlements "$ent" ${kc[@]+"${kc[@]}"} --sign "$MACOS_SIGN_IDENTITY" "$app"
  codesign --verify --strict --verbose=2 "$app"
}

can_notarize() {
  [ -n "${MACOS_SIGN_IDENTITY:-}" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ]
}

# notarize <file.dmg|file.zip>: submit, wait, fail with the notary log unless Accepted.
notarize() {
  local file="$1" result="$WORK/notary.json"
  note "notarizing $(basename "$file")"
  xcrun notarytool submit "$file" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" \
    --wait --timeout 1h --output-format json > "$result" || true
  local status id
  status="$(plutil -extract status raw -o - "$result" 2>/dev/null || echo unknown)"
  id="$(plutil -extract id raw -o - "$result" 2>/dev/null || echo "")"
  if [ "$status" != "Accepted" ]; then
    echo "Notarization of $(basename "$file") failed: $status" >&2
    [ -n "$id" ] && xcrun notarytool log "$id" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" >&2 || true
    return 1
  fi
}

make_dmg() {
  local app="$1" dmg="$2" src="$WORK/dmg-src"
  rm -rf "$src" "$dmg" && mkdir -p "$src"
  ditto "$app" "$src/Meridian.app"
  ln -s /Applications "$src/Applications"
  # hdiutil occasionally fails with "Resource busy" on CI runners; retry a few times.
  local i
  for i in 1 2 3 4 5; do
    if hdiutil create -volname "Meridian $VERSION" -srcfolder "$src" -fs HFS+ -format UDZO -ov "$dmg" >/dev/null; then break; fi
    [ "$i" = 5 ] && return 1
    sleep 3
  done
  rm -rf "$src"
}

package_macos() {
  local rid="$1" arch="${1#osx-}"
  local stage="$WORK/$rid" app="$WORK/$rid/Meridian.app"
  local dmg="$OUT/Meridian-$VERSION-macos-$arch.dmg" zipfile="$OUT/Meridian-$VERSION-macos-$arch.zip"
  publish_desktop "$rid" "$WORK/$rid-files"
  rm -rf "$stage" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$WORK/$rid-files/." "$app/Contents/MacOS/"
  cp "$ROOT/build/assets/meridian.icns" "$app/Contents/Resources/Meridian.icns"
  sed -e "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$app/Contents/Info.plist"
  chmod +x "$app/Contents/MacOS/Meridian" "$app/Contents/MacOS/Meridian.FmuHost"
  macos_sign_app "$app"

  if use_velopack; then
    local vargs=(--bundleId io.meridian.desktop --icon "$ROOT/build/assets/meridian.icns" --noInst)
    if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
      # vpk re-signs the bundle with its updater inside; it wants the entitlements file named *.entitlements.
      cp "$ROOT/build/macos/entitlements.plist" "$WORK/meridian.entitlements"
      vargs+=(--signAppIdentity "$MACOS_SIGN_IDENTITY" --signEntitlements "$WORK/meridian.entitlements")
    fi
    [ -n "${MACOS_KEYCHAIN:-}" ] && vargs+=(--keychain "$MACOS_KEYCHAIN")
    if velopack_pack "$rid" "" "$app" Meridian "${vargs[@]}"; then
      # The .dmg and .zip hold Velopack's bundle (the app plus its updater), so a copy installed from either updates itself.
      rm -rf "$app"
      ditto -x -k "$VPK_OUT/$rid/Meridian-$rid-Portable.zip" "$stage"
      rm -rf "$stage/__MACOSX"
      [ -z "${MACOS_SIGN_IDENTITY:-}" ] && macos_sign_app "$app"
    fi
  fi

  if command -v hdiutil >/dev/null 2>&1; then
    make_dmg "$app" "$dmg"
    if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
      codesign --force --timestamp ${MACOS_KEYCHAIN:+--keychain "$MACOS_KEYCHAIN"} --sign "$MACOS_SIGN_IDENTITY" "$dmg"
    fi
    if can_notarize; then
      notarize "$dmg"
      xcrun stapler staple "$dmg"
      # The dmg's ticket covers the app inside it too: staple it to the app so the zip also opens offline.
      xcrun stapler staple "$app"
    else
      note "not notarized (needs MACOS_SIGN_IDENTITY, APPLE_ID, APPLE_APP_PASSWORD, APPLE_TEAM_ID)"
    fi
  else
    note "hdiutil not available: no .dmg"
  fi
  rm -f "$zipfile"
  if command -v ditto >/dev/null 2>&1; then (cd "$stage" && ditto -c -k --keepParent Meridian.app "$zipfile"); else make_zip "$stage" Meridian.app "$zipfile"; fi
}

# ---------------------------------------------------------------- Windows

find_tool() { # find_tool <env value> <name...> <fallback paths...>
  local v
  for v in "$@"; do
    [ -z "$v" ] && continue
    if [ -f "$v" ]; then printf '%s' "$v"; return 0; fi
    if command -v "$v" >/dev/null 2>&1; then command -v "$v"; return 0; fi
  done
  return 1
}

windows_sign() {
  local target="$1"
  [ -n "${WINDOWS_CERT_PFX_BASE64:-}" ] || { note "not Authenticode-signed (no WINDOWS_CERT_PFX_BASE64): $(basename "$target")"; return 0; }
  local signtool
  if ! signtool="$(find_tool "${SIGNTOOL:-}" signtool.exe signtool)"; then
    note "signtool not found: $(basename "$target") left unsigned"; return 0
  fi
  local pfx="$WORK/signing.pfx"
  [ -f "$pfx" ] || printf '%s' "$WINDOWS_CERT_PFX_BASE64" | base64 -d > "$pfx"
  note "signing $(basename "$target")"
  MSYS2_ARG_CONV_EXCL='*' "$signtool" sign /f "$(win_path "$pfx")" /p "${WINDOWS_CERT_PASSWORD:-}" /fd SHA256 \
    /tr "${WINDOWS_TIMESTAMP_URL:-http://timestamp.digicert.com}" /td SHA256 /d Meridian "$(win_path "$target")"
}

package_windows() {
  local rid="$1" arch="${1#win-}"
  local dir="$WORK/$rid/Meridian"
  publish_desktop "$rid" "$dir"
  cp "$ROOT/build/windows/README.txt" "$dir/README.txt"
  windows_sign "$dir/Meridian.exe"

  if use_velopack && velopack_pack "$rid" "[win]" "$dir" Meridian.exe --icon "$ROOT/build/assets/meridian.ico" --shortcuts StartMenuRoot,Desktop; then
    # Per-user setup (no admin rights; Start menu and desktop shortcuts) and a portable zip, both self-updating.
    # Unsigned unless signed separately: Windows SmartScreen warns on first run (docs/RELEASING.md).
    cp "$VPK_OUT/$rid/Meridian-$rid-Setup.exe" "$OUT/Meridian-$VERSION-windows-$arch-setup.exe"
    local portable="$WORK/$rid/portable"
    rm -rf "$portable" && mkdir -p "$portable/Meridian"
    (cd "$portable/Meridian" && unzip -q "$VPK_OUT/$rid/Meridian-$rid-Portable.zip")
    make_zip "$portable" Meridian "$OUT/Meridian-$VERSION-windows-$arch.zip"
    return 0
  fi
  make_zip "$WORK/$rid" Meridian "$OUT/Meridian-$VERSION-windows-$arch.zip"

  local iscc
  if [ "$arch" != "x64" ]; then note "installer is x64 only"
  elif iscc="$(find_tool "${ISCC:-}" iscc ISCC.exe "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "C:/Program Files (x86)/Inno Setup 6/ISCC.exe")"; then
    local name="Meridian-$VERSION-windows-$arch-setup"
    MSYS2_ARG_CONV_EXCL='*' "$iscc" /Q "/DAppVersion=$VERSION" "/DNumericVersion=$NUMERIC_VERSION" "/DSourceDir=$(win_path "$dir")" \
      "/DOutputDir=$(win_path "$OUT")" "/DOutputName=$name" "$(win_path "$ROOT/build/windows/meridian.iss")"
    windows_sign "$OUT/$name.exe"
  else
    note "Inno Setup (ISCC) not found: no installer (the Windows release job builds it)"
  fi
}

# ---------------------------------------------------------------- Linux

linux_desktop_file() { # <exec> <icon> <dest>
  sed -e "s|__EXEC__|$1|g" -e "s|__ICON__|$2|g" "$ROOT/build/linux/meridian.desktop" > "$3"
}

package_linux() {
  local rid="$1" arch="${1#linux-}"
  local files="$WORK/$rid/files" dir="$WORK/$rid/meridian-$VERSION"
  publish_desktop "$rid" "$files"

  # tar.gz: unpack anywhere, ./install.sh registers the menu entry and file types for this user.
  rm -rf "$dir" && mkdir -p "$dir"
  cp -R "$files/." "$dir/"
  cp "$ROOT/build/linux/meridian.desktop" "$ROOT/build/linux/install.sh" "$ROOT/build/linux/README.txt" "$ROOT/build/linux/meridian-mime.xml" "$dir/"
  chmod +x "$dir/Meridian" "$dir/Meridian.FmuHost" "$dir/install.sh"
  (cd "$WORK/$rid" && tar -czf "$OUT/Meridian-$VERSION-linux-$arch.tar.gz" "meridian-$VERSION")

  # AppImage: one file; the AppDir carries the .desktop MimeType and the mime XML for appimaged / AppImageLauncher.
  local appdir="$WORK/$rid/Meridian.AppDir" aiarch
  case "$arch" in x64) aiarch=x86_64 ;; arm64) aiarch=aarch64 ;; *) aiarch="$arch" ;; esac
  rm -rf "$appdir"
  mkdir -p "$appdir/usr/lib/meridian" "$appdir/usr/share/applications" "$appdir/usr/share/mime/packages" "$appdir/usr/share/icons/hicolor/256x256/apps"
  cp -R "$files/." "$appdir/usr/lib/meridian/"
  cp "$ROOT/build/linux/AppRun" "$appdir/AppRun" && chmod +x "$appdir/AppRun" "$appdir/usr/lib/meridian/Meridian" "$appdir/usr/lib/meridian/Meridian.FmuHost"
  linux_desktop_file Meridian meridian "$appdir/meridian.desktop"
  cp "$appdir/meridian.desktop" "$appdir/usr/share/applications/meridian.desktop"
  cp "$ROOT/build/assets/meridian.png" "$appdir/meridian.png"
  cp "$ROOT/build/assets/meridian.png" "$appdir/usr/share/icons/hicolor/256x256/apps/meridian.png"
  ln -sf meridian.png "$appdir/.DirIcon"
  cp "$ROOT/build/linux/meridian-mime.xml" "$appdir/usr/share/mime/packages/meridian.xml"
  local appimagetool
  if use_velopack && command -v mksquashfs >/dev/null 2>&1 \
     && velopack_pack "$rid" "[linux]" "$files" Meridian --icon "$ROOT/build/assets/meridian.png" --categories Development; then
    # Velopack's AppImage updates itself (it registers no file types; the .deb and tar.gz do).
    cp "$VPK_OUT/$rid/Meridian.AppImage" "$OUT/Meridian-$VERSION-linux-$arch.AppImage" 2>/dev/null \
      || cp "$VPK_OUT/$rid"/*.AppImage "$OUT/Meridian-$VERSION-linux-$arch.AppImage"
  elif appimagetool="$(find_tool "${APPIMAGETOOL:-}" appimagetool appimagetool-x86_64.AppImage)"; then
    ARCH="$aiarch" APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" --no-appstream "$appdir" "$OUT/Meridian-$VERSION-linux-$arch.AppImage"
  else
    note "appimagetool not found: no AppImage (AppDir staged at $appdir)"
  fi

  # .deb: /opt/meridian, a /usr/bin/meridian link, menu entry, icon and file types (dpkg triggers refresh the caches).
  local deb="$WORK/$rid/deb" debarch
  case "$arch" in x64) debarch=amd64 ;; arm64) debarch=arm64 ;; *) debarch="$arch" ;; esac
  rm -rf "$deb"
  mkdir -p "$deb/DEBIAN" "$deb/opt/meridian" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/mime/packages" "$deb/usr/share/icons/hicolor/256x256/apps"
  cp -R "$files/." "$deb/opt/meridian/"
  ln -sf /opt/meridian/Meridian "$deb/usr/bin/meridian"
  linux_desktop_file /opt/meridian/Meridian meridian "$deb/usr/share/applications/meridian.desktop"
  cp "$ROOT/build/linux/meridian-mime.xml" "$deb/usr/share/mime/packages/meridian.xml"
  cp "$ROOT/build/assets/meridian.png" "$deb/usr/share/icons/hicolor/256x256/apps/meridian.png"
  cat > "$deb/DEBIAN/control" <<EOF
Package: meridian
Version: ${VERSION//-/\~}
Architecture: $debarch
Maintainer: Meridian <meridian@users.noreply.github.com>
Installed-Size: $(du -sk "$deb" | cut -f1)
Depends: libwebkit2gtk-4.1-0, libgtk-3-0, libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66
Section: devel
Priority: optional
Homepage: https://github.com/finepointcgi/meridian
Description: Model-based systems engineering
 SysML, requirements, BPMN, ArchiMate and free drawing, offline on this
 computer or connected to a Meridian team server.
EOF
  if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --root-owner-group -Zxz --build "$deb" "$OUT/Meridian-$VERSION-linux-$arch.deb" >/dev/null
  else
    note "dpkg-deb not found: no .deb (package tree staged at $deb)"
  fi
}

# ---------------------------------------------------------------- main

build_guide

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  case "$rid" in
    osx-*) package_macos "$rid" ;;
    win-*) package_windows "$rid" ;;
    linux-*) package_linux "$rid" ;;
    *) echo "unknown rid $rid" >&2; exit 1 ;;
  esac
done

if [ "${SKIP_SERVER:-}" != "1" ]; then
  echo "==> server"
  srv="$WORK/server/meridian-server-$VERSION"
  rm -rf "$srv"
  dotnet publish "$ROOT/src/Meridian.Web" -c Release -p:Version="$VERSION" -o "$srv" --nologo -v q
  rm -f "$srv"/*.pdb "$srv"/appsettings.Development*.json
  add_guide "$srv"
  cp "$ROOT/build/server/README.txt" "$srv/README.txt"
  make_zip "$WORK/server" "meridian-server-$VERSION" "$OUT/Meridian-Server-$VERSION.zip"
fi

[ "${KEEP_WORK:-}" = "1" ] || rm -rf "$WORK"

if [ "${NO_CHECKSUMS:-}" != "1" ]; then
  echo "==> SHA256SUMS.txt"
  (
    cd "$OUT"
    files=()
    for f in *; do [ -f "$f" ] && [ "$f" != SHA256SUMS.txt ] && files+=("$f"); done
    rm -f SHA256SUMS.txt
    if [ ${#files[@]} -gt 0 ]; then
      if command -v sha256sum >/dev/null 2>&1; then sha256sum "${files[@]}"; else shasum -a 256 "${files[@]}"; fi > SHA256SUMS.txt
    fi
  )
fi

echo "==> done"
ls -lh "$OUT"
