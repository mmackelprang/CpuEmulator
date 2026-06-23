#!/usr/bin/env sh
# Launches the web surface booting Apple II Pascal (UCSD p-System) in the browser, deterministically. This
# combined launcher: (1) stages the owner-supplied Pascal disks (idempotent — chains get-apple-pascal.sh,
# which skips files already present), (2) confirms the Apple ][+ ROMs (apple2plus.rom + the slot-6 disk2.rom)
# are cached — both REQUIRED to boot a real disk, (3) starts the web server with --system pascal, which FORCES
# the Pascal boot branch regardless of what else is cached (the deterministic override; the auto-probe would
# also pick Pascal once the disks are staged, but --system pascal makes the choice explicit + reproducible).
#
# The Apple ROMs are NOT fetched here (they're a separate owner-supplied asset) — run tools/setup-apple2.sh
# (or tools/get-apple2-roms.sh) first if they're missing; this script tells you so and exits non-zero.
# Idempotent + CWD-independent: re-running it just re-confirms the assets and relaunches the server.
set -eu
DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
ROOT="$(CDPATH= cd -- "$DIR/.." && pwd)"
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"

echo "Apple Pascal (UCSD p-System) web launcher -> cache root: $DEST"

echo "==> staging the Apple Pascal disks (APPLE1 boot + APPLE0 program; idempotent) ..."
sh "$DIR/get-apple-pascal.sh"

echo "==> confirming the Apple ][+ ROMs are cached ..."
sys_rom="$DEST/apple2/apple2plus.rom"
disk_rom="$DEST/apple2/disk2.rom"
if [ ! -f "$sys_rom" ]; then
    echo "ERROR: the Apple ][+ system ROM is missing ($sys_rom)." >&2
    echo "       Run tools/setup-apple2.sh (or tools/get-apple2-roms.sh) to stage it, then re-run this." >&2
    exit 1
fi
if [ ! -f "$disk_rom" ]; then
    echo "ERROR: the slot-6 Disk II boot ROM is missing ($disk_rom) — REQUIRED to boot the Pascal disk." >&2
    echo "       Run tools/setup-apple2.sh (or tools/get-apple2-roms.sh) to stage it, then re-run this." >&2
    exit 1
fi
echo "    apple2plus.rom + disk2.rom present."

echo "Open http://localhost:5000 in your browser once the server prints its URL."
echo "==> launching the web server with --system pascal (forces the Pascal boot branch deterministically) ..."
exec dotnet run --project "$ROOT/src/CpuEmulator.Surface.Web" -- --system pascal
