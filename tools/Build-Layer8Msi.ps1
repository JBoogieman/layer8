<#
.SYNOPSIS
    Builds Layer8 and its installer: Build\Installer\<Configuration>\Layer8.msi.

.DESCRIPTION
    1. Builds the Layer8 projects (Release unless you say otherwise) into Build\<Configuration>.
    2. Builds Layer8.Setup\Layer8.Setup.wixproj, which packs those DLLs and the Extensions XML into one MSI.
       The first run downloads the WiX Toolset SDK from nuget.org.

    It only builds. Installing is up to you:
        msiexec /i Build\Installer\Release\Layer8.msi /qn          (close the ConfigMgr console first)
        msiexec /x Build\Installer\Release\Layer8.msi /qn          (uninstall)

    Don't mix this with tools\Install-Layer8Dev.ps1 on the same machine: both put the same files in the
    console's folders. Run tools\Uninstall-Layer8Dev.ps1 before installing the MSI.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    .\tools\Build-Layer8Msi.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = if (Test-Path $vswhere) { & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio with the .NET desktop development workload.' }

$projects = 'IniFileParser\INIFileParser.csproj',
            'Layer8\Layer8.csproj',
            'Layer8.Device\Layer8.Device.csproj',
            'Layer8.DriverManager\Layer8.DriverManager.csproj',
            'Layer8.Setup\Layer8.Setup.wixproj'
foreach ($p in $projects) {
    Write-Host "Building $p ($Configuration)"
    & $msbuild (Join-Path $repo $p) -restore -t:Build -p:RestorePackagesConfig=true -p:Configuration=$Configuration "-p:SolutionDir=$repo/" -nologo -v:minimal
    if ($LASTEXITCODE) { throw "Build failed: $p" }
}

$msi = Join-Path $repo "Build\Installer\$Configuration\Layer8.msi"
if (-not (Test-Path $msi)) { throw "The build finished but $msi isn't there." }
$version = (Get-Item (Join-Path $repo "Build\$Configuration\Layer8.dll")).VersionInfo.FileVersion
Write-Host ''
Write-Host "Built Layer8 $version"
Write-Host "  $msi"
