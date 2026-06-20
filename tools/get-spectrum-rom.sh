#!/usr/bin/env sh
# Fetches the ZX Spectrum 48K ROM (16 KiB) into the vector cache (same root as the ZEX/Klaus assets;
# never vendored). Provenance: the 48K Spectrum ROM is Amstrad's copyright; Amstrad granted permission
# to redistribute the Spectrum ROMs for emulation use. This repo fetches it at test time, exactly as it
# fetches the Klaus 6502 binary + the ZEX exercisers — it is NOT committed to the repository.
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
ROM_DIR="$DEST/spectrum"
OUT="$ROM_DIR/48.rom"
mkdir -p "$ROM_DIR"

if [ -f "$OUT" ]; then echo "Spectrum 48K ROM already present at $OUT"; exit 0; fi

PRIMARY="https://raw.githubusercontent.com/chrishaynes/spectrum-roms/master/48.rom"
MIRROR="https://raw.githubusercontent.com/oldcomputers-ddns/zx-spectrum-roms/main/48.rom"

for url in "$PRIMARY" "$MIRROR"; do
    if curl -fsSL "$url" -o "$OUT"; then
        len=$(wc -c < "$OUT")
        if [ "$len" -eq 16384 ]; then
            echo "Spectrum 48K ROM fetched to $OUT ($len bytes) from $url"; exit 0
        fi
        rm -f "$OUT"; echo "WARN: $url failed sanity (len=$len, want 16384) — trying mirror" >&2
    else
        rm -f "$OUT"; echo "WARN: fetch of $url failed — trying mirror" >&2
    fi
done

echo "ERROR: could not fetch the Spectrum 48K ROM from any source" >&2
exit 1
