param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
$work = Join-Path (Split-Path $common -Parent) ('.scratch\coop-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$built = & (Join-Path $PSScriptRoot 'Build-Mod.ps1') -ModFolder (Join-Path $repo 'mods\JonCoopQoL') -GamePath $GamePath -OutputPath $work
$cecil = Join-Path $GamePath 'Mods\0_TFP_Harmony\Mono.Cecil.dll'
Copy-Item -LiteralPath $cecil -Destination $work
$compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
& $compiler -nologo -target:exe -r:System.Core.dll ('-r:' + $cecil) ('-out:' + (Join-Path $work 'CoopChecks.exe')) (Join-Path $repo 'mods\JonCoopQoL\src\Rules.cs') (Join-Path $repo 'mods\JonCoopQoL\src\LootLedger.cs') (Join-Path $repo 'tests\CoopChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Co-op check compilation failed' }
# Drive the extracted artifact rather than inspecting the source directory.
$extracted = Join-Path $work 'extracted'
Expand-Archive -LiteralPath $built.Archive -DestinationPath $extracted
$mod = Join-Path $extracted 'JonCoopQoL'
& (Join-Path $work 'CoopChecks.exe') (Join-Path $GamePath 'Data\Config\items.xml') $work (Join-Path $GamePath '7DaysToDie_Data\Managed\Assembly-CSharp.dll') (Join-Path $mod 'JonCoopQoL.dll')
if ($LASTEXITCODE -ne 0) { throw ('Co-op verification failed; evidence: ' + $work) }
if ((Get-FileHash -LiteralPath $built.Archive).Hash -ne $built.Sha256) { throw 'Archive checksum differs from build identity' }
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo 'mods\JonCoopQoL') -File -Recurse) {
    $relative = $file.FullName.Substring((Join-Path $repo 'mods\JonCoopQoL').Length + 1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $mod $relative)).Hash) { throw ('Extracted package differs: ' + $relative) }
}
$built | Format-List
Write-Output ('Extracted archive and all source/config bytes verified. Evidence: ' + $work)
