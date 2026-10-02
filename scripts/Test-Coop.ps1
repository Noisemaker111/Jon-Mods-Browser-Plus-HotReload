param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
$work = Join-Path (Split-Path $common -Parent) ('.scratch\individual-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$folders = @(Get-ChildItem -LiteralPath (Join-Path $repo 'mods') -Directory | Select-Object -ExpandProperty FullName)
$built = @(& (Join-Path $PSScriptRoot 'Build-Mod.ps1') -ModFolder $folders -GamePath $GamePath -OutputPath $work)
$cecil = Join-Path $GamePath 'Mods\0_TFP_Harmony\Mono.Cecil.dll'
Copy-Item -LiteralPath $cecil -Destination $work
$compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$sources = @('mods/JonCategoryStorage/src/Rules.cs','mods/JonFollow/src/Rules.cs','mods/JonLootSkulls/src/Rules.cs','mods/JonLootSkulls/src/LootLedger.cs','mods/JonSharedWaypoints/src/TeamProtocol.cs','mods/JonGroundPings/src/TeamProtocol.cs','src/TeamEnvelope.cs','tests/CoopChecks.cs') | ForEach-Object { Join-Path $repo $_ }
# Compile against the game's own API too, so a gameplay source that legitimately uses
# engine type names (GamePrefs, enums) does not fail only because this check is narrow.
$managed = Join-Path $GamePath '7DaysToDie_Data\Managed'
$options = @('-nologo','-target:exe','-r:System.Core.dll')
# Keep the framework runtime (do not reference the game's Mono mscorlib/netstandard);
# add the game API so legitimate engine type names compile.
foreach ($reference in @((Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $managed 'UnityEngine.CoreModule.dll'), $cecil)) { $options += '-r:' + $reference }
$options += '-out:' + (Join-Path $work 'CoopChecks.exe')
& $compiler @options @sources
if ($LASTEXITCODE -ne 0) { throw 'Individual gameplay check compilation failed' }
$dlls = @()
foreach ($build in $built) {
    $extracted = Join-Path $work ('extracted-' + $build.Name)
    Expand-Archive -LiteralPath $build.Archive -DestinationPath $extracted
    $roots = @(Get-ChildItem -LiteralPath $extracted -Force)
    if ($roots.Count -ne 1 -or !$roots[0].PSIsContainer -or $roots[0].Name -ne $build.Name) { throw 'Mod ZIP must contain exactly its own mod folder' }
    $mod = $roots[0].FullName
    $dlls += Join-Path $mod ($build.Name + '.dll')
    if ((Get-FileHash -LiteralPath $build.Archive).Hash -ne $build.Sha256) { throw 'Archive checksum differs from build' }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo ('mods/' + $build.Name)) -File -Recurse) {
        $relative = $file.FullName.Substring((Join-Path $repo ('mods/' + $build.Name)).Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $mod $relative)).Hash) { throw ('Extracted package differs: ' + $relative) }
    }
}
& (Join-Path $work 'CoopChecks.exe') (Join-Path $GamePath 'Data/Config/items.xml') $work (Join-Path $GamePath '7DaysToDie_Data/Managed/Assembly-CSharp.dll') @dlls
if ($LASTEXITCODE -ne 0) { throw ('Gameplay verification failed; evidence: ' + $work) }
Write-Output ('Six independent archives and source/config bytes verified. Evidence: ' + $work)
# Follow hit-tests party entries itself, so it ships no HUD XML and works with
# the vanilla party list or Portraits. Portraits' entry must apply to the game.
if (Test-Path -LiteralPath (Join-Path $repo 'mods/JonFollow/Config')) { throw 'Follow must not patch the party HUD' }
$base = New-Object Xml.XmlDocument
$base.Load((Join-Path $GamePath 'Data/Config/XUi_InGame/templates.xml'))
$patch = New-Object Xml.XmlDocument
$patch.Load((Join-Path $repo 'mods/JonPartyPortraits/Config/XUi_InGame/templates.xml'))
foreach ($operation in $patch.DocumentElement.ChildNodes) {
    if ($operation.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
    $targets = @($base.SelectNodes($operation.GetAttribute('xpath')))
    if (!$targets.Count) { throw ('Portraits XPath matches nothing: ' + $operation.GetAttribute('xpath')) }
    foreach ($target in $targets) {
        switch ($operation.Name) {
            'set' { $target.InnerText = $operation.InnerText }
            'remove' { if ($target -is [Xml.XmlAttribute]) { $target.OwnerElement.RemoveAttributeNode($target) | Out-Null } else { $target.ParentNode.RemoveChild($target) | Out-Null } }
            'append' { foreach ($node in $operation.ChildNodes) { $target.AppendChild($base.ImportNode($node,$true)) | Out-Null } }
        }
    }
}
foreach ($name in 'jonPortrait','jonLevel','jonXp','jonXpDeficit','distance','arrowContent') {
    if ($base.SelectNodes('/templates/party_entry/rect/*[@name="' + $name + '"]').Count -ne 1) { throw ('Portraits party entry lacks ' + $name) }
}
Write-Output 'PASS Portraits party entry applies with portrait, level, XP, penalty and distance; Follow needs no HUD patch'
