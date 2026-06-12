#!/usr/bin/env pwsh
# Fetches the Klaus Dörmann 6502 functional test binary (pre-assembled default build)
# into the vector cache (same root as the TomHarte vectors; never vendored, spec §8).
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$klausDir = Join-Path $Destination "klaus"
$binPath  = Join-Path $klausDir "6502_functional_test.bin"
if (Test-Path $binPath) { Write-Host "Klaus binary already present at $binPath"; exit 0 }
New-Item -ItemType Directory -Force $klausDir | Out-Null
$url = "https://raw.githubusercontent.com/Klaus2m5/6502_65C02_functional_tests/master/bin_files/6502_functional_test.bin"
Invoke-WebRequest -Uri $url -OutFile $binPath
if ((Get-Item $binPath).Length -ne 65536) {
    Remove-Item $binPath
    # Write-Error terminates under $ErrorActionPreference = "Stop" and exits non-zero.
    Write-Error "downloaded image is not 64 KiB — refusing to cache it"
}
Write-Host "Klaus binary fetched to $binPath"
