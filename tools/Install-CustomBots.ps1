[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [ValidateSet('Low','Medium','High')][string]$Difficulty = 'Medium',
    [switch]$Restore,
    [string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
if (Get-Process cs2 -ErrorAction SilentlyContinue) { throw 'Exit CS2 before replacing or restoring plugins.' }
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $gameRoot 'game/csgo/gameinfo.gi'))) { throw 'Select the Counter-Strike Global Offensive installation root.' }
$packageRoot = $PSScriptRoot
function Contained([string]$root, [string]$relative) {
    $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$full.StartsWith($root.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes installation root.' }
    return $full
}
if ($Restore) {
    if (!$BackupDirectory) { throw 'Supply -BackupDirectory from the install result.' }
    $backup = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $manifest = Get-Content -LiteralPath (Join-Path $backup 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($entry in $manifest) {
        $target = Contained $gameRoot $entry.Path
        if ($entry.Existed) {
            Copy-Item -LiteralPath (Contained $backup $entry.Path) -Destination $target -Force
        } elseif (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    Write-Output 'Original files restored. Empty plugin folders are harmless.'
    return
}
$packageGame = Join-Path $packageRoot 'game'
if (!(Test-Path -LiteralPath $packageGame)) { throw 'Run the installer inside the extracted HumanizedBots package.' }
foreach ($native in @('addons/metamod/BotController.vdf','addons/counterstrikesharp/api/CounterStrikeSharp.API.dll','addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $gameRoot "game/csgo/$native"))) { throw "Required existing component missing: $native" }
}
$backup = Join-Path $gameRoot ('CS2-Bot-Improver/custom-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
$entries = @()
foreach ($file in Get-ChildItem -LiteralPath $packageGame -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($packageRoot,$file.FullName)
    $target = Contained $gameRoot $relative
    $exists = Test-Path -LiteralPath $target
    if ($exists) {
        $copy = Contained $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $copy -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $copy
    }
    $entries += [pscustomobject]@{ Path=$relative; Existed=$exists }
}
# Save the complete rollback manifest before changing any installed file.
$entries | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8
foreach ($entry in $entries) {
    $target = Contained $gameRoot $entry.Path
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Contained $packageRoot $entry.Path) -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $gameRoot "game/csgo/overrides/$Difficulty/botprofile.vpk") -Destination (Join-Path $gameRoot 'game/csgo/overrides/botprofile.vpk') -Force
Write-Output "Installed $Difficulty. Backup: $backup"
Write-Output "Restore: .\Install-CustomBots.ps1 -GameDirectory '$gameRoot' -Restore -BackupDirectory '$backup'"
