#!/usr/bin/env sh
# Stages the owner-supplied Apple II Pascal (UCSD p-System) distribution disk images into the vector cache
# (same root as the Apple/Spectrum/CP-M/Klaus assets; NEVER vendored -- Apple's copyright). These `.dsk`
# images are OWNER-SUPPLIED, staged on demand from the owner's local source, NOT fetched from any mirror and
# NOT committed (the SoftCard-CP/M / WOZ owner-supplied posture).
#
# Layout written (consumed by CpuEmulator.Machines.Pascal):
#   <cache>/pascal/APPLE1.dsk   143360 bytes  (the BOOT volume: SYSTEM.APPLE interpreter + SYSTEM.PASCAL)
#   <cache>/pascal/APPLE0.dsk   143360 bytes  (the program/compiler volume: COMPILER/EDITOR/FILER)
#   <cache>/pascal/APPLE2.dsk   143360 bytes  (optional)
#   <cache>/pascal/APPLE3.dsk   143360 bytes  (optional)
# The two REQUIRED images for the boot gate are APPLE1 (drive 1) + APPLE0 (drive 2); the others are optional.
#
# Source: set PASCAL_SRC_DIR to the directory holding the owner's distribution .dsk files. Default is the
# owner's local D:/prj/ROMs (where the four "Apple Pascal N - 680-000N-0M.dsk" images live). Each `.dsk` is a
# 140K 5.25" image (35 trk x 16 sec x 256 B); the length check (143360) guards a correct image regardless of
# the exact distribution filename. The images are DOS-3.3 sector order containing a UCSD Pascal filesystem
# (see CpuEmulator.Machines.Pascal -- DskFluxImage uses SectorOrderKind.Dos33, NOT ProDOS).
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
PASCAL_DIR="$DEST/pascal"
SRC="${PASCAL_SRC_DIR:-/d/prj/ROMs}"
mkdir -p "$PASCAL_DIR"

# Each row: dest filename | required(1)/optional(0) | the owner's source filename within $SRC.
stage_one() {
    dest="$1"; required="$2"; src_name="$3"
    out="$PASCAL_DIR/$dest"
    if [ -f "$out" ]; then echo "$dest already present at $out"; return 0; fi
    src="$SRC/$src_name"
    if [ -f "$src" ]; then
        len=$(wc -c < "$src" | tr -d ' ')
        if [ "$len" -eq 143360 ]; then
            cp "$src" "$out"; echo "$dest staged from $src ($len bytes)"; return 0
        fi
        echo "WARN: $src has length $len, want 143360 -- skipping" >&2
    fi
    if [ "$required" -eq 1 ]; then
        echo "ERROR: required $dest not found at $src." >&2
        echo "       Set PASCAL_SRC_DIR to the folder holding your Apple Pascal distribution .dsk files," >&2
        echo "       or copy your '$src_name' to $out (143360 bytes)." >&2
        return 1
    fi
    echo "NOTE: optional $dest not staged (no $src)" >&2; return 0
}

stage_one "APPLE1.dsk" 1 "Apple Pascal 1 - 680-0004-01.dsk"
stage_one "APPLE0.dsk" 1 "Apple Pascal 0 - 680-0003-01.dsk"
stage_one "APPLE2.dsk" 0 "Apple Pascal 2 - 680-0005-01.dsk"
stage_one "APPLE3.dsk" 0 "Apple Pascal 3 - 680-0006-01.dsk"

echo "Apple Pascal staging complete (cache: $PASCAL_DIR). APPLE1 (boot) + APPLE0 (program) are required; "
echo "APPLE2/APPLE3 are optional."
