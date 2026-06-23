#!/usr/bin/env sh
# Sets up the 80-column CP/M 3.1 + Videx rig (apl2cpm3) in the vector cache by orchestrating the existing
# per-asset get-* scripts (NEVER vendored; same cache root as the Apple/Spectrum/Klaus assets). This combined
# script adds NO fetch logic of its own -- it chains the building blocks so the multi-asset 80-col rig is one
# command:
#   tools/get-apl2cpm3.sh    CP/M 3.1 Disk 1 (the bootable disk) -> cpm/apl2cpm3/CPM3.1_Disk_1.dsk
#   tools/get-videx-roms.sh  Videx firmware + char ROMs (both OPTIONAL; synthetic fallbacks cover the boot
#                            gate, but the BootProbe --apl2cpm3-videx screenshot needs the REAL firmware)
#
# The CP/M 3.1 disk is owner-supplied / fetch-on-demand (placeholder URL in get-apl2cpm3, guarded to fail
# clearly until configured; owner sign-off COVERED per ADR 0018). The Videx ROMs are owner-supplied + optional.
# Idempotent: each per-asset script skips work it has already done.
#
# NOTE: this rig ALSO needs the Apple ][+ ROMs (apple2plus.rom + the slot-6 disk2.rom). Those are NOT fetched
# here -- run tools/setup-apple2.sh (or tools/get-apple2-roms.sh) separately for the Apple ROM half.
set -eu
DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"

echo "CP/M 3.1 + Videx (80-col) setup -> cache root: $DEST"

echo "==> apl2cpm3 CP/M 3.1 Disk 1 (the bootable disk) ..."
sh "$DIR/get-apl2cpm3.sh"

echo "==> Videx Videoterm ROMs (firmware + char; optional, real firmware needed for the screenshot) ..."
sh "$DIR/get-videx-roms.sh"

echo "CP/M 3.1 + Videx setup complete."
echo "Reminder: also fetch the Apple ][+ ROMs (tools/setup-apple2.sh or tools/get-apple2-roms.sh)."
echo "Render the 80-col A> screenshot with: dotnet run --project tools/BootProbe -- --apl2cpm3-videx out.png"
