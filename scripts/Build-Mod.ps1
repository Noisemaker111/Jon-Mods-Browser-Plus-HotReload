param(
    [Parameter(Mandatory=$true)][string[]]$ModFolder,
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$Compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputPath) {
    $common = & git -C $repo rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve checkout home' }
    $OutputPath = Join-Path (Split-Path $common -Parent) '.scratch\mod-builds'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
foreach ($active in @((Join-Path $env:APPDATA '7DaysToDie\Mods'), (Join-Path $GamePath 'Mods'))) {
    $active = [IO.Path]::GetFullPath($active).TrimEnd('\')
    if ($OutputPath -eq $active -or $OutputPath.StartsWith($active + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Build into an offline workspace, outside active Mods' }
}
function Read-Xml([string]$path) {
    $doc = New-Object Xml.XmlDocument
    $doc.XmlResolver = $null
    $doc.Load($path)
    return ,$doc
}
# Loaded once per batch; the exact compiler runner is also used by in-game edits.
if (!('HotReloadTool.ModCompiler' -as [type])) {
    Add-Type -Path (Join-Path $repo 'src\ModCompiler.cs')
}
foreach ($folder in $ModFolder) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $folder = (Resolve-Path -LiteralPath $folder).Path
    if ($OutputPath.StartsWith($folder.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the mod source folder' }
    $info = Read-Xml (Join-Path $folder 'ModInfo.xml')
    $name = $info.SelectSingleNode('/xml/Name/@value').Value
    $version = $info.SelectSingleNode('/xml/Version/@value').Value
    if (!$name -or !$version -or $name -notmatch '^[A-Za-z0-9_.-]+$' -or $name -in @('.','..')) { throw ('Invalid ModInfo identity: ' + $folder) }
    $patchCount = 0
    $config = Join-Path $folder 'Config'
    if (Test-Path -LiteralPath $config) {
        foreach ($file in Get-ChildItem -LiteralPath $config -Filter '*.xml' -Recurse -File) {
            $patch = Read-Xml $file.FullName
            if ($patch.DocumentElement.Name -ne 'configs') { throw ('Patch root must be configs: ' + $file.FullName) }
            $relative = $file.FullName.Substring($config.Length + 1)
            $baseFile = Join-Path (Join-Path $GamePath 'Data\Config') $relative
            if (!(Test-Path -LiteralPath $baseFile)) { throw ('No base game XML for ' + $relative + '; dependency-specific configs require their own validation') }
            $base = Read-Xml $baseFile
            foreach ($operation in $patch.DocumentElement.ChildNodes) {
                if ($operation.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
                $xpath = $operation.GetAttribute('xpath')
                if (!$xpath) { throw ('Missing XPath in ' + $relative) }
                $targets = @($base.SelectNodes($xpath))
                if (!$targets.Count) { throw ('XPath matches no current game nodes in ' + $relative + ': ' + $xpath) }
                foreach ($target in $targets) {
                    switch ($operation.Name) {
                        'set' { $target.InnerText = $operation.InnerText }
                        'remove' { if ($target -is [Xml.XmlAttribute]) { $target.OwnerElement.RemoveAttributeNode($target) | Out-Null } else { $target.ParentNode.RemoveChild($target) | Out-Null } }
                        'append' { foreach ($node in $operation.ChildNodes) { $target.AppendChild($base.ImportNode($node,$true)) | Out-Null } }
                        'insertBefore' { foreach ($node in $operation.ChildNodes) { $target.ParentNode.InsertBefore($base.ImportNode($node,$true),$target) | Out-Null } }
                        'insertAfter' { $anchor = $target; foreach ($node in $operation.ChildNodes) { $anchor = $target.ParentNode.InsertAfter($base.ImportNode($node,$true),$anchor) } }
                        default { throw ('Unsupported validation operation: ' + $operation.Name + '; check it through the game before packaging') }
                    }
                }
                $patchCount++
            }
        }
    }
    $sources = @()
    foreach ($sourceName in @('src','source','sources')) {
        $sourceDir = Join-Path $folder $sourceName
        if (Test-Path -LiteralPath $sourceDir) { $sources += @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -Recurse -File | Select-Object -ExpandProperty FullName) }
    }
    $run = Join-Path $OutputPath ($name + '-' + [guid]::NewGuid().ToString('N'))
    $package = Join-Path $run $name
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $folder -Force) {
        if ($entry.Name -in @('cache','bin','obj','.git','.claude','.scratch')) { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $package -Recurse
    }
    $compileMs = 0
    if ($sources.Count) {
        $references = @(Get-ChildItem -LiteralPath (Join-Path $GamePath '7DaysToDie_Data\Managed') -Filter '*.dll' -File | Select-Object -ExpandProperty FullName)
        $references += Join-Path $GamePath 'Mods\0_TFP_Harmony\0Harmony.dll'
        $libraries = Join-Path $folder 'lib'
        if (Test-Path -LiteralPath $libraries) { $references += @(Get-ChildItem -LiteralPath $libraries -Filter '*.dll' -File | Select-Object -ExpandProperty FullName) }
        $compiled = [HotReloadTool.ModCompiler]::Compile($Compiler, [string[]]$sources, [string[]]$references, (Join-Path $package ($name + '.dll')), 30000)
        if (!$compiled.Success) { throw $compiled.Diagnostics }
        $compileMs = $compiled.Milliseconds
    }
    $archive = Join-Path $run ($name + '.zip')
    Compress-Archive -LiteralPath $package -DestinationPath $archive
    $digest = (Get-FileHash -LiteralPath $archive).Hash
    ($digest.ToLowerInvariant() + '  ' + (Split-Path $archive -Leaf)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
    $result = [pscustomobject]@{ Name=$name; Version=$version; XmlOperations=$patchCount; Sources=$sources.Count; CompileMilliseconds=$compileMs; TotalMilliseconds=$clock.ElapsedMilliseconds; Archive=$archive; Sha256=$digest }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'build-result.json') -Encoding UTF8
    $result
}
