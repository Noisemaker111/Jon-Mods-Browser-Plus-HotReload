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
& (Join-Path $PSScriptRoot 'Test-Coop.ps1') -GamePath $GamePath
if ($LASTEXITCODE -ne 0) { throw 'Individual gameplay verification failed' }
