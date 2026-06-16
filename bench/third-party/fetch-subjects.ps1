# Fetch the third-party 6502 emulator sources/runtimes into the bench cache dir
# (the PowerShell twin of fetch-subjects.sh). The emulators are NOT vendored — this
# populates a cache dir the adapters probe for. Re-runnable + idempotent.
#
# Cache layout (default ~/.cache/cpuemulator/bench, override with $env:CPUEMULATOR_BENCHCACHE):
#   fake6502/fake6502.c          (Mike Chambers' single-file C 6502)
#   py65venv/                    (a Python venv with py65 installed)
#   node_modules/@sfotty-pie/    (the sfotty npm package)
$ErrorActionPreference = 'Continue'
$cache = if ($env:CPUEMULATOR_BENCHCACHE) { $env:CPUEMULATOR_BENCHCACHE }
         else { Join-Path $HOME '.cache/cpuemulator/bench' }
New-Item -ItemType Directory -Force -Path $cache | Out-Null
Write-Host "bench cache: $cache"

# fake6502 (C) — the omarandlorraine fork (context-struct API: needs both .c + .h)
$fakeDir = Join-Path $cache 'fake6502'
New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
function Get-Fake([string]$name) {
    $dst = Join-Path $fakeDir $name
    if (-not (Test-Path $dst)) {
        Write-Host "fetching $name ..."
        try {
            Invoke-WebRequest -UseBasicParsing `
                -Uri "https://raw.githubusercontent.com/omarandlorraine/fake6502/master/$name" `
                -OutFile $dst
            Write-Host "  -> $dst"
        } catch { Write-Host "  !! $name fetch failed — the C adapter will skip-with-note" }
    } else { Write-Host "$name already present" }
}
Get-Fake 'fake6502.c'
Get-Fake 'fake6502.h'

# superzazu/z80 (C) — the Z80 cross-language C anchor (single-file: z80.h + z80.c)
$z80cDir = Join-Path $cache 'z80c'
New-Item -ItemType Directory -Force -Path $z80cDir | Out-Null
function Get-Z80c([string]$name) {
    $dst = Join-Path $z80cDir $name
    if (-not (Test-Path $dst)) {
        Write-Host "fetching $name ..."
        try {
            Invoke-WebRequest -UseBasicParsing `
                -Uri "https://raw.githubusercontent.com/superzazu/z80/master/$name" `
                -OutFile $dst
            Write-Host "  -> $dst"
        } catch { Write-Host "  !! $name fetch failed — the Z80 C adapter will skip-with-note" }
    } else { Write-Host "$name already present" }
}
Get-Z80c 'z80.h'
Get-Z80c 'z80.c'

# DrGoldfire/Z80.js (JS) — the OPTIONAL Z80 cross-language node subject (MIT, single file)
$z80jsDir = Join-Path $cache 'z80js'
New-Item -ItemType Directory -Force -Path $z80jsDir | Out-Null
$z80jsDst = Join-Path $z80jsDir 'Z80.js'
if (-not (Test-Path $z80jsDst)) {
    Write-Host 'fetching Z80.js ...'
    try {
        Invoke-WebRequest -UseBasicParsing `
            -Uri 'https://raw.githubusercontent.com/DrGoldfire/Z80.js/master/Z80.js' `
            -OutFile $z80jsDst
        Write-Host "  -> $z80jsDst"
    } catch { Write-Host '  !! Z80.js fetch failed — the Z80 JS adapter will skip-with-note' }
} else { Write-Host 'Z80.js already present' }

# py65 (Python)
if (Get-Command python -ErrorAction SilentlyContinue) {
    $venv = Join-Path $cache 'py65venv'
    if (-not (Test-Path $venv)) {
        Write-Host 'creating py65 venv + installing py65 ...'
        python -m venv $venv
        $py = Join-Path $venv 'Scripts/python.exe'
        if (-not (Test-Path $py)) { $py = Join-Path $venv 'bin/python' }
        & $py -m pip install --quiet py65
        if ($LASTEXITCODE -eq 0) { Write-Host '  -> py65 installed' }
        else { Write-Host '  !! py65 install failed — the Python adapter will skip-with-note' }
    } else { Write-Host 'py65 venv already present' }
} else { Write-Host 'python not found — the Python adapter will skip-with-note' }

# sfotty (JS / Node)
if (Get-Command npm -ErrorAction SilentlyContinue) {
    $nm = Join-Path $cache 'node_modules/@sfotty-pie/sfotty'
    if (-not (Test-Path $nm)) {
        Write-Host 'installing @sfotty-pie/sfotty ...'
        Push-Location $cache
        npm install --no-save '@sfotty-pie/sfotty' 2>$null | Out-Null
        Pop-Location
        if (Test-Path $nm) { Write-Host '  -> sfotty installed' }
        else { Write-Host '  !! sfotty install failed — the JS adapter will skip-with-note' }
    } else { Write-Host 'sfotty already present' }
} else { Write-Host 'npm not found — the JS adapter will skip-with-note' }

Write-Host 'done. (Asm6502 C# restores via NuGet at build time — no fetch needed.)'
