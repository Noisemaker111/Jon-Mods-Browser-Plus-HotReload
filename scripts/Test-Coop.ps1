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
& $compiler -nologo -target:exe -r:System.Core.dll ('-r:' + $cecil) ('-out:' + (Join-Path $work 'CoopChecks.exe')) @sources
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
# The two optional party mods must compose in either XML application order.
foreach ($order in @(@('JonFollow','JonPartyPortraits'),@('JonPartyPortraits','JonFollow'))) {
    $base = New-Object Xml.XmlDocument
    $base.Load((Join-Path $GamePath 'Data/Config/XUi_InGame/templates.xml'))
    foreach ($name in $order) {
        $patch = New-Object Xml.XmlDocument
        $patch.Load((Join-Path $repo ('mods/' + $name + '/Config/XUi_InGame/templates.xml')))
        foreach ($operation in $patch.DocumentElement.ChildNodes) {
            if ($operation.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
            foreach ($target in @($base.SelectNodes($operation.GetAttribute('xpath')))) {
                switch ($operation.Name) {
                    'set' { $target.InnerText = $operation.InnerText }
                    'remove' { $target.ParentNode.RemoveChild($target) | Out-Null }
                    'append' { foreach ($node in $operation.ChildNodes) { $target.AppendChild($base.ImportNode($node,$true)) | Out-Null } }
                }
            }
        }
    }
    if ($base.SelectNodes('/templates/party_entry/rect/button[@name="jonFollowTarget"]').Count -ne 1 -or $base.SelectNodes('/templates/party_entry/rect/texture[@name="jonPortrait"]').Count -ne 1) { throw 'Follow and Portraits do not compose in both XML orders' }
}
Write-Output 'PASS independent Follow and Portraits compose in either native XML order'
