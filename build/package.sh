#!/usr/bin/env bash
# Builds Meridian release artifacts into ./artifacts:
#   Meridian-<ver>-macos-arm64.zip / -macos-x64.zip   (Meridian.app, ad-hoc signed)
#   Meridian-<ver>-windows-x64.zip                     (Meridian.exe + files)
#   Meridian-<ver>-linux-x64.tar.gz                    (meridian launcher + .desktop entry)
#   Meridian-Server-<ver>.zip                          (web server, needs .NET 10 runtime; Docker image via Dockerfile)
#
# Usage: build/package.sh [version] [rid ...]     e.g. build/package.sh 0.1.0 osx-arm64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-0.1.0}"
shift || true
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(osx-arm64 osx-x64 win-x64 linux-x64)
OUT="$ROOT/artifacts"
WORK="$ROOT/artifacts/work"
mkdir -p "$OUT" "$WORK"

publish_desktop() {
  local rid="$1" dir="$2"
  rm -rf "$dir"
  # The web app first (its static assets and endpoint manifest), then the desktop host on top: one self-contained folder.
  dotnet publish "$ROOT/src/Meridian.Web" -c Release -r "$rid" --self-contained -p:Version="$VERSION" -o "$dir" --nologo -v q
  dotnet publish "$ROOT/src/Meridian.Desktop" -c Release -r "$rid" --self-contained -p:Version="$VERSION" -o "$dir" --nologo -v q
  # The server executable is not needed inside the desktop app (the desktop host runs it in-process).
  rm -f "$dir/Meridian.Web" "$dir/Meridian.Web.exe" "$dir"/*.pdb
}

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  case "$rid" in
    osx-*)
      arch="${rid#osx-}"
      stage="$WORK/$rid"
      app="$stage/Meridian.app"
      publish_desktop "$rid" "$WORK/$rid-files"
      rm -rf "$stage" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      cp -R "$WORK/$rid-files/." "$app/Contents/MacOS/"
      cp "$ROOT/build/assets/meridian.icns" "$app/Contents/Resources/Meridian.icns"
      sed -e "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$app/Contents/Info.plist"
      chmod +x "$app/Contents/MacOS/Meridian"
      if command -v codesign >/dev/null; then
        codesign --force --deep --sign - --entitlements "$ROOT/build/macos/entitlements.plist" --options runtime "$app"
      fi
      (cd "$stage" && rm -f "$OUT/Meridian-$VERSION-macos-$arch.zip" && \
        if command -v ditto >/dev/null; then ditto -c -k --keepParent Meridian.app "$OUT/Meridian-$VERSION-macos-$arch.zip"; else zip -qry "$OUT/Meridian-$VERSION-macos-$arch.zip" Meridian.app; fi)
      ;;
    win-*)
      arch="${rid#win-}"
      dir="$WORK/$rid/Meridian"
      publish_desktop "$rid" "$dir"
      cp "$ROOT/build/windows/README.txt" "$dir/README.txt"
      (cd "$WORK/$rid" && rm -f "$OUT/Meridian-$VERSION-windows-$arch.zip" && zip -qr "$OUT/Meridian-$VERSION-windows-$arch.zip" Meridian)
      ;;
    linux-*)
      arch="${rid#linux-}"
      dir="$WORK/$rid/meridian-$VERSION"
      publish_desktop "$rid" "$dir"
      cp "$ROOT/build/linux/meridian.desktop" "$ROOT/build/linux/install.sh" "$ROOT/build/linux/README.txt" "$dir/"
      chmod +x "$dir/Meridian" "$dir/install.sh"
      (cd "$WORK/$rid" && tar -czf "$OUT/Meridian-$VERSION-linux-$arch.tar.gz" "meridian-$VERSION")
      ;;
    *) echo "unknown rid $rid" >&2; exit 1 ;;
  esac
done

echo "==> server"
srv="$WORK/server/meridian-server-$VERSION"
rm -rf "$srv"
dotnet publish "$ROOT/src/Meridian.Web" -c Release -p:Version="$VERSION" -o "$srv" --nologo -v q
rm -f "$srv"/*.pdb "$srv"/appsettings.Development.json
cp "$ROOT/build/server/README.txt" "$srv/README.txt"
(cd "$WORK/server" && rm -f "$OUT/Meridian-Server-$VERSION.zip" && zip -qr "$OUT/Meridian-Server-$VERSION.zip" "meridian-server-$VERSION")

rm -rf "$WORK"
echo "==> done"
ls -lh "$OUT"
