#!/usr/bin/env bash
# Fetches the SingleStepTests Z80 v1 vectors via sparse checkout. The Z80 vectors live in a SEPARATE
# repo from the 6502's (SingleStepTests/z80, NOT ProcessorTests), with the test set at the repo TOP
# LEVEL under v1/ (verified against the live repo). Cached under <dest>/z80/v1.
set -euo pipefail

DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
VECTOR_DIR="$DEST/z80/v1"

if [ -d "$VECTOR_DIR" ]; then
    echo "Z80 vectors already present at $VECTOR_DIR"
    exit 0
fi

CLONE="$DEST/z80-clone"
rm -rf "$CLONE"
git clone --depth 1 --filter=blob:none --sparse \
    https://github.com/SingleStepTests/z80.git "$CLONE"
git -C "$CLONE" sparse-checkout set v1

mkdir -p "$DEST/z80"
mv "$CLONE/v1" "$VECTOR_DIR"
rm -rf "$CLONE"
echo "Z80 vectors fetched to $VECTOR_DIR"
