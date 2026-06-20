#!/usr/bin/env pwsh
# Fetches the ZX Spectrum 48K ROM (16 KiB) into the vector cache (same root as the ZEX/Klaus assets;
# never vendored).
#
# Provenance: the 48K Spectrum ROM is Amstrad's copyright; Amstrad granted permission to redistribute
# the Spectrum ROMs for emulation use. This repo fetches it at test time, exactly as it fetches the
# Klaus 6502 binary + the ZEX exercisers — it is NOT committed to the repository.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$romDir = Join-Path $Destination "spectrum"
$out = Join-Path $romDir "48.rom"
New-Item -ItemType Directory -Force $romDir | Out-Null

if (Test-Path $out) { Write-Host "Spectrum 48K ROM already present at $out"; exit 0 }

$urls = @(
    "https://raw.githubusercontent.com/chrishaynes/spectrum-roms/master/48.rom",
    "https://raw.githubusercontent.com/oldcomputers-ddns/zx-spectrum-roms/main/48.rom"
)

$ok = $false
foreach ($url in $urls) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
        $len = (Get-Item $out).Length
        if ($len -eq 16384) {
            Write-Host "Spectrum 48K ROM fetched to $out ($len bytes) from $url"
            $ok = $true
            break
        }
        Remove-Item $out -ErrorAction SilentlyContinue
        Write-Warning "fetched $url but it failed the sanity check (len=$len, want 16384) — trying the mirror"
    } catch {
        Remove-Item $out -ErrorAction SilentlyContinue
        Write-Warning "fetch of $url failed ($_) — trying the mirror"
    }
}
if (-not $ok) { Write-Error "could not fetch the Spectrum 48K ROM from any source"; exit 1 }
