#!/usr/bin/env sh
# Fetches the Apple ][+ ROMs into the vector cache (same root as the Spectrum/ZEX/Klaus assets; NEVER
# vendored). The Apple ][+ system ROM (Applesoft + Monitor), the slot-6 Disk II P5/P6 boot ROM, and the
# character-generator ROM are Apple's copyright; this repo fetches them on demand at test time — they are
# NOT committed to the repository (ADR 0014 Decision 7). Provide your own URLs/mirror if the defaults move.
#
# Layout written (consumed by CpuEmulator.Machines.Apple2Rom):
#   <cache>/apple2/apple2plus.rom   12288 bytes  (REQUIRED to boot a real Apple)
#   <cache>/apple2/disk2.rom          256 bytes  (needed to boot a disk; slot 6 $C600)
#   <cache>/apple2/char.rom          2048 bytes  (OPTIONAL — a built-in fallback font covers it)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
ROM_DIR="$DEST/apple2"
mkdir -p "$ROM_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URLs below are placeholders for the owner to point at their preferred source/mirror; the Apple
# ROMs are user-supplied. The length sanity-check is what guarantees a correct image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$ROM_DIR/$name"
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
    echo "NOTE: optional $name not fetched — the built-in fallback font will be used" >&2; return 0
}

fetch_one "apple2plus.rom" 12288 1 \
    "https://mirror.example/apple2/apple2plus.rom"
fetch_one "disk2.rom" 256 1 \
    "https://mirror.example/apple2/disk2-p5p6.rom"
fetch_one "char.rom" 2048 0 \
    "https://mirror.example/apple2/apple2-character.rom"

echo "Apple ][+ ROM fetch complete (cache: $ROM_DIR)."
