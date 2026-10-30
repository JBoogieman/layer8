<#
.SYNOPSIS
    Builds Layer8 and loads it into the ConfigMgr console on this machine (developer install).

.DESCRIPTION
    Does what the old Quick Tools MSI did, straight from the repo:
      - copies Layer8's DLLs and the libraries they use into the console's bin folder
      - copies the menu (Actions) and window (Forms) XML into AdminConsole\XmlStorage\Extensions
      - records every file it placed in %ProgramData%\Layer8\install-manifest.txt,
        so Uninstall-Layer8Dev.ps1 can remove exactly those files and nothing else
      - removes files an earlier install placed that this build no longer ships
        (for example a feature that was deleted), so the console never loads leftovers.

    Run it from an elevated PowerShell with the ConfigMgr console closed. The console only
    picks up extensions at startup, so reopen it afterwards.

    Layer8 is an old-style console extension. On a site where Hierarchy Settings has
    "Only allow console extensions that are approved for the hierarchy" ticked, the console
    ignores it until that box is cleared.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER NoBuild
    Skip the build and install whatever is already in Build\<Configuration>.

.EXAMPLE
    .\tools\Install-Layer8Dev.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not $env:SMS_ADMIN_UI_PATH) { throw 'ConfigMgr console not found on this machine (SMS_ADMIN_UI_PATH is not set).' }
$bin = Split-Path $env:SMS_ADMIN_UI_PATH          # ...\AdminConsole\bin
$console = Split-Path $bin                         # ...\AdminConsole

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: it writes into Program Files.'
}
if (Get-Process Microsoft.ConfigurationManagement -ErrorAction SilentlyContinue) {
    throw 'Close the ConfigMgr console first: it locks the DLLs this script replaces.'
}

if (-not $NoBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = if (Test-Path $vswhere) { & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
    if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio with the .NET desktop development workload.' }

    $projects = 'IniFileParser\INIFileParser.csproj',
                'Layer8\Layer8.csproj',
                'Layer8.Device\Layer8.Device.csproj',
                'Layer8.DriverManager\Layer8.DriverManager.csproj'
    foreach ($p in $projects) {
        Write-Host "Building $p ($Configuration)"
        & $msbuild (Join-Path $repo $p) -restore -t:Build -p:RestorePackagesConfig=true -p:Configuration=$Configuration "-p:SolutionDir=$repo/" -nologo -v:quiet
        if ($LASTEXITCODE) { throw "Build failed: $p" }
    }
}

$build = Join-Path $repo "Build\$Configuration"
$libraries = 'Layer8.dll', 'Layer8.Device.dll', 'Layer8.DriverManager.dll',
             'Layer8.IniFileParser.dll', 'log4net.dll', 'Layer8.log4net.config', 'ByteSize.dll',
             'Microsoft.Deployment.Compression.dll', 'Microsoft.Deployment.Compression.Cab.dll', 'SmartThreadPool.dll'

$placed = New-Object System.Collections.Generic.List[string]
foreach ($name in $libraries) {
    $source = Join-Path $build $name
    if (-not (Test-Path $source)) { throw "Missing build output: $source (build first, or drop -NoBuild)" }
    Copy-Item $source $bin -Force
    $placed.Add((Join-Path $bin $name))
}
Get-ChildItem $build -Filter 'Layer8*.pdb' | ForEach-Object {
    Copy-Item $_.FullName $bin -Force
    $placed.Add((Join-Path $bin $_.Name))
}

$extensionSource = Join-Path $repo 'Extensions'
$extensionTarget = Join-Path $console 'XmlStorage\Extensions'
Get-ChildItem $extensionSource -Recurse -File | ForEach-Object {
    $target = Join-Path $extensionTarget $_.FullName.Substring($extensionSource.Length + 1)
    New-Item (Split-Path $target) -ItemType Directory -Force | Out-Null
    Copy-Item $_.FullName $target -Force
    $placed.Add($target)
}

$manifestFolder = Join-Path $env:ProgramData 'Layer8'
$manifest = Join-Path $manifestFolder 'install-manifest.txt'
New-Item $manifestFolder -ItemType Directory -Force | Out-Null
# Clean up anything an earlier install placed that this build no longer ships.
if (Test-Path $manifest) {
    foreach ($old in Get-Content $manifest) {
        if (-not $old -or $placed -contains $old) { continue }
        if (-not $old.StartsWith($console + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (Test-Path $old) { Remove-Item $old -Force; Write-Host "Removed leftover from an earlier install: $old" }
    }
}
$placed | Sort-Object -Unique | Set-Content $manifest

Write-Host "Installed $($placed.Count) files into $console"
Write-Host "Manifest: $manifest"
Write-Host 'Open the ConfigMgr console to load Layer8.'
