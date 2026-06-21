#!/usr/bin/env sh
# Copies the owner's six ZX Spectrum 48K ROM *variants* (each exactly 16384 bytes) into the vector cache
# (<root>/spectrum/variants). NOT vendored — Amstrad's copyright; used with permission per the owner's
# zx-roms/spectrum16-48/info.txt. Source defaults to the owner's local mirror; override with $1.
set -eu
SRC="${1:-D:/prj/zx-roms/spectrum16-48}"
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
OUT="$DEST/spectrum/variants"
mkdir -p "$OUT"

count=0
for f in "$SRC"/spec48.rom "$SRC"/spec48-arabic-v1.rom "$SRC"/spec48-arabic-v2.rom \
         "$SRC"/spec48-arabic-v31.rom "$SRC"/spec48-beckman.rom "$SRC"/spec48-prototype.rom; do
    if [ ! -f "$f" ]; then echo "WARN: missing $f — skipping" >&2; continue; fi
    len=$(wc -c < "$f")
    if [ "$len" -ne 16384 ]; then echo "WARN: $f is $len bytes (want 16384) — skipping" >&2; continue; fi
    cp "$f" "$OUT/$(basename "$f")"
    count=$((count + 1))
done
echo "Copied $count Spectrum 48K variant ROM(s) into $OUT"
[ "$count" -gt 0 ] || { echo "ERROR: copied 0 variants from $SRC" >&2; exit 1; }
