#!/usr/bin/env pwsh
# Fetches the SingleStepTests 680x0 v1 vectors via sparse checkout. CONFIRMED at implementation time
# (M4.4b Task 1, against the live repo): the repo is SingleStepTests/680x0, and the 68000 test set's
# in-repo path is 68000/v1/ (NOT a top-level v1/ — the repo also holds a map/ tree). The files are GZIP-
# compressed, MNEMONIC+SIZE-keyed (ADD.b.json.gz, ABCD.json.gz). We sparse-checkout 68000/v1 and cache it
# under <dest>/680x0/v1 so the harness (M68000TomHarteVectors) resolves it like the 6502/Z80.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$vectorDir = Join-Path $Destination "680x0/v1"
if (Test-Path $vectorDir) { Write-Host "680x0 vectors already present at $vectorDir"; exit 0 }

# Clone into a temp sibling, then move 68000/v1 into <dest>/680x0/v1. Check $LASTEXITCODE (native commands
# do not trip $ErrorActionPreference) so a failed clone cannot report success (the Z80 script's finding).
$clone = Join-Path $Destination "680x0-clone"
if (Test-Path $clone) { Remove-Item -Recurse -Force $clone }
git clone --depth 1 --filter=blob:none --sparse `
    https://github.com/SingleStepTests/680x0.git $clone
if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed (exit $LASTEXITCODE)"; exit 1 }
git -C $clone sparse-checkout set 68000/v1
if ($LASTEXITCODE -ne 0) { Write-Error "git sparse-checkout failed (exit $LASTEXITCODE)"; exit 1 }
$srcV1 = Join-Path $clone "68000/v1"
if (-not (Test-Path $srcV1)) { Write-Error "clone succeeded but 68000/v1/ is missing"; exit 1 }

New-Item -ItemType Directory -Force (Join-Path $Destination "680x0") | Out-Null
Move-Item $srcV1 $vectorDir
Remove-Item -Recurse -Force $clone
Write-Host "680x0 vectors fetched to $vectorDir"
