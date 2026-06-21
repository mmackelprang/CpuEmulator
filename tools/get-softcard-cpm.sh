#!/usr/bin/env sh
# Fetches the Microsoft Z-80 SoftCard CP/M 2.2 disk image into the vector cache (same root as the
# Apple/Spectrum/ZEX/Klaus assets; NEVER vendored). The CP/M .dsk is fetched on demand at test time — it is
# NOT committed (ADR 0016 Decisions 4/5; owner sign-off GIVEN for the fetch-on-demand loader from the
# Asimov preservation mirror). Provide your own URL/mirror if the default moves.
#
# Layout written (consumed by CpuEmulator.Machines.SoftCardCpm):
#   <cache>/cpm/softcard-cpm.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the CP/M boot disk)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
CPM_DIR="$DEST/cpm"
mkdir -p "$CPM_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URL below is a placeholder for the owner to point at the Asimov mirror (apple2.org.za
# /images/cpm/os/, research §9) or a preferred source. The length sanity-check (143360) is what guarantees
# a correct CP/M image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$CPM_DIR/$name"
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
    echo "NOTE: optional $name not fetched" >&2; return 0
}

# The owner confirms the real Asimov-mirror URL at PR time (sign-off GIVEN, ADR 0016 Decision 5). Until then
# the placeholder below would fail with an opaque DNS error — guard it so an unconfigured run says so plainly.
CPM_URL="https://mirror.example/cpm/softcard-cpm.dsk"
if [ ! -f "$CPM_DIR/softcard-cpm.dsk" ] && [ "${CPM_URL#*mirror.example}" != "$CPM_URL" ]; then
    echo "ERROR: the CP/M .dsk URL has not been configured — edit tools/get-softcard-cpm.sh and set the" >&2
    echo "       real Asimov mirror URL (apple2.org.za /images/cpm/os/, research §9), then re-run." >&2
    exit 1
fi

fetch_one "softcard-cpm.dsk" 143360 1 "$CPM_URL"

echo "SoftCard CP/M fetch complete (cache: $CPM_DIR)."
