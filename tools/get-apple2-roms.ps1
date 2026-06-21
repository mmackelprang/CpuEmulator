#!/usr/bin/env pwsh
# Fetches the Apple ][+ ROMs into the vector cache (same root as the Spectrum/ZEX/Klaus assets; NEVER
# vendored). The system ROM (Applesoft + Monitor), the slot-6 Disk II P5/P6 boot ROM, and the
# character-generator ROM are Apple's copyright; fetched on demand at test time, NOT committed (ADR 0014
# Decision 7). Layout written (consumed by CpuEmulator.Machines.Apple2Rom):
#   <cache>/apple2/apple2plus.rom   12288 bytes  (REQUIRED)
#   <cache>/apple2/disk2.rom          256 bytes  (needed to boot a disk)
#   <cache>/apple2/char.rom          2048 bytes  (OPTIONAL — fallback font covers it)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$romDir = Join-Path $Destination "apple2"
New-Item -ItemType Directory -Force $romDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $romDir $name
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
    else { Write-Warning "optional $name not fetched — the built-in fallback font will be used" }
}

# NOTE: placeholder URLs for the owner to point at a preferred source/mirror; the length sanity-check
# guarantees a correct image regardless of source.
Fetch-One "apple2plus.rom" 12288 $true  @("https://mirror.example/apple2/apple2plus.rom")
Fetch-One "disk2.rom"        256  $true  @("https://mirror.example/apple2/disk2-p5p6.rom")
Fetch-One "char.rom"        2048  $false @("https://mirror.example/apple2/apple2-character.rom")

Write-Host "Apple ][+ ROM fetch complete (cache: $romDir)."
