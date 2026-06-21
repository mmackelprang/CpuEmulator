#!/usr/bin/env pwsh
# Copies the owner's six ZX Spectrum 48K ROM variants (each exactly 16384 bytes) into the vector cache
# (<root>/spectrum/variants). NOT vendored — Amstrad's copyright; used with permission.
param(
    [string]$Source = "D:/prj/zx-roms/spectrum16-48",
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$out = Join-Path $Destination "spectrum/variants"
New-Item -ItemType Directory -Force $out | Out-Null

$names = @("spec48.rom","spec48-arabic-v1.rom","spec48-arabic-v2.rom",
           "spec48-arabic-v31.rom","spec48-beckman.rom","spec48-prototype.rom")
$count = 0
foreach ($n in $names) {
    $src = Join-Path $Source $n
    if (-not (Test-Path $src)) { Write-Warning "missing $src — skipping"; continue }
    $len = (Get-Item $src).Length
    if ($len -ne 16384) { Write-Warning "$src is $len bytes (want 16384) — skipping"; continue }
    Copy-Item $src (Join-Path $out $n) -Force
    $count++
}
Write-Host "Copied $count Spectrum 48K variant ROM(s) into $out"
if ($count -eq 0) { Write-Error "copied 0 variants from $Source" }
