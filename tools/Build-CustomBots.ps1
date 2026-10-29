#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [string]$DotnetPath = 'dotnet',
    [string]$PythonPath = 'python',
    [ValidateSet('Low','Medium','High')][string]$Difficulty = 'Medium'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$api = Join-Path $gameRoot 'game/csgo/addons/counterstrikesharp/api'
$controller = Join-Path $gameRoot 'game/csgo/addons/counterstrikesharp/shared/BotControllerApi'
if (!(Test-Path -LiteralPath (Join-Path $api 'CounterStrikeSharp.API.dll'))) { throw 'Installed CounterStrikeSharp API not found.' }
$output = Join-Path $sourceRoot 'dist'
$package = Join-Path $output 'HumanizedBots'
$env:DOTNET_CLI_HOME = Join-Path $sourceRoot '.scratch/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $PythonPath (Join-Path $sourceRoot 'tools/package_difficulty.py') --output (Join-Path $output 'overrides')
if ($LASTEXITCODE) { throw 'Difficulty packaging failed.' }
foreach ($component in @('BotAI','BotAimImprover','BotState','NadeSystem','BotTeamplay')) {
    $project = Join-Path $sourceRoot "addons/counterstrikesharp/plugins/$component/$component.csproj"
    & $DotnetPath build $project -c Release "-p:CounterStrikeSharpApiPath=$api" "-p:BotControllerApiPath=$controller" --ignore-failed-sources -v minimal
    if ($LASTEXITCODE) { throw "Build failed: $component" }
    $destination = Join-Path $package "game/csgo/addons/counterstrikesharp/plugins/$component"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($suffix in @('.dll','.pdb','.deps.json')) {
        Copy-Item -LiteralPath (Join-Path (Split-Path $project -Parent) "bin/Release/net10.0/$component$suffix") -Destination $destination -Force
    }
    $license = Join-Path (Split-Path $project -Parent) 'LICENSE'
    if (!(Test-Path -LiteralPath $license)) { $license = Join-Path $sourceRoot 'LICENSE' }
    Copy-Item -LiteralPath $license -Destination $destination -Force
}
$grenadeDest = Join-Path $package 'game/csgo/addons/counterstrikesharp/plugins/NadeSystem/grenades'
New-Item -ItemType Directory -Path $grenadeDest -Force | Out-Null
Copy-Item -Path (Join-Path $sourceRoot 'addons/counterstrikesharp/data/NadeSystem/grenades/*.json') -Destination $grenadeDest -Force
$cfgDest = Join-Path $package 'game/csgo/cfg'
New-Item -ItemType Directory -Path $cfgDest -Force | Out-Null
Copy-Item -Path (Join-Path $sourceRoot 'cfg/my_bot_*config*.cfg') -Destination $cfgDest -Force
foreach ($level in @('Low','Medium','High')) {
    $levelDest = Join-Path $package "game/csgo/overrides/$level"
    New-Item -ItemType Directory -Path $levelDest -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $output "overrides/$level/botprofile.vpk") -Destination $levelDest -Force
}
Copy-Item -LiteralPath (Join-Path $output "overrides/$Difficulty/botprofile.vpk") -Destination (Join-Path $package 'game/csgo/overrides/botprofile.vpk') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-CustomBots.ps1') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/HUMANIZED.zh-CN.md') -Destination (Join-Path $package '使用说明.md') -Force
$zip = Join-Path $output 'HumanizedBots-Windows.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -Force
Write-Output "Built $zip. Existing native plugins and grenade lineup data are required."
