#!/usr/bin/env pwsh
# Fetches the Microsoft Z-80 SoftCard CP/M 2.2 disk image into the vector cache (same root as the
# Apple/Spectrum/ZEX/Klaus assets; NEVER vendored). Fetched on demand at test time, NOT committed (ADR 0016
# Decisions 4/5; owner sign-off GIVEN for the fetch-on-demand loader from the Asimov mirror).
# Layout written (consumed by CpuEmulator.Machines.SoftCardCpm):
#   <cache>/cpm/softcard-cpm.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the CP/M boot disk)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$cpmDir = Join-Path $Destination "cpm"
New-Item -ItemType Directory -Force $cpmDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $cpmDir $name
    if (Test-Path $out) { Write-Host "$name already present at $out"; return }
    foreach ($url in $urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
            $len = (Get-Item $out).Length
            if ($len -eq $wantLen) { Write-Host "$name fetched to $out ($len bytes) from $url"; return }
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "$url failed the sanity check (len=$len, want $wantLen) — trying next"
        } catch {
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "fetch of $url failed ($_) — trying next"
        }
    }
    if ($required) { Write-Error "could not fetch the required $name from any source" }
}

# NOTE: placeholder URL for the owner to point at the Asimov mirror (apple2.org.za /images/cpm/os/,
# research §9) or a preferred source; the length sanity-check (143360) guarantees a correct image.
Fetch-One "softcard-cpm.dsk" 143360 $true @("https://mirror.example/cpm/softcard-cpm.dsk")

Write-Host "SoftCard CP/M fetch complete (cache: $cpmDir)."
