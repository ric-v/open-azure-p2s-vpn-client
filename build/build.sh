#!/usr/bin/env bash
# Build P2SVpn.exe from source on Linux (also used by the GitHub Actions release).
#
#   1. download OpenVPN, OpenSSL, the TAP header and the OpenVPN installer,
#      verifying each against build/versions.env
#   2. cross-compile OpenSSL + patched OpenVPN for Windows (mingw-w64)
#   3. publish the .NET app as one self-contained P2SVpn.exe into dist/
#
# Needs: x86_64-w64-mingw32 gcc/binutils, make, perl, patch, curl, sha256sum,
#        and the .NET 10 SDK.
# Env:   WORK (default .build), OUT (default dist), VERSION (e.g. 1.2.0)
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mkdir -p "${WORK:-$ROOT/.build}" && cd "${WORK:-$ROOT/.build}" && pwd)
OUT=$(mkdir -p "${OUT:-$ROOT/dist}" && cd "${OUT:-$ROOT/dist}" && pwd)
# shellcheck source=versions.env
source "$ROOT/build/versions.env"
mkdir -p "$WORK/dl"

log() { printf '\n==== %s ====\n' "$*"; }

fetch() { # <url> <sha256> <file name>
    local url=$1 sum=$2 f="$WORK/dl/$3"
    if [[ -f $f ]] && echo "$sum  $f" | sha256sum -c --quiet >/dev/null 2>&1; then
        echo "cached  $3"; return
    fi
    echo "fetch   $3"
    curl -fsSL --retry 3 -o "$f.part" "$url"
    if ! echo "$sum  $f.part" | sha256sum -c --quiet; then
        echo "SHA-256 mismatch for $3 ($url)" >&2; rm -f "$f.part"; exit 1
    fi
    mv "$f.part" "$f"
}

log "inputs"
fetch "$OPENVPN_URL"     "$OPENVPN_SHA256"     "openvpn-$OPENVPN_VERSION.tar.gz"
fetch "$OPENSSL_URL"     "$OPENSSL_SHA256"     "openssl-$OPENSSL_VERSION.tar.gz"
fetch "$TAP_HEADER_URL"  "$TAP_HEADER_SHA256"  "tap-windows.h"
fetch "$OPENVPN_MSI_URL" "$OPENVPN_MSI_SHA256" "OpenVPN.msi"
echo "$PATCH_SHA256  $ROOT/openvpn/long-credentials.patch" | sha256sum -c --quiet

log "OpenSSL + patched OpenVPN for Windows"
bash "$ROOT/openvpn/build-openvpn-win.sh" "$WORK" "$ROOT/openvpn/long-credentials.patch"

log "P2SVpn.exe"
PAYLOAD="$ROOT/src/P2SVpn/payload"
mkdir -p "$PAYLOAD"
cp "$WORK/out/bin/openvpn.exe" "$PAYLOAD/openvpn.exe"
cp "$WORK/dl/OpenVPN.msi" "$PAYLOAD/OpenVPN.msi"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish "$ROOT/src/P2SVpn/P2SVpn.csproj" -c Release -o "$OUT" ${VERSION:+-p:Version=$VERSION}

log "done"
ls -la "$OUT/P2SVpn.exe"
sha256sum "$OUT/P2SVpn.exe"
