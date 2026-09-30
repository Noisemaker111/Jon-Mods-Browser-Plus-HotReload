# Compatibility alias; QoL is published separately from the manager.
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Stage-Release.ps1') -GamePath $GamePath -OutputPath $OutputPath