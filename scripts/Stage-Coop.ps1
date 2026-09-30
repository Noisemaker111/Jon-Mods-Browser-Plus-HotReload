param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
$scratch = Join-Path (Split-Path $common -Parent) '.scratch'
if (!$OutputPath) { $OutputPath = Join-Path $scratch ('coop-ready-' + [guid]::NewGuid().ToString('N')) }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
foreach ($active in @((Join-Path $env:APPDATA '7DaysToDie\Mods'), (Join-Path $GamePath 'Mods'))) {
    $active = [IO.Path]::GetFullPath($active).TrimEnd('\')
    if ($OutputPath -eq $active -or $OutputPath.StartsWith($active + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Stage outside active Mods' }
}
if (Test-Path -LiteralPath $OutputPath) { throw 'Choose a fresh staging folder; existing files are preserved' }
$dirty = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the reviewed source before recording a bundle revision' }
$revision = & git -C $repo rev-parse HEAD
$work = Join-Path $scratch ('coop-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -GamePath $GamePath -OutputPath (Join-Path $work 'manager')
$built = & (Join-Path $PSScriptRoot 'Build-Mod.ps1') -ModFolder (Join-Path $repo 'mods\JonCoopQoL') -GamePath $GamePath -OutputPath (Join-Path $work 'qol')
$manager = Join-Path $work 'manager\HotReloadTool'
$coop = Join-Path (Split-Path $built.Archive -Parent) 'JonCoopQoL'
New-Item -ItemType Directory -Path (Join-Path $OutputPath 'Mods') -Force | Out-Null
Copy-Item -LiteralPath $manager,$coop -Destination (Join-Path $OutputPath 'Mods') -Recurse
Copy-Item -LiteralPath $built.Archive,($built.Archive + '.sha256') -Destination $OutputPath
$readme = @'
READY FOR A MANUAL SWAP AFTER PLAYING - DEVELOPMENT BUILD

Nothing installs itself or changes the running game. After closing 7 Days
to Die, move old HotReloadTool/JonCoopQoL folders into a sibling Mods-offline
folder outside active Mods and copy BOTH folders from this download's Mods
folder into your active Mods folder. Keep the game's 0_TFP_Harmony and one
copy of each added mod across the user and game Mods locations.

The usual user Mods folder is %AppData%\7DaysToDie\Mods. Preserve your old
HotReloadTool\browser folder in the replacement to retain browser settings,
saved packs and backups. Keep EAC disabled and use the same game build and
these manager/QoL files on both friends and the world host.

The manager retains the browser, manager, hot reload, portable packs, friend
downloads and source compilation. There is no automatic manager update.
Loading changed DLLs/config downloaded from the host still needs a restart.

JonCoopQoL retains loot skulls, party portraits/health/stamina, Tab ->
right-click a friend's party row -> Follow (walking/ground vehicles),
category sorting and Smart/Move matching chest routing. One representative
book, item mod, weapon, ammo type or tool seeds its whole category. Chest
labels are not required. Fill remains native exact-stack filling.

NEW: Middle-click while aiming in gameplay creates pulsing cyan ground
arrows for six seconds, with a fade during the last two. Party members get
the ping automatically. Native saved manual waypoints automatically share
on joining and when added/deleted with map controls. Team copies show the
owner's name, can be tracked, and disappear on party/world cleanup; they
are not saved as your own originals. Both updated folders are necessary
because team sharing uses the new manager bootstrap channel.

See Mods\JonCoopQoL\README.txt for controls and limits. Nine QoL source
files are included for supported live editing after the initial swap.

Offline checks cover native routing definitions, saved loot history, ping
fade/pulse, serialized team snapshots/deletions/ownership, compiler and pack
behavior. Native UI, ground rendering/input, walking/driving, shared-map
controls, loot replication and two-player join/download/rejoin remain
UNVERIFIED while Jon is playing. Building never starts or alters the game.

This combined development bundle is staged locally from beta. It does not
replace the older public v6.3.0-beta.1 GitHub release.
'@
[IO.File]::WriteAllText((Join-Path $OutputPath 'README.txt'),$readme)
$managerIdentity = Get-Content (Join-Path $manager 'build.json') -Raw | ConvertFrom-Json
if ($managerIdentity.revision -ne $revision) { throw 'Manager source identity differs from the bundle' }
$hashes = @{}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $OutputPath 'Mods') -File -Recurse) {
    $relative = $file.FullName.Substring($OutputPath.Length + 1).Replace('\','/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName).Hash
}
@{ application='JonCoop-development'; revision=$revision; managerRevision=$managerIdentity.revision;
    gameReferences=$managerIdentity.gameReferences; nativeGameplayVerified=$false; files=$hashes } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputPath 'bundle.json') -Encoding UTF8
$zip = Join-Path $work 'Jon-Coop-development.zip'
Compress-Archive -LiteralPath (Join-Path $OutputPath 'Mods'),(Join-Path $OutputPath 'README.txt'),(Join-Path $OutputPath 'bundle.json') -DestinationPath $zip
$extracted = Join-Path $work 'extracted'
Expand-Archive -LiteralPath $zip -DestinationPath $extracted
foreach ($pair in $hashes.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $extracted $pair.Key)).Hash -ne $pair.Value) { throw ('Combined archive mismatch: ' + $pair.Key) }
}
foreach ($file in Get-ChildItem -LiteralPath $manager,$coop -File -Recurse) {
    $base = if ($file.FullName.StartsWith($manager + '\')) { $manager } else { $coop }
    $relative = $file.FullName.Substring($base.Length + 1)
    $staged = Join-Path $OutputPath ('Mods\' + (Split-Path $base -Leaf) + '\' + $relative)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $staged).Hash) { throw ('Stage differs from build: ' + $relative) }
}
$digest = (Get-FileHash -LiteralPath $zip).Hash
Copy-Item -LiteralPath $zip -Destination $OutputPath
($digest.ToLowerInvariant() + '  Jon-Coop-development.zip') | Set-Content -LiteralPath (Join-Path $OutputPath 'Jon-Coop-development.zip.sha256') -Encoding ASCII
Write-Output ('Ready: ' + $OutputPath)
Write-Output ('Verified mod files: ' + $hashes.Count)
Write-Output ('Source revision: ' + $revision)
Write-Output ('Combined ZIP SHA256: ' + $digest)
