#!/usr/bin/env pwsh
# Fetches the SingleStepTests Z80 v1 vectors via sparse checkout. CONFIRMED at implementation time:
# the Z80 vectors live in a SEPARATE repo from the 6502's (SingleStepTests/z80, NOT ProcessorTests),
# and the test set is at the repo TOP LEVEL under v1/ (not z80/v1/ — verified against the live repo).
# We sparse-checkout v1 and cache it under <dest>/z80/v1 so the harness resolves it like the 6502.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$vectorDir = Join-Path $Destination "z80/v1"
if (Test-Path $vectorDir) { Write-Host "Z80 vectors already present at $vectorDir"; exit 0 }

# Clone into a temp sibling, then move v1 into <dest>/z80/v1. The repo's test set is the top-level
# v1/ directory, so sparse-checkout 'v1' (NOT 'z80/v1'). Check $LASTEXITCODE (native commands don't
# trip $ErrorActionPreference) so a failed clone cannot report success (the 6502 script's finding).
$clone = Join-Path $Destination "z80-clone"
if (Test-Path $clone) { Remove-Item -Recurse -Force $clone }
git clone --depth 1 --filter=blob:none --sparse `
    https://github.com/SingleStepTests/z80.git $clone
if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed (exit $LASTEXITCODE)"; exit 1 }
git -C $clone sparse-checkout set v1
if ($LASTEXITCODE -ne 0) { Write-Error "git sparse-checkout failed (exit $LASTEXITCODE)"; exit 1 }
if (-not (Test-Path (Join-Path $clone "v1"))) { Write-Error "clone succeeded but v1/ is missing"; exit 1 }

New-Item -ItemType Directory -Force (Join-Path $Destination "z80") | Out-Null
Move-Item (Join-Path $clone "v1") $vectorDir
Remove-Item -Recurse -Force $clone
Write-Host "Z80 vectors fetched to $vectorDir"
