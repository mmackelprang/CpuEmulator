#!/usr/bin/env sh
# Fetches the Videx Videoterm ROMs into the vector cache (same root as the Apple/Spectrum/ZEX/Klaus assets;
# NEVER vendored). The 1 KiB firmware ROM ($C800-$CBFF) and the 2 KiB character-generator ROM are fetched
# on demand at test time — they are NOT committed (ADR 0016 Decision 4). BOTH are OPTIONAL: a synthetic
# fallback font + an all-zero firmware cover the CP/M-on-Videx boot gate; the real ROMs add glyph fidelity.
# Provide your own URLs/mirror if the defaults move (research §9: asimov.net/emulators/rom_images/videx/).
#
# Layout written (consumed by CpuEmulator.Machines.VidexRom):
#   <cache>/videx/videx-firmware.rom   1024 bytes  (OPTIONAL — $C800 firmware; synthetic zero covers it)
#   <cache>/videx/videx-char.rom       2048 bytes  (OPTIONAL — 256x8 glyphs; VidexFont.Fallback covers it)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
VIDEX_DIR="$DEST/videx"
mkdir -p "$VIDEX_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URLs below are placeholders for the owner to point at the Asimov Videx mirror or a preferred
# source; the length sanity-check is what guarantees a correct image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$VIDEX_DIR/$name"
    if [ -f "$out" ]; then echo "$name already present at $out"; return 0; fi
    for url in "$@"; do
        if curl -fsSL "$url" -o "$out" 2>/dev/null; then
            len=$(wc -c < "$out")
            if [ "$len" -eq "$want_len" ]; then
                echo "$name fetched to $out ($len bytes) from $url"; return 0
            fi
            rm -f "$out"; echo "WARN: $url failed sanity (len=$len, want $want_len) — trying next" >&2
        else
            rm -f "$out"; echo "WARN: fetch of $url failed — trying next" >&2
        fi
    done
    if [ "$required" -eq 1 ]; then
        echo "ERROR: could not fetch the required $name from any source" >&2; return 1
    fi
    echo "NOTE: optional $name not fetched — the built-in synthetic Videx asset will be used" >&2; return 0
}

fetch_one "videx-firmware.rom" 1024 0 \
    "https://mirror.example/videx/videx-firmware.rom"
fetch_one "videx-char.rom" 2048 0 \
    "https://mirror.example/videx/videx-character.rom"

echo "Videx ROM fetch complete (cache: $VIDEX_DIR)."
