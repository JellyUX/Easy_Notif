# Builds the plugin and deploys it to the test Jellyfin container.
# Run from the project root: .\docker\scripts\deploy-plugin.ps1
#
# Jellyfin loads a plugin from a "<Name>_<Version>" directory in preference to a bare-name one,
# and marks the bare-name copy "Superseded" whenever a catalogue-installed copy is present. So a
# plain DLL copy is silently ignored once the plugin has ever been installed from the manifest.
# This script therefore removes any catalogue copy and writes a valid, Active meta.json next to
# the freshly built DLL so the dev build is the one that loads.

param(
    [string]$Configuration = "Release",
    [string]$PluginName = "Jellyfin.Plugin.EasyNotif"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$pluginsDir = Join-Path $scriptDir "..\plugins"
$pluginOutputDir = Join-Path $pluginsDir $PluginName
$buildOutputDir = "$projectRoot\src\$PluginName\bin\$Configuration\net9.0"
$builtDll = "$buildOutputDir\$PluginName.dll"

Write-Host "Building $PluginName ($Configuration)..."
dotnet build "$projectRoot\src\$PluginName\$PluginName.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

Write-Host "Removing any catalogue-installed copy..."
Get-ChildItem -Path $pluginsDir -Directory -Filter "Easy Notif_*" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  - $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }

Write-Host "Deploying the dev build..."
New-Item -ItemType Directory -Force $pluginOutputDir | Out-Null
Copy-Item $builtDll $pluginOutputDir -Force

# The plugin bundles its own copy of Serilog (not provided by the Jellyfin host, unlike
# Newtonsoft.Json / Microsoft.Extensions.Http - see the Phase 6 build: commit for why).
Get-ChildItem -Path $buildOutputDir -Filter "Serilog*.dll" | ForEach-Object {
    Write-Host "  + $($_.Name)"
    Copy-Item $_.FullName $pluginOutputDir -Force
}

$version = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $builtDll)).Version.ToString()
$meta = [ordered]@{
    category   = "General"
    changelog  = ""
    description = "Internal email notification service: scheduled new-media newsletters, personalised weekly watch recaps and manual admin emails."
    guid       = "7a27339e-e774-4969-9755-8cd213dcc5e7"
    name       = "Easy Notif"
    overview   = ""
    owner      = "Samuellct"
    targetAbi  = "10.11.10.0"
    timestamp  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.0000000Z")
    version    = $version
    status     = "Active"
    autoUpdate = $false
    assemblies = @()
}
# Write without a BOM: Jellyfin's PluginManager parses meta.json with System.Text.Json, which
# rejects a leading U+FEFF ("'0xEF' is an invalid start of a value"). PS 5.1 "-Encoding utf8"
# emits a BOM, so go through UTF8Encoding($false) explicitly.
$metaPath = Join-Path $pluginOutputDir "meta.json"
[System.IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "  meta.json written (version $version, status Active)"

Write-Host "Restarting easynotif-test container..."
docker compose -f "$scriptDir\..\docker-compose.yml" restart jellyfin

Write-Host "Done. Jellyfin restarting at http://localhost:8099"
