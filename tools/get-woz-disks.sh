#!/usr/bin/env bash
# Fetch a small, freely-redistributable WOZ2 disk image into the asset cache (NEVER vendored).
#
# W-8 (asset provenance): no concrete public-domain / freely-redistributable single-.woz URL could be
# confirmed at implementation time. The WOZ *format spec* is public domain (applesaucefdc.com/woz), but
# the AppleSauce sample set is distributed only as a ZIP (woz_images.zip) with no stated licensing for the
# disk contents, and most circulating .woz images are copyrighted commercial game disks. Rather than pin a
# guessed URL (a 404 or a copyright-tainted asset is worse than the fallback), this script requires the
# owner to supply a confirmed public-domain .woz via WOZ_DISK_URL (or to drop a local file at the dest).
# The asset-free parser gates (WozFluxImageTests) carry the row; the [WozDiskFact] headline gate
# skips-with-note until an asset is present. When a vetted public-domain URL is found, pin it here.
set -euo pipefail
ROOT="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
DEST="$ROOT/woz"
mkdir -p "$DEST"
URL="${WOZ_DISK_URL:?set WOZ_DISK_URL to a public-domain .woz, or copy a local file to $DEST/demo.woz}"
echo "Fetching $URL -> $DEST/demo.woz"
curl -fsSL "$URL" -o "$DEST/demo.woz"
# Sanity: WOZ2 magic.
head -c4 "$DEST/demo.woz" | grep -q "WOZ2" || { echo "not a WOZ2 file"; exit 1; }
echo "ok: $DEST/demo.woz"
