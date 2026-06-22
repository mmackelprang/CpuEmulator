#!/usr/bin/env sh
# Fetches apl2cpm3 / CPM3.1_Z80_Softcard Disk 1 (CP/M 3.1 for the Microsoft Z-80 SoftCard) into the vector
# cache (same root as the Apple/Spectrum/CP-M-2.2/Klaus assets; NEVER vendored). Fetched on demand at test
# time, NOT committed (ADR 0018 Decision 5; owner sign-off COVERED by the existing CP/M-disk sign-off --
# same fetch-on-demand posture as get-softcard-cpm). Provide your own URL/mirror if the default moves.
#
# Layout written (consumed by CpuEmulator.Machines.Apl2Cpm3 -- a DISTINCT subdir from the 2.2 disk so the
# working cpm/softcard-cpm.dsk is never clobbered):
#   <cache>/cpm/apl2cpm3/CPM3.1_Disk_1.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the boot disk)
# Disks 2-7 are OPTIONAL (data/tool/help; no boot sector) -- Disk 1 boots standalone (ADR 0018 Decision 5).
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
APL_DIR="$DEST/cpm/apl2cpm3"
mkdir -p "$APL_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$APL_DIR/$name"
    if [ -f "$out" ]; then echo "$name already present at $out"; return 0; fi
    for url in "$@"; do
        if curl -fsSL "$url" -o "$out" 2>/dev/null; then
            len=$(wc -c < "$out")
            if [ "$len" -eq "$want_len" ]; then
                echo "$name fetched to $out ($len bytes) from $url"; return 0
            fi
            rm -f "$out"; echo "WARN: $url failed sanity (len=$len, want $want_len) -- trying next" >&2
        else
            rm -f "$out"; echo "WARN: fetch of $url failed -- trying next" >&2
        fi
    done
    if [ "$required" -eq 1 ]; then
        echo "ERROR: could not fetch the required $name from any source" >&2; return 1
    fi
    echo "NOTE: optional $name not fetched" >&2; return 0
}

# The owner confirms the real source URL at PR time (sign-off COVERED, ADR 0018 Decision 5/6: the
# cpm.z80.de/download/apl2cpm3.zip package, or the Asimov CPM3.1_Z80_Softcard.zip). The placeholder below is
# guarded so an unconfigured run says so plainly. The fetch must extract CPM3.1_Disk_1.dsk from the .zip --
# the owner points DISK1_URL at a direct .dsk mirror, OR adapts the unzip step below.
DISK1_URL="https://mirror.example/cpm/apl2cpm3/CPM3.1_Disk_1.dsk"
if [ ! -f "$APL_DIR/CPM3.1_Disk_1.dsk" ] && [ "${DISK1_URL#*mirror.example}" != "$DISK1_URL" ]; then
    echo "ERROR: the apl2cpm3 Disk-1 URL has not been configured -- edit tools/get-apl2cpm3.sh and set the" >&2
    echo "       real source (cpm.z80.de/download/apl2cpm3.zip or the Asimov CPM3.1_Z80_Softcard.zip;" >&2
    echo "       extract CPM3.1_Disk_1.dsk), then re-run." >&2
    exit 1
fi

fetch_one "CPM3.1_Disk_1.dsk" 143360 1 "$DISK1_URL"

echo "apl2cpm3 fetch complete (cache: $APL_DIR). Disk 1 is the only required image; Disks 2-7 are optional."
