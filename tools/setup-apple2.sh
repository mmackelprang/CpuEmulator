#!/usr/bin/env sh
# Sets up the Apple ][+ rig in the vector cache by orchestrating the existing per-asset get-* scripts (NEVER
# vendored; same cache root as the Spectrum/CP-M/Klaus assets). This combined script adds NO fetch logic of
# its own — it chains the building blocks so the multi-asset Apple ][+ rig is one command:
#   tools/get-apple2-roms.sh   the system ROM + slot-6 Disk II boot ROM + (optional) char ROM
#   tools/get-woz-disks.sh     a sample .woz disk -- ONLY when WOZ_DISK_URL is set (owner-supplied; see W-8)
#
# The Apple ROMs are Apple's copyright + owner-supplied (placeholder URLs in get-apple2-roms; the length
# sanity-check guarantees a correct image regardless of source). The .woz step is opt-in: with no
# WOZ_DISK_URL it is skipped with a note (the get-woz-disks script has no default URL by design, W-8).
# Idempotent: each per-asset script skips work it has already done.
set -eu
DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"

echo "Apple ][+ setup -> cache root: $DEST"

echo "==> Apple ][+ ROMs (system + Disk II boot + optional char) ..."
sh "$DIR/get-apple2-roms.sh"

if [ -n "${WOZ_DISK_URL:-}" ]; then
    echo "==> sample .woz disk (WOZ_DISK_URL is set) ..."
    sh "$DIR/get-woz-disks.sh"
else
    echo "==> sample .woz disk SKIPPED — set WOZ_DISK_URL to a public-domain .woz to fetch one"
    echo "    (most circulating .woz images are copyrighted; this step is opt-in by design, W-8)."
fi

echo "Apple ][+ setup complete. Run: dotnet run --project src/CpuEmulator.Surface.Web"
