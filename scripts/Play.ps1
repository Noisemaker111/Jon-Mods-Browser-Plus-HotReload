param([string]$GamePath)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 7DaysToDie,7DaysToDieServer,7DaysToDie_EAC -ErrorAction SilentlyContinue) { throw '7 Days to Die is already running' }
if (!$GamePath) {
    $steam = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (!$steam) { throw 'Steam was not found; launch through the game launcher with EAC disabled' }
    $libraries = @($steam)
    $folders = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $folders) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $folders -Raw), '"path"\s+"([^"]+)"')) { $libraries += $match.Groups[1].Value.Replace('\\','\') }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\7 Days To Die'
        if (Test-Path -LiteralPath (Join-Path $candidate '7DaysToDie.exe')) { $GamePath = $candidate; break }
    }
}
if (!$GamePath -or !(Test-Path -LiteralPath (Join-Path $GamePath '7DaysToDie.exe'))) { throw '7 Days to Die was not found in your Steam libraries' }
Start-Process -FilePath (Join-Path $GamePath '7DaysToDie.exe') -WorkingDirectory $GamePath -WindowStyle Normal
Write-Output 'Launched the non-EAC game executable. The host must disable EAC for the modded world.'
