#!/usr/bin/env sh
# Adds Meridian to your application menu and lets it open .meridian and .sysml files (current user only).
# ./install.sh --uninstall removes those entries again (the folder itself is left alone).
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"

if [ "${1:-}" = "--uninstall" ]; then
  rm -f "$DATA/applications/meridian.desktop" "$DATA/mime/packages/meridian.xml" "$DATA/icons/hicolor/256x256/apps/meridian.png"
else
  mkdir -p "$DATA/applications" "$DATA/mime/packages" "$DATA/icons/hicolor/256x256/apps"
  sed -e "s|__EXEC__|\"$DIR/Meridian\"|g" -e "s|__ICON__|meridian|g" "$DIR/meridian.desktop" > "$DATA/applications/meridian.desktop"
  cp "$DIR/meridian-mime.xml" "$DATA/mime/packages/meridian.xml"
  cp "$DIR/meridian.png" "$DATA/icons/hicolor/256x256/apps/meridian.png"
fi

# Refresh the desktop's caches when the tools exist (they do on GNOME, KDE and most other desktops).
command -v update-mime-database >/dev/null 2>&1 && update-mime-database "$DATA/mime" || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DATA/applications" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA/icons/hicolor" || true
if [ "${1:-}" != "--uninstall" ] && command -v xdg-mime >/dev/null 2>&1; then
  xdg-mime default meridian.desktop application/x-meridian || true
fi

if [ "${1:-}" = "--uninstall" ]; then echo "Meridian removed from your applications menu."
else echo "Meridian added to your applications menu; .meridian files now open with it. You can also run $DIR/Meridian directly."; fi
