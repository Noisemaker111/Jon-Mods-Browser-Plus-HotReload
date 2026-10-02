# One command for everything that can be verified without a manual launch.
#
# Fast (default): pack/archive, real compiler, static IL checks, headless logic
# simulation, and the offline UI preview. Seconds, deterministic.
#
# Engine (opt-in, -Engine): also boots the real headless server, runs the in-engine
# mod probe, and generates a random world from a seed. Minutes; leaves nothing running.
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [switch]$Engine
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot 'test.ps1') -GamePath $GamePath
if ($LASTEXITCODE -ne 0) { throw 'Fast verification chain failed' }
Write-Output 'Fast chain passed (archive, compiler, static IL, logic sim, UI preview).'

if ($Engine) {
    $python = (Get-Command python -ErrorAction SilentlyContinue).Source
    if (!$python) { throw 'python is required for the engine tier' }
    Write-Output '--- engine: scripted server baseline ---'
    & $python (Join-Path $repo 'tools\headless.py') run
    if ($LASTEXITCODE -ne 0) { throw 'Headless engine baseline failed' }
    Write-Output '--- engine: in-engine mod probe ---'
    & $python (Join-Path $repo 'tools\headless.py') probe
    if ($LASTEXITCODE -ne 0) { throw 'In-engine probe failed' }
    Write-Output '--- engine: random world generation ---'
    & $python (Join-Path $repo 'tools\headless.py') gen --seed TestAll
    if ($LASTEXITCODE -ne 0) { throw 'World generation failed' }
    Write-Output 'Engine tier passed (server baseline, in-engine probe, world generation).'
}
