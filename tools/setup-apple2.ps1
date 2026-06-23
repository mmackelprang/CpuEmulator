#!/usr/bin/env pwsh
# Sets up the Apple ][+ rig in the vector cache by orchestrating the existing per-asset get-* scripts (NEVER
# vendored; same cache root as the Spectrum/CP-M/Klaus assets). This combined script adds NO fetch logic of
# its own -- it chains the building blocks so the multi-asset Apple ][+ rig is one command:
#   tools/get-apple2-roms.ps1   the system ROM + slot-6 Disk II boot ROM + (optional) char ROM
#   tools/get-woz-disks.ps1     a sample .woz disk -- ONLY when WOZ_DISK_URL is set (owner-supplied; see W-8)
#
# The Apple ROMs are Apple's copyright + owner-supplied (placeholder URLs in get-apple2-roms; the length
# sanity-check guarantees a correct image). The .woz step is opt-in: with no WOZ_DISK_URL it is skipped with a
# note. Idempotent: each per-asset script skips work it has already done.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Apple ][+ setup -> cache root: $Destination"

Write-Host "==> Apple ][+ ROMs (system + Disk II boot + optional char) ..."
& (Join-Path $dir "get-apple2-roms.ps1") -Destination $Destination

if ($env:WOZ_DISK_URL) {
    Write-Host "==> sample .woz disk (WOZ_DISK_URL is set) ..."
    # get-woz-disks.ps1 has no -Destination param -- it reads $CPUEMULATOR_TESTVECTORS. Forward the resolved
    # cache root via the env var so an explicit -Destination doesn't split the cache (the .woz would otherwise
    # land in the default root while the ROMs went to -Destination).
    $env:CPUEMULATOR_TESTVECTORS = $Destination
    & (Join-Path $dir "get-woz-disks.ps1")
} else {
    Write-Host "==> sample .woz disk SKIPPED — set WOZ_DISK_URL to a public-domain .woz to fetch one"
    Write-Host "    (most circulating .woz images are copyrighted; this step is opt-in by design, W-8)."
}

Write-Host "Apple ][+ setup complete. Run: dotnet run --project src/CpuEmulator.Surface.Web"
