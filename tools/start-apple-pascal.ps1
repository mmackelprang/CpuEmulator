#!/usr/bin/env pwsh
# Launches the web surface booting Apple II Pascal (UCSD p-System) in the browser, deterministically. This
# combined launcher: (1) stages the owner-supplied Pascal disks (idempotent -- chains get-apple-pascal.ps1,
# which skips files already present), (2) confirms the Apple ][+ ROMs (apple2plus.rom + the slot-6 disk2.rom)
# are cached -- both REQUIRED to boot a real disk, (3) starts the web server with --system pascal, which FORCES
# the Pascal boot branch regardless of what else is cached (the deterministic override; the auto-probe would
# also pick Pascal once the disks are staged, but --system pascal makes the choice explicit + reproducible).
#
# The Apple ROMs are NOT fetched here (they're a separate owner-supplied asset) -- run tools/setup-apple2.ps1
# (or tools/get-apple2-roms.ps1) first if they're missing; this script tells you so and exits non-zero.
# Idempotent + CWD-independent: re-running it just re-confirms the assets and relaunches the server.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir

Write-Host "Apple Pascal (UCSD p-System) web launcher -> cache root: $Destination"

Write-Host "==> staging the Apple Pascal disks (APPLE1 boot + APPLE0 program; idempotent) ..."
& (Join-Path $dir "get-apple-pascal.ps1") -Destination $Destination

Write-Host "==> confirming the Apple ][+ ROMs are cached ..."
$sysRom  = Join-Path $Destination "apple2/apple2plus.rom"
$diskRom = Join-Path $Destination "apple2/disk2.rom"
if (-not (Test-Path $sysRom)) {
    Write-Error ("the Apple ][+ system ROM is missing ($sysRom). Run tools/setup-apple2.ps1 (or " +
                 "tools/get-apple2-roms.ps1) to stage it, then re-run this.")
}
if (-not (Test-Path $diskRom)) {
    Write-Error ("the slot-6 Disk II boot ROM is missing ($diskRom) -- REQUIRED to boot the Pascal disk. " +
                 "Run tools/setup-apple2.ps1 (or tools/get-apple2-roms.ps1) to stage it, then re-run this.")
}
Write-Host "    apple2plus.rom + disk2.rom present."

Write-Host "Open http://localhost:5000 in your browser once the server prints its URL."
Write-Host "==> launching the web server with --system pascal (forces the Pascal boot branch deterministically) ..."
& dotnet run --project (Join-Path $root "src/CpuEmulator.Surface.Web") -- --system pascal
