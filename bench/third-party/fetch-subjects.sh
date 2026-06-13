#!/usr/bin/env bash
# Fetch the third-party 6502 emulator sources/runtimes into the bench cache dir.
# The third-party emulators themselves are NOT vendored (license + size + the same
# principle as the TomHarte vectors) — this script populates a cache dir that the
# adapters probe for. Re-runnable + idempotent. Each subject is independent: a
# failure to fetch one (no network, no toolchain) leaves the others usable, and
# the corresponding adapter skips-with-note.
#
# Cache layout (default ~/.cache/cpuemulator/bench, override with CPUEMULATOR_BENCHCACHE):
#   fake6502/fake6502.c          (Mike Chambers' single-file C 6502)
#   py65venv/                    (a Python venv with py65 installed)
#   node_modules/@sfotty-pie/    (the sfotty npm package)
set -u
CACHE="${CPUEMULATOR_BENCHCACHE:-$HOME/.cache/cpuemulator/bench}"
mkdir -p "$CACHE"
echo "bench cache: $CACHE"

# ── fake6502 (C) — the omarandlorraine fork (context-struct API: needs both .c + .h) ──────────
mkdir -p "$CACHE/fake6502"
fetch_fake() {
  local name="$1"
  if [ ! -f "$CACHE/fake6502/$name" ]; then
    echo "fetching $name ..."
    curl -fsSL "https://raw.githubusercontent.com/omarandlorraine/fake6502/master/$name" \
      -o "$CACHE/fake6502/$name" \
      && echo "  -> $CACHE/fake6502/$name" \
      || echo "  !! $name fetch failed (no network?) — the C adapter will skip-with-note"
  else
    echo "$name already present"
  fi
}
fetch_fake fake6502.c
fetch_fake fake6502.h

# ── py65 (Python) ───────────────────────────────────────────────────────────────
if command -v python >/dev/null 2>&1; then
  if [ ! -d "$CACHE/py65venv" ]; then
    echo "creating py65 venv + installing py65 ..."
    python -m venv "$CACHE/py65venv" \
      && "$CACHE/py65venv/Scripts/python.exe" -m pip install --quiet py65 2>/dev/null \
      || "$CACHE/py65venv/bin/python" -m pip install --quiet py65 \
      && echo "  -> py65 installed" \
      || echo "  !! py65 install failed — the Python adapter will skip-with-note"
  else
    echo "py65 venv already present"
  fi
else
  echo "python not found — the Python adapter will skip-with-note"
fi

# ── sfotty (JS / Node) ───────────────────────────────────────────────────────────
if command -v npm >/dev/null 2>&1; then
  if [ ! -d "$CACHE/node_modules/@sfotty-pie/sfotty" ]; then
    echo "installing @sfotty-pie/sfotty ..."
    ( cd "$CACHE" && npm install --no-save @sfotty-pie/sfotty >/dev/null 2>&1 ) \
      && echo "  -> sfotty installed" \
      || echo "  !! sfotty install failed — the JS adapter will skip-with-note"
  else
    echo "sfotty already present"
  fi
else
  echo "npm not found — the JS adapter will skip-with-note"
fi

# ── Asm6502 (C#) is restored via NuGet by the bench build (a PackageReference), so
#    it needs no fetch here; it populates whenever nuget.org is reachable at build. ──
echo "done. (Asm6502 C# restores via NuGet at build time — no fetch needed.)"
