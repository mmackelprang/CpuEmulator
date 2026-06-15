#!/usr/bin/env pwsh
# Fetches Frank D. Cringle's Z80 instruction-set exercisers (zexdoc.com + zexall.com) into the vector
# cache (same root as the TomHarte vectors + the Klaus binary; never vendored).
#
# Provenance: ZEXDOC/ZEXALL — Frank D. Cringle's Z80 instruction set exerciser (1994), GPL-2.0.
# Primary source: agn453/ZEXALL (curated from YAZE-AG v2.51.3 by Andreas Gerlich).
# Mirror/fallback: begoon/z80exer. These are widely-redistributed public Z80 test binaries; this repo
# fetches them at test time, exactly as it fetches the Klaus 6502 binary + the SingleStepTests vectors.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$zexDir = Join-Path $Destination "zex"
New-Item -ItemType Directory -Force $zexDir | Out-Null

$files = @(
    @{ Name = "zexdoc.com";
       Urls = @("https://raw.githubusercontent.com/agn453/ZEXALL/main/zexdoc.com",
                "https://raw.githubusercontent.com/begoon/z80exer/master/zexdoc.com") },
    @{ Name = "zexall.com";
       Urls = @("https://raw.githubusercontent.com/agn453/ZEXALL/main/zexall.com",
                "https://raw.githubusercontent.com/begoon/z80exer/master/zexall.com") }
)

foreach ($f in $files) {
    $binPath = Join-Path $zexDir $f.Name
    if (Test-Path $binPath) { Write-Host "$($f.Name) already present at $binPath"; continue }
    $ok = $false
    foreach ($url in $f.Urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $binPath -ErrorAction Stop
            $len = (Get-Item $binPath).Length
            $firstByte = [System.IO.File]::ReadAllBytes($binPath)[0]
            # Sanity: non-empty, under 16 KiB, not an HTML error page (first byte '<' = 0x3C).
            if ($len -gt 0 -and $len -lt 0x4000 -and $firstByte -ne 0x3C) {
                Write-Host "$($f.Name) fetched to $binPath ($len bytes) from $url"
                $ok = $true
                break
            }
            Remove-Item $binPath -ErrorAction SilentlyContinue
            Write-Warning "fetched $url but it failed the sanity check (len=$len) — trying the mirror"
        } catch {
            Remove-Item $binPath -ErrorAction SilentlyContinue
            Write-Warning "fetch of $url failed ($_) — trying the mirror"
        }
    }
    if (-not $ok) { Write-Error "could not fetch $($f.Name) from any source"; exit 1 }
}
