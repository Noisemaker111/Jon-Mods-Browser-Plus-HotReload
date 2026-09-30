param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die')
$ErrorActionPreference = 'Stop'
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
Write-Output ('Evidence: ' + $work)
