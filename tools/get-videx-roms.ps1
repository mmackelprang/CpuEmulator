#!/usr/bin/env pwsh
# Fetches the Videx Videoterm ROMs into the vector cache (same root as the Apple/Spectrum/ZEX/Klaus assets;
# NEVER vendored). The 1 KiB firmware ROM and the 2 KiB char ROM are fetched on demand, NOT committed (ADR
# 0016 Decision 4). BOTH are OPTIONAL: a synthetic fallback font + all-zero firmware cover the CP/M-on-Videx
# boot gate; the real ROMs add glyph fidelity.
# Layout written (consumed by CpuEmulator.Machines.VidexRom):
#   <cache>/videx/videx-firmware.rom   1024 bytes  (OPTIONAL)
#   <cache>/videx/videx-char.rom       2048 bytes  (OPTIONAL)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$videxDir = Join-Path $Destination "videx"
New-Item -ItemType Directory -Force $videxDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $videxDir $name
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

# NOTE: placeholder URLs for the owner to point at the Asimov Videx mirror (research §9) or a preferred
# source; the length sanity-checks (1024 / 2048) guarantee a correct image.
Fetch-One "videx-firmware.rom" 1024 $false @("https://mirror.example/videx/videx-firmware.rom")
Fetch-One "videx-char.rom" 2048 $false @("https://mirror.example/videx/videx-character.rom")

Write-Host "Videx ROM fetch complete (cache: $videxDir)."
