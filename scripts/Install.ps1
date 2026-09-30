param([string]$ModsPath = (Join-Path $env:APPDATA '7DaysToDie\Mods'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'HotReloadTool'
if (!(Test-Path -LiteralPath (Join-Path $source 'ModInfo.xml'))) { throw 'Extract the whole release ZIP before running Install.cmd' }
if (Get-Process -Name 7DaysToDie,7DaysToDieServer,7DaysToDie_EAC -ErrorAction SilentlyContinue) { throw 'Close 7 Days to Die before installing this update' }
$identity = Get-Content -LiteralPath (Join-Path $source 'build.json') -Raw | ConvertFrom-Json
if (!$identity.files -or !$identity.revision) { throw 'This release is missing its file manifest' }
foreach ($entry in $identity.files.PSObject.Properties) {
    $path = Join-Path $source $entry.Name
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.Value) { throw ('Release file failed verification: ' + $entry.Name) }
}
$destination = Join-Path ([IO.Path]::GetFullPath($ModsPath)) 'HotReloadTool'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$previous = Join-Path (Split-Path ([IO.Path]::GetFullPath($ModsPath)) -Parent) ('Mods-offline\HotReloadTool-update-' + [guid]::NewGuid().ToString('N'))
$changed = @()
# Replace only release files; browser registry, portable packs and backups survive.
try {
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = $file.FullName.Substring($source.Length + 1)
    $target = Join-Path $destination $relative
    if ($relative -ne 'build.json' -and !$identity.files.PSObject.Properties[$relative.Replace('\','/')]) { throw ('Unexpected release file: ' + $relative) }
    $saved = Join-Path $previous $relative
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
} catch {
    for ($i = $changed.Count - 1; $i -ge 0; $i--) {
        $item = $changed[$i]
        if ($item.Existed) { Copy-Item -LiteralPath $item.Saved -Destination $item.Target -Force }
        elseif (Test-Path -LiteralPath $item.Target) { Remove-Item -LiteralPath $item.Target }
    }
    throw
}
Write-Output ('Installed and verified: ' + $destination)
if (Test-Path -LiteralPath $previous) { Write-Output ('Previous release files: ' + $previous) }
Write-Output 'Use Play.cmd to launch without EAC. Both friends install the same release and game version.'
