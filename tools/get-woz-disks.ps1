# Fetch a small, freely-redistributable WOZ2 disk image into the asset cache (NEVER vendored).
#
# W-8 (asset provenance): no concrete public-domain / freely-redistributable single-.woz URL could be
# confirmed at implementation time. The WOZ *format spec* is public domain (applesaucefdc.com/woz), but the
# AppleSauce sample set is distributed only as a ZIP (woz_images.zip) with no stated licensing for the disk
# contents, and most circulating .woz images are copyrighted commercial game disks. Rather than pin a
# guessed URL (a 404 or a copyright-tainted asset is worse than the fallback), this script requires the
# owner to supply a confirmed public-domain .woz via WOZ_DISK_URL (or to drop a local file at the dest). The
# asset-free parser gates (WozFluxImageTests) carry the row; the [WozDiskFact] headline gate skips-with-note
# until an asset is present. When a vetted public-domain URL is found, pin it here.
$ErrorActionPreference = "Stop"
$root = if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS } else { Join-Path $HOME ".cache/cpuemulator/vectors" }
$dest = Join-Path $root "woz"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
if (-not $env:WOZ_DISK_URL) { throw "Set WOZ_DISK_URL to a public-domain .woz, or copy a local file to $dest/demo.woz" }
$out = Join-Path $dest "demo.woz"
Write-Host "Fetching $($env:WOZ_DISK_URL) -> $out"
Invoke-WebRequest -Uri $env:WOZ_DISK_URL -OutFile $out
$magic = [System.Text.Encoding]::ASCII.GetString((Get-Content $out -AsByteStream -TotalCount 4))
if ($magic -ne "WOZ2") { throw "not a WOZ2 file" }
Write-Host "ok: $out"
