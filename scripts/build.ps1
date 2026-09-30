param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$OutputPath,
    [string]$Compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputPath) {
    $common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve checkout home' }
    $OutputPath = Join-Path (Split-Path $common -Parent) '.scratch\release'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$managed = Join-Path $GamePath '7DaysToDie_Data\Managed'
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Roslyn csc is required; pass -Compiler with its path' }
if (!(Test-Path -LiteralPath $managed)) { throw 'Pass -GamePath for your installed game' }
$package = Join-Path $OutputPath 'HotReloadTool'
New-Item -ItemType Directory -Force -Path $package,(Join-Path $package 'core') | Out-Null
$refs = @('mscorlib','netstandard','System','System.Core','System.Net.Http','System.Xml',
    'System.IO.Compression','System.IO.Compression.FileSystem','Newtonsoft.Json','Assembly-CSharp',
    'Assembly-CSharp-firstpass','UnityEngine.CoreModule','UnityEngine.IMGUIModule',
    'UnityEngine.TextRenderingModule','UnityEngine.ImageConversionModule','LogLibrary')
$options = @('-noconfig','-nologo','-target:library','-platform:x64','-langversion:latest','-deterministic+')
foreach ($ref in $refs) { $options += '-r:' + (Join-Path $managed ($ref + '.dll')) }
$options += '-r:' + (Join-Path $GamePath 'Mods\0_TFP_Harmony\0Harmony.dll')
& $Compiler @options ('-out:' + (Join-Path $package 'HotReloadTool.dll')) (Join-Path $repo 'src\Bootstrap.cs') (Join-Path $repo 'src\SyncPacket.cs') (Join-Path $repo 'src\TeamEnvelope.cs')
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap compilation failed' }
$core = @('HotReload','ModBrowserCore','ModBrowserUi','ModPack','ModFiles','FriendSync','InstallQueue','ModCompiler') | ForEach-Object { Join-Path $repo ('src\' + $_ + '.cs') }
& $Compiler @options ('-r:' + (Join-Path $package 'HotReloadTool.dll')) ('-out:' + (Join-Path $package 'core\HotReloadCore.dll')) @core
if ($LASTEXITCODE -ne 0) { throw 'Core compilation failed' }
Copy-Item -LiteralPath (Join-Path $repo 'ModInfo.xml') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $repo 'Config') -Destination $package -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $OutputPath 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.txt') -Destination (Join-Path $OutputPath 'README.txt') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.txt') -Destination (Join-Path $package 'README.txt') -Force
Copy-Item -LiteralPath (Join-Path $repo 'scripts\Install.ps1') -Destination $OutputPath -Force
Copy-Item -LiteralPath (Join-Path $repo 'Install.cmd') -Destination $OutputPath -Force
Copy-Item -LiteralPath (Join-Path $repo 'scripts\Play.ps1') -Destination $OutputPath -Force
Copy-Item -LiteralPath (Join-Path $repo 'Play.cmd') -Destination $OutputPath -Force
$revision = & git -C $repo rev-parse HEAD
$files = @{}
foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
    if ($file.FullName -eq (Join-Path $package 'build.json')) { continue }
    $files[$file.FullName.Substring($package.Length + 1).Replace('\','/')] = (Get-FileHash -LiteralPath $file.FullName).Hash
}
$identity = @{ version = '6.3.0-beta.3'; revision = $revision; gameReferences = (Get-FileHash (Join-Path $managed 'Assembly-CSharp.dll')).Hash; builtUtc = [DateTime]::UtcNow.ToString('o'); files = $files }
$identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build.json') -Encoding UTF8
$zip = Join-Path (Split-Path $OutputPath -Parent) 'JonModsBrowser-Plus-HotReload-beta.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip | Format-List
((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() + '  ' + (Split-Path $zip -Leaf)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
Write-Output ('Package: ' + $zip)
