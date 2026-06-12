#!/usr/bin/env bash
# Fetches the Klaus Dörmann 6502 functional test binary (pre-assembled default build)
# into the vector cache (same root as the TomHarte vectors; never vendored, spec §8).
set -eu

DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
KLAUSDIR="$DEST/klaus"
BIN="$KLAUSDIR/6502_functional_test.bin"

if [ -f "$BIN" ]; then
    echo "Klaus binary already present at $BIN"
    exit 0
fi

mkdir -p "$KLAUSDIR"
URL="https://raw.githubusercontent.com/Klaus2m5/6502_65C02_functional_tests/master/bin_files/6502_functional_test.bin"
curl -fsSL "$URL" -o "$BIN"

SIZE=$(wc -c < "$BIN")
if [ "$SIZE" -ne 65536 ]; then
    rm -f "$BIN"
    echo "ERROR: downloaded image is not 64 KiB (got $SIZE bytes) — refusing to cache it" >&2
    exit 1
fi

echo "Klaus binary fetched to $BIN"
