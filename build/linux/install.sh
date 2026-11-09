#!/usr/bin/env sh
# Adds Meridian to your application menu (current user only).
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.local/share/applications"
sed "s|__DIR__|$DIR|g" "$DIR/meridian.desktop" > "$HOME/.local/share/applications/meridian.desktop"
echo "Meridian added to your applications menu. You can also run $DIR/Meridian directly."
