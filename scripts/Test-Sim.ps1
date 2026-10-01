# Headless mod simulation. Builds every gameplay mod, then runs the real mod
# assemblies against the real installed game assemblies and data - no game launch.
#
# Must run under PowerShell 7 (.NET 10): the game's assemblies need System.Memory,
# which pwsh already provides. The harness is compiled to a library and invoked
# in-process for that reason.
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$Compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this with PowerShell 7 (pwsh); the game assemblies need .NET 10 System.Memory.' }
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputPath) {
    $common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve checkout home' }
    $OutputPath = Join-Path (Split-Path $common -Parent) ('.scratch\sim-' + [guid]::NewGuid().ToString('N'))
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

$managed = Join-Path $GamePath '7DaysToDie_Data\Managed'
if (!(Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) { throw 'Pass -GamePath for your installed game' }

# Build the gameplay mods into the simulation workspace and collect their DLLs.
$folders = @(Get-ChildItem -LiteralPath (Join-Path $repo 'mods') -Directory | Select-Object -ExpandProperty FullName)
$built = @(& (Join-Path $PSScriptRoot 'Build-Mod.ps1') -ModFolder $folders -GamePath $GamePath -Compiler $Compiler -OutputPath (Join-Path $OutputPath 'builds'))
$modDlls = @()
foreach ($build in $built) {
    $extracted = Join-Path $OutputPath ('extract-' + $build.Name)
    Expand-Archive -LiteralPath $build.Archive -DestinationPath $extracted
    $modDlls += Join-Path $extracted ($build.Name + '\' + $build.Name + '.dll')
}

# Compile the harness against the real game assembly.
$harnessDll = Join-Path $OutputPath 'GameSim.dll'
& $Compiler -nologo -target:library ('-r:' + (Join-Path $managed 'Assembly-CSharp.dll')) ('-r:' + (Join-Path $managed 'UnityEngine.CoreModule.dll')) ('-out:' + $harnessDll) (Join-Path $repo 'sim\GameSim.cs')
if ($LASTEXITCODE -ne 0) { throw 'Simulation harness compilation failed' }

# Resolve game types from the install, then run the harness in this .NET 10 host.
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ($eventArgs.Name -split ',')[0]
    $path = Join-Path $managed ($name + '.dll')
    if (Test-Path -LiteralPath $path) { return [System.Reflection.Assembly]::LoadFrom($path) }
    return $null
})
$harness = [System.Reflection.Assembly]::LoadFrom($harnessDll)
$arguments = [string[]]@(@($GamePath) + $modDlls)
$exit = $harness.GetType('GameSim').GetMethod('Main').Invoke($null, @(, $arguments))
if ($exit -ne 0) { throw ('Headless simulation failed; evidence: ' + $OutputPath) }
Write-Output ('Simulation evidence: ' + $OutputPath)
