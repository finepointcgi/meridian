#!/usr/bin/env bash
# Installs the Chromium build that this project's Microsoft.Playwright version expects, using the Playwright driver
# (Node) bundled with the NuGet package, so no PowerShell or global tools are needed.
#   tests/Meridian.E2E/install-browsers.sh            # macOS / Linux / WSL
#   tests/Meridian.E2E/install-browsers.sh --with-deps  # Linux CI: also apt-installs the system libraries Chromium needs
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
config="${CONFIGURATION:-Debug}"
dotnet build "$here/Meridian.E2E.csproj" -c "$config" --nologo -v quiet
out="$here/bin/$config/net10.0"
node="$(ls -d "$out"/.playwright/node/*/node 2>/dev/null | head -n 1 || true)"
if [[ -z "$node" ]]; then node="$(command -v node || true)"; fi
if [[ -z "$node" ]]; then echo "No Node runtime found (neither bundled nor on PATH)." >&2; exit 1; fi
"$node" "$out/.playwright/package/cli.js" install "$@" chromium
