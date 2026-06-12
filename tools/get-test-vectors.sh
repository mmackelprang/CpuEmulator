#!/usr/bin/env bash
# Fetches the SingleStepTests 6502 v1 vectors via sparse checkout (the full repo covers
# many CPUs and is multi-GB; 6502/v1 alone is ~hundreds of MB — never vendored, spec §8).
set -euo pipefail

DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
VECTOR_DIR="$DEST/6502/v1"

if [ -d "$VECTOR_DIR" ]; then
    echo "Vectors already present at $VECTOR_DIR"
    exit 0
fi

git clone --depth 1 --filter=blob:none --sparse \
    https://github.com/SingleStepTests/ProcessorTests.git "$DEST"
git -C "$DEST" sparse-checkout set 6502/v1
echo "Vectors fetched to $VECTOR_DIR"
