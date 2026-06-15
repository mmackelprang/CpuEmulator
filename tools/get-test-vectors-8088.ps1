#!/usr/bin/env pwsh
# Fetches the SingleStepTests 8088 v2 vectors via sparse checkout. CONFIRMED at M5 recon time (read-only, the
# 8088 schema-pinning pass) against the live repo: the repo is SingleStepTests/8088, and the v2 test set's
# in-repo path is v2/ (a top-level dir; the repo also holds v1/, v2_binary/, v2_undefined/, metadata.json).
# The files are GZIP-compressed and OPCODE-HEX-keyed — 00.json.gz, 88.json.gz, A4.json.gz (NOT mnemonic+size-
# keyed like the 680x0 set; closer to the 6502/Z80 hex keying, but gzipped like the 680x0 set). 324 files,
# 10,000 cases each (string ops 2,000; a few trivial families 1,000). We sparse-checkout v2 and cache it under
# <dest>/8088/v2 so a future M5.4 harness (M8088TomHarteVectors) resolves it like the 6502/Z80/680x0 sets.
# See docs/architecture/0006-8086-decode-modrm-instruction-set-and-m5-arc.md Decision 5 for the pinned schema.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$vectorDir = Join-Path $Destination "8088/v2"
if (Test-Path $vectorDir) { Write-Host "8088 vectors already present at $vectorDir"; exit 0 }

# Ensure the destination root exists (a fresh machine or a custom CPUEMULATOR_TESTVECTORS may not have it)
# so the clone's parent directory is present.
New-Item -ItemType Directory -Force $Destination | Out-Null

# Clone into a temp sibling, then move v2 into <dest>/8088/v2. Check $LASTEXITCODE (native commands do not
# trip $ErrorActionPreference) so a failed clone cannot report success (the Z80/680x0 scripts' finding).
$clone = Join-Path $Destination "8088-clone"
if (Test-Path $clone) { Remove-Item -Recurse -Force $clone }
git clone --depth 1 --filter=blob:none --sparse `
    https://github.com/SingleStepTests/8088.git $clone
if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed (exit $LASTEXITCODE)"; exit 1 }
git -C $clone sparse-checkout set v2
if ($LASTEXITCODE -ne 0) { Write-Error "git sparse-checkout failed (exit $LASTEXITCODE)"; exit 1 }
$srcV2 = Join-Path $clone "v2"
if (-not (Test-Path $srcV2)) { Write-Error "clone succeeded but v2/ is missing"; exit 1 }

New-Item -ItemType Directory -Force (Join-Path $Destination "8088") | Out-Null
Move-Item $srcV2 $vectorDir
Remove-Item -Recurse -Force $clone
Write-Host "8088 vectors fetched to $vectorDir"
