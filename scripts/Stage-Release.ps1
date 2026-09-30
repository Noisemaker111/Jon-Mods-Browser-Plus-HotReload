param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
$scratch = Join-Path (Split-Path $common -Parent) '.scratch'
if (!$OutputPath) { $OutputPath = Join-Path $scratch ('release-ready-' + [guid]::NewGuid().ToString('N')) }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
foreach ($active in @((Join-Path $env:APPDATA '7DaysToDie\Mods'), (Join-Path $GamePath 'Mods'))) {
    $active = [IO.Path]::GetFullPath($active).TrimEnd('\')
    if ($OutputPath -eq $active -or $OutputPath.StartsWith($active + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Stage outside active Mods' }
}
if (Test-Path -LiteralPath $OutputPath) { throw 'Choose a fresh staging folder; existing files are preserved' }
$dirty = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the reviewed source before recording a release revision' }
$revision = & git -C $repo rev-parse HEAD
$work = Join-Path $scratch ('release-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work,$OutputPath -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -GamePath $GamePath -OutputPath (Join-Path $work 'manager')
$built = & (Join-Path $PSScriptRoot 'Build-Mod.ps1') -ModFolder (Join-Path $repo 'mods\JonCoopQoL') -GamePath $GamePath -OutputPath (Join-Path $work 'qol')
$archives = @(
    @{ Path=(Join-Path $work 'JonModsBrowser-Plus-HotReload-beta.zip'); Folder='HotReloadTool'; Source=(Join-Path $work 'manager\HotReloadTool') },
    @{ Path=$built.Archive; Folder='JonCoopQoL'; Source=(Join-Path (Split-Path $built.Archive -Parent) 'JonCoopQoL') }
)
foreach ($archive in $archives) {
    $extracted = Join-Path $work ('extract-' + $archive.Folder)
    Expand-Archive -LiteralPath $archive.Path -DestinationPath $extracted
    $roots = @(Get-ChildItem -LiteralPath $extracted -Force)
    if ($roots.Count -ne 1 -or !$roots[0].PSIsContainer -or $roots[0].Name -ne $archive.Folder) { throw ('Archive must contain only ' + $archive.Folder) }
    $sourceFiles = @(Get-ChildItem -LiteralPath $archive.Source -File -Recurse)
    $extractedFiles = @(Get-ChildItem -LiteralPath $roots[0].FullName -File -Recurse)
    if ($sourceFiles.Count -ne $extractedFiles.Count) { throw 'Archive file count differs from the built mod' }
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($archive.Source.Length + 1)
        if ((Get-FileHash -LiteralPath (Join-Path $roots[0].FullName $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw ('Archive mismatch: ' + $relative) }
    }
    Copy-Item -LiteralPath $archive.Path,($archive.Path + '.sha256') -Destination $OutputPath
    Write-Output ('Verified standalone download: ' + (Join-Path $OutputPath (Split-Path $archive.Path -Leaf)))
}
$identity = Get-Content -LiteralPath (Join-Path $work 'manager\HotReloadTool\build.json') -Raw | ConvertFrom-Json
if ($identity.revision -ne $revision) { throw 'Manager revision differs from staged source' }
Write-Output ('Source revision: ' + $revision)
Write-Output 'Downloads are separate ordinary mod folders. Building never installs, launches or publishes.'
