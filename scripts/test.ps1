param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die')
$ErrorActionPreference = 'Stop'

# The game assemblies need System.Memory, which only PowerShell 7 (.NET 10) hosts.
# Relaunch under pwsh when invoked from Windows PowerShell so one command still works.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (!$pwsh) { throw 'PowerShell 7 (pwsh) is required for headless simulation.' }
    & $pwsh -NoProfile -File $PSCommandPath -GamePath $GamePath
    exit $LASTEXITCODE
}

$repo = Split-Path $PSScriptRoot -Parent
$common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
$work = Join-Path (Split-Path $common -Parent) ('.scratch\checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$json = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Newtonsoft.Json.dll'
Copy-Item -LiteralPath $json -Destination $work
$options = @('-nologo','-target:exe','-r:System.IO.Compression.dll','-r:System.IO.Compression.FileSystem.dll',('-r:' + $json),('-out:' + (Join-Path $work 'PackChecks.exe')))
$options += '-r:' + (Join-Path $GamePath '7DaysToDie_Data\Managed\netstandard.dll')
& $compiler @options (Join-Path $repo 'src\ModFiles.cs') (Join-Path $repo 'src\InstallQueue.cs') (Join-Path $repo 'tests\PackChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Check compilation failed' }
& (Join-Path $work 'PackChecks.exe') $work
if ($LASTEXITCODE -ne 0) { throw ('Pack verification failed; evidence: ' + $work) }
& $compiler -nologo -target:exe -r:System.Core.dll ('-out:' + (Join-Path $work 'CompilerChecks.exe')) (Join-Path $repo 'src\ModCompiler.cs') (Join-Path $repo 'tests\CompilerChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compiler check compilation failed' }
& (Join-Path $work 'CompilerChecks.exe') (Join-Path $work 'compiler checks with spaces') $compiler
if ($LASTEXITCODE -ne 0) { throw ('Compiler verification failed; evidence: ' + $work) }
Write-Output ('Evidence: ' + $work)
& (Join-Path $PSScriptRoot 'Test-Sim.ps1') -GamePath $GamePath
if ($LASTEXITCODE -ne 0) { throw 'Headless simulation failed' }
& python (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\xui-preview.py') --template party_entry --check
if ($LASTEXITCODE -ne 0) { throw 'Offline UI preview failed' }
& python (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\xui-preview.py') --window mainMenu --check
if ($LASTEXITCODE -ne 0) { throw 'Offline window preview failed' }
& (Join-Path $PSScriptRoot 'Test-Coop.ps1') -GamePath $GamePath
if ($LASTEXITCODE -ne 0) { throw 'Individual gameplay verification failed' }
& python -B -m unittest discover -s (Join-Path $repo 'lab') -p test_server.py
if ($LASTEXITCODE -ne 0) { throw 'Browser lab backend checks failed' }
& python -B -m unittest discover -s (Join-Path $repo 'lab') -p test_studio.py
if ($LASTEXITCODE -ne 0) { throw 'Native UI editor round-trip checks failed' }
& node --check (Join-Path $repo 'lab/static/app.js')
if ($LASTEXITCODE -ne 0) { throw 'Browser lab JavaScript syntax check failed' }
& node --check (Join-Path $repo 'lab/static/studio-editor.js')
if ($LASTEXITCODE -ne 0) { throw 'UI editor JavaScript syntax check failed' }
& node (Join-Path $repo 'lab/test-model.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Browser lab coordinate/replay checks failed' }
$pencilSource = Join-Path (Split-Path $common -Parent) '.scratch/reference/open-pencil-v0.15.1'
$bun = Get-Command bun -ErrorAction SilentlyContinue
if ($bun -and (Test-Path (Join-Path $pencilSource 'node_modules'))) {
    & $bun.Source (Join-Path $repo 'lab/pencil/test-adapter.mjs') $pencilSource
    if ($LASTEXITCODE -ne 0) { throw 'Real OpenPencil/XUi adapter round-trip checks failed' }
} else {
    Write-Output 'SKIP OpenPencil graph checks: build the optional local editor with python lab/pencil/build.py first'
}
Write-Output 'PASS browser lab job reruns, isolation, local API protection, telemetry retention and drawing persistence'
