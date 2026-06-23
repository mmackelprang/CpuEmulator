#!/usr/bin/env pwsh
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
# Source: set PASCAL_SRC_DIR (env) to the directory holding the owner's distribution .dsk files, or pass
# -SourceDir. Default is the owner's local D:\prj\ROMs. Each .dsk is a 140K 5.25" image (35 trk x 16 sec x
# 256 B); the length check (143360) guards a correct image regardless of the exact distribution filename. The
# images are DOS-3.3 sector order containing a UCSD Pascal filesystem (see CpuEmulator.Machines.Pascal --
# DskFluxImage uses SectorOrderKind.Dos33, NOT ProDOS).
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" }),
    [string]$SourceDir = $(if ($env:PASCAL_SRC_DIR) { $env:PASCAL_SRC_DIR } else { "D:\prj\ROMs" })
)
$ErrorActionPreference = "Stop"
$pascalDir = Join-Path $Destination "pascal"
New-Item -ItemType Directory -Force $pascalDir | Out-Null

function Stage-One($dest, $required, $srcName) {
    $out = Join-Path $pascalDir $dest
    if (Test-Path $out) { Write-Host "$dest already present at $out"; return }
    $src = Join-Path $SourceDir $srcName
    if (Test-Path $src) {
        $len = (Get-Item $src).Length
        if ($len -eq 143360) {
            Copy-Item $src $out
            Write-Host "$dest staged from $src ($len bytes)"
            return
        }
        Write-Warning "$src has length $len, want 143360 -- skipping"
    }
    if ($required) {
        Write-Error ("required $dest not found at $src. Set PASCAL_SRC_DIR (or -SourceDir) to the folder " +
                     "holding your Apple Pascal distribution .dsk files, or copy your '$srcName' to $out " +
                     "(143360 bytes).")
    } else {
        Write-Warning "optional $dest not staged (no $src)"
    }
}

Stage-One "APPLE1.dsk" $true  "Apple Pascal 1 - 680-0004-01.dsk"
Stage-One "APPLE0.dsk" $true  "Apple Pascal 0 - 680-0003-01.dsk"
Stage-One "APPLE2.dsk" $false "Apple Pascal 2 - 680-0005-01.dsk"
Stage-One "APPLE3.dsk" $false "Apple Pascal 3 - 680-0006-01.dsk"

Write-Host "Apple Pascal staging complete (cache: $pascalDir). APPLE1 (boot) + APPLE0 (program) are required; APPLE2/APPLE3 are optional."
