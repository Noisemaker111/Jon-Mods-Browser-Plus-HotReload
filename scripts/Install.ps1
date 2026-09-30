param([string]$ModsPath = (Join-Path $env:APPDATA '7DaysToDie\Mods'))
$ErrorActionPreference = 'Stop'
$sourceBase = $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Mods\HotReloadTool\ModInfo.xml')) { $sourceBase = Join-Path $PSScriptRoot 'Mods' }
$source = Join-Path $sourceBase 'HotReloadTool'
if (!(Test-Path -LiteralPath (Join-Path $source 'ModInfo.xml'))) { throw 'Extract the whole release ZIP before running Install.cmd' }
if (Get-Process -Name 7DaysToDie,7DaysToDieServer,7DaysToDie_EAC -ErrorAction SilentlyContinue) { throw 'Close 7 Days to Die before installing this update' }
$identity = Get-Content -LiteralPath (Join-Path $source 'build.json') -Raw | ConvertFrom-Json
if (!$identity.files -or !$identity.revision) { throw 'This release is missing its file manifest' }
foreach ($entry in $identity.files.PSObject.Properties) {
    $path = Join-Path $source $entry.Name
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.Value) { throw ('Release file failed verification: ' + $entry.Name) }
}
$packages = @($source)
$coop = Join-Path $sourceBase 'JonCoopQoL'
if ((Test-Path -LiteralPath (Join-Path $PSScriptRoot 'bundle.json')) -and !(Test-Path -LiteralPath (Join-Path $coop 'ModInfo.xml'))) { throw 'Co-op bundle is missing JonCoopQoL; extract the whole ZIP' }
if (Test-Path -LiteralPath $coop) {
    $bundle = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bundle.json') -Raw | ConvertFrom-Json
    if ($bundle.revision -ne $identity.revision) { throw 'Co-op bundle revision differs from the manager' }
    foreach ($file in Get-ChildItem -LiteralPath $coop -File -Recurse) {
        $relative = 'Mods/JonCoopQoL/' + $file.FullName.Substring($coop.Length + 1).Replace('\','/')
        $entry = $bundle.files.PSObject.Properties[$relative]
        if (!$entry -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $entry.Value) { throw ('Co-op file failed verification: ' + $relative) }
    }
    foreach ($entry in $bundle.files.PSObject.Properties) {
        if (!$entry.Name.StartsWith('Mods/JonCoopQoL/')) { continue }
        $file = Join-Path $PSScriptRoot $entry.Name
        if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.Value) { throw ('Missing or damaged co-op file: ' + $entry.Name) }
    }
    $packages += $coop
}
$modsDestination = [IO.Path]::GetFullPath($ModsPath)
$destination = Join-Path $modsDestination 'HotReloadTool'
$previous = Join-Path (Split-Path ([IO.Path]::GetFullPath($ModsPath)) -Parent) ('Mods-offline\HotReloadTool-update-' + [guid]::NewGuid().ToString('N'))
$changed = @()
# Replace only release files; browser registry, portable packs and backups survive.
try {
foreach ($package in $packages) {
foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
    $relative = $file.FullName.Substring($package.Length + 1)
    $packageName = Split-Path $package -Leaf
    $target = Join-Path (Join-Path $modsDestination $packageName) $relative
    if ($packageName -eq 'HotReloadTool' -and $relative -ne 'build.json' -and !$identity.files.PSObject.Properties[$relative.Replace('\','/')]) { throw ('Unexpected release file: ' + $relative) }
    $saved = Join-Path (Join-Path $previous $packageName) $relative
    $existed = Test-Path -LiteralPath $target
    if ($existed) {
        New-Item -ItemType Directory -Force -Path (Split-Path $saved -Parent) | Out-Null
        Copy-Item -LiteralPath $target -Destination $saved
    }
    $changed += @{ Target = $target; Saved = $saved; Existed = $existed }
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw ('Install verification failed: ' + $relative) }
}
}
} catch {
    for ($i = $changed.Count - 1; $i -ge 0; $i--) {
        $item = $changed[$i]
        if ($item.Existed) { Copy-Item -LiteralPath $item.Saved -Destination $item.Target -Force }
        elseif (Test-Path -LiteralPath $item.Target) { Remove-Item -LiteralPath $item.Target }
    }
    throw
}
Write-Output ('Installed and verified: ' + $destination)
if ($packages.Count -gt 1) { Write-Output ('Installed and verified: ' + (Join-Path $modsDestination 'JonCoopQoL')) }
if (Test-Path -LiteralPath $previous) { Write-Output ('Previous release files: ' + $previous) }
Write-Output 'Use Play.cmd to launch without EAC. Both friends install the same release and game version.'
