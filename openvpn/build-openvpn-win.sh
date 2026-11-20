#!/usr/bin/env bash
# Cross-compile a patched OpenVPN 2.7.8 for Windows x64 (mingw-w64), statically
# linked against OpenSSL 3.5.9, with the OpenP2S long-credentials patch:
#   USER_PASS_LEN 128 -> 8192, TLS_CHANNEL_BUF_SIZE 2048 -> 16384
# so an Entra access token fits as the OpenVPN password.
#
# Usage: build-openvpn-win.sh <build-dir> <long-credentials.patch>
# Expects <build-dir>/dl/{openvpn-2.7.8,openssl-3.5.9}.tar.gz and dl/tap-windows.h,
# already checksum-verified by build/build.sh.
set -euo pipefail

B=$(realpath "$1"); PATCH=$(realpath "$2")
HOST=x86_64-w64-mingw32
JOBS=$(nproc)
SRC=$B/src; PREFIX=$B/prefix; OUT=$B/out
mkdir -p "$SRC" "$PREFIX" "$OUT"

log() { printf '\n==== %s ====\n' "$*"; }

# ---- OpenSSL (static) --------------------------------------------------------
if [[ ! -f $PREFIX/lib/libcrypto.a ]]; then
    log "OpenSSL 3.5.9 for $HOST"
    rm -rf "$SRC/openssl-3.5.9"
    tar -xzf "$B/dl/openssl-3.5.9.tar.gz" -C "$SRC"
    cd "$SRC/openssl-3.5.9"
    ./Configure mingw64 no-shared no-tests no-docs no-apps no-legacy \
        --cross-compile-prefix=$HOST- \
        --prefix="$PREFIX" --libdir=lib
    make -j"$JOBS" build_libs >/dev/null
    make install_dev >/dev/null
fi
ls -la "$PREFIX/lib/libssl.a" "$PREFIX/lib/libcrypto.a"

# OpenVPN's Windows build needs the TAP driver's public header.
install -m 644 "$B/dl/tap-windows.h" "$PREFIX/include/tap-windows.h"

# ---- OpenVPN (patched) -------------------------------------------------------
log "OpenVPN 2.7.8 + long-credentials for $HOST"
rm -rf "$SRC/openvpn-2.7.8"
tar -xzf "$B/dl/openvpn-2.7.8.tar.gz" -C "$SRC"
cd "$SRC/openvpn-2.7.8"
patch -p1 < "$PATCH"
grep -n 'define USER_PASS_LEN 8192' src/openvpn/misc.h
grep -n 'define TLS_CHANNEL_BUF_SIZE 16384' src/openvpn/common.h

WINLIBS="-lws2_32 -lcrypt32 -lbcrypt -luser32 -ladvapi32 -lgdi32"
./configure --host=$HOST --prefix="$OUT" \
    --with-crypto-library=openssl \
    OPENSSL_CFLAGS="-I$PREFIX/include" \
    OPENSSL_LIBS="-L$PREFIX/lib -lssl -lcrypto $WINLIBS" \
    --disable-lzo --disable-lz4 --disable-pkcs11 --disable-plugins \
    --disable-unit-tests --enable-management \
    CPPFLAGS="-I$PREFIX/include" \
    CFLAGS="-O2" LDFLAGS="-static -static-libgcc" >/dev/null
# Only openvpn.exe is needed: the official installer supplies the drivers,
# tapctl.exe and the interactive service.
make -j"$JOBS" -C src/compat >/dev/null
make -j"$JOBS" -C src/openvpn openvpn.exe >/dev/null
mkdir -p "$OUT/bin"
cp src/openvpn/openvpn.exe "$OUT/bin/openvpn.exe"
$HOST-strip "$OUT/bin/openvpn.exe"

log "result"
EXE=$OUT/bin/openvpn.exe
file "$EXE"
$HOST-objdump -p "$EXE" | awk '/DLL Name/{print "  imports " $3}'
sha256sum "$EXE"
