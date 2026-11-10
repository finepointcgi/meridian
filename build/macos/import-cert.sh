#!/usr/bin/env bash
# CI helper: imports a Developer ID Application certificate into a temporary keychain and exports
# MACOS_SIGN_IDENTITY / MACOS_KEYCHAIN for build/package.sh (to $GITHUB_ENV when present, else stdout).
#   APPLE_CERT_P12_BASE64   base64 of the exported .p12 (certificate + private key)
#   APPLE_CERT_PASSWORD     the .p12 export password
# Without them it prints a note and exits 0, so unsigned (ad-hoc) builds keep working.
#   build/macos/import-cert.sh            import
#   build/macos/import-cert.sh --cleanup  delete the temporary keychain
set -euo pipefail

KEYCHAIN="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/meridian-signing.keychain-db"

if [ "${1:-}" = "--cleanup" ]; then
  security delete-keychain "$KEYCHAIN" 2>/dev/null || true
  exit 0
fi

if [ -z "${APPLE_CERT_P12_BASE64:-}" ] || [ -z "${APPLE_CERT_PASSWORD:-}" ]; then
  echo "No APPLE_CERT_P12_BASE64 / APPLE_CERT_PASSWORD: macOS builds will be ad-hoc signed."
  exit 0
fi

KEYCHAIN_PASSWORD="$(openssl rand -hex 24)"
P12="$(mktemp -t meridian-cert).p12"
trap 'rm -f "$P12"' EXIT
printf '%s' "$APPLE_CERT_P12_BASE64" | base64 --decode > "$P12"

security delete-keychain "$KEYCHAIN" 2>/dev/null || true
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$P12" -k "$KEYCHAIN" -P "$APPLE_CERT_PASSWORD" -f pkcs12 -T /usr/bin/codesign -T /usr/bin/security
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
# Keep the login keychain searchable and add ours in front of it.
security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')

IDENTITY="$(security find-identity -v -p codesigning "$KEYCHAIN" | sed -n 's/.*"\(Developer ID Application:[^"]*\)".*/\1/p' | head -n 1)"
if [ -z "$IDENTITY" ]; then
  echo "The certificate in APPLE_CERT_P12_BASE64 is not a 'Developer ID Application' identity." >&2
  security find-identity -v -p codesigning "$KEYCHAIN" >&2 || true
  exit 1
fi
if [ -n "${APPLE_TEAM_ID:-}" ] && [[ "$IDENTITY" != *"($APPLE_TEAM_ID)"* ]]; then
  echo "warning: identity '$IDENTITY' does not belong to team $APPLE_TEAM_ID" >&2
fi

echo "Signing identity: $IDENTITY"
if [ -n "${GITHUB_ENV:-}" ]; then
  { echo "MACOS_SIGN_IDENTITY=$IDENTITY"; echo "MACOS_KEYCHAIN=$KEYCHAIN"; } >> "$GITHUB_ENV"
else
  echo "export MACOS_SIGN_IDENTITY=\"$IDENTITY\" MACOS_KEYCHAIN=\"$KEYCHAIN\""
fi
