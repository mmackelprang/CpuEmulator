#!/usr/bin/env pwsh
# Fetches the SingleStepTests 6502 v1 vectors via sparse checkout (the full repo covers
# many CPUs and is multi-GB; 6502/v1 alone is ~hundreds of MB — never vendored, spec §8).
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$vectorDir = Join-Path $Destination "6502/v1"
if (Test-Path $vectorDir) { Write-Host "Vectors already present at $vectorDir"; exit 0 }
# Note: $ErrorActionPreference does NOT trip on native-command failure — check
# $LASTEXITCODE explicitly so a failed clone cannot report success (review finding).
git clone --depth 1 --filter=blob:none --sparse `
    https://github.com/SingleStepTests/ProcessorTests.git $Destination
if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed (exit $LASTEXITCODE)"; exit 1 }
git -C $Destination sparse-checkout set 6502/v1
if ($LASTEXITCODE -ne 0) { Write-Error "git sparse-checkout failed (exit $LASTEXITCODE)"; exit 1 }
if (-not (Test-Path $vectorDir)) { Write-Error "clone succeeded but $vectorDir is missing"; exit 1 }
Write-Host "Vectors fetched to $vectorDir"
