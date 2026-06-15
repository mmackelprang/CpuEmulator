#!/usr/bin/env sh
# Fetches Frank D. Cringle's Z80 exercisers (zexdoc.com + zexall.com) into the vector cache.
# Provenance: GPL-2.0, Frank D. Cringle (1994). Primary: agn453/ZEXALL; mirror: begoon/z80exer.
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
ZEX_DIR="$DEST/zex"
mkdir -p "$ZEX_DIR"

fetch() {
    name="$1"; primary="$2"; mirror="$3"
    out="$ZEX_DIR/$name"
    if [ -f "$out" ]; then echo "$name already present at $out"; return 0; fi
    for url in "$primary" "$mirror"; do
        if curl -fsSL "$url" -o "$out"; then
            len=$(wc -c < "$out")
            first=$(head -c 1 "$out" | od -An -tu1 | tr -d ' ')
            if [ "$len" -gt 0 ] && [ "$len" -lt 16384 ] && [ "$first" != "60" ]; then
                echo "$name fetched to $out ($len bytes) from $url"; return 0
            fi
            rm -f "$out"; echo "WARN: $url failed sanity (len=$len) — trying mirror" >&2
        else
            rm -f "$out"; echo "WARN: fetch of $url failed — trying mirror" >&2
        fi
    done
    echo "ERROR: could not fetch $name from any source" >&2; return 1
}

fetch zexdoc.com \
    "https://raw.githubusercontent.com/agn453/ZEXALL/main/zexdoc.com" \
    "https://raw.githubusercontent.com/begoon/z80exer/master/zexdoc.com"
fetch zexall.com \
    "https://raw.githubusercontent.com/agn453/ZEXALL/main/zexall.com" \
    "https://raw.githubusercontent.com/begoon/z80exer/master/zexall.com"
