#!/usr/bin/env pwsh
# Fetches apl2cpm3 / CPM3.1_Z80_Softcard Disk 1 (CP/M 3.1 for the Microsoft Z-80 SoftCard) into the vector
# cache (NEVER vendored). Fetched on demand at test time, NOT committed (ADR 0018 Decision 5; owner sign-off
# COVERED by the existing CP/M-disk sign-off). A DISTINCT subdir from the 2.2 disk so cpm/softcard-cpm.dsk is
# never clobbered.
#   <cache>/cpm/apl2cpm3/CPM3.1_Disk_1.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the boot disk)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$aplDir = Join-Path $Destination "cpm/apl2cpm3"
New-Item -ItemType Directory -Force $aplDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $aplDir $name
    if (Test-Path $out) { Write-Host "$name already present at $out"; return }
    foreach ($url in $urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
            $len = (Get-Item $out).Length
            if ($len -eq $wantLen) { Write-Host "$name fetched to $out ($len bytes) from $url"; return }
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "$url failed the sanity check (len=$len, want $wantLen) -- trying next"
        } catch {
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "fetch of $url failed ($_) -- trying next"
        }
    }
    if ($required) { Write-Error "could not fetch the required $name from any source" }
}

# The owner confirms the real source URL at PR time (sign-off COVERED, ADR 0018 Decision 5/6:
# cpm.z80.de/download/apl2cpm3.zip or the Asimov CPM3.1_Z80_Softcard.zip; extract CPM3.1_Disk_1.dsk).
$disk1Url = "https://mirror.example/cpm/apl2cpm3/CPM3.1_Disk_1.dsk"
if (-not (Test-Path (Join-Path $aplDir "CPM3.1_Disk_1.dsk")) -and $disk1Url -like "*mirror.example*") {
    Write-Error ("the apl2cpm3 Disk-1 URL has not been configured -- edit tools/get-apl2cpm3.ps1 and set the " +
                 "real source (cpm.z80.de/download/apl2cpm3.zip or the Asimov CPM3.1_Z80_Softcard.zip), then re-run.")
}

Fetch-One "CPM3.1_Disk_1.dsk" 143360 $true @($disk1Url)

Write-Host "apl2cpm3 fetch complete (cache: $aplDir). Disk 1 is the only required image; Disks 2-7 optional."
