<#
.SYNOPSIS
    Builds Layer8's MSI, installs it and reopens the ConfigMgr console: the whole inner loop in one command.
.PARAMETER NoOpen
    Don't start the console afterwards.
.PARAMETER WithDevTools
    Also copy the developer-only items in Extensions.Dev (the Probe Selection item) into the console. Without it,
    any copies a previous run made are removed.
.EXAMPLE
    .\tools\Dev-Loop.ps1
#>
[CmdletBinding()]
param([switch]$NoOpen, [switch]$WithDevTools)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated PowerShell.' }
if (Get-Process Microsoft.ConfigurationManagement -ErrorAction SilentlyContinue) { throw 'Close every ConfigMgr console first.' }

& (Join-Path $PSScriptRoot 'Build-Layer8Msi.ps1')

$msi = Join-Path $repo 'Build\Installer\Release\Layer8.msi'
$log = Join-Path $env:TEMP 'Layer8-install.log'
$p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /l*v `"$log`"" -Wait -PassThru
switch ($p.ExitCode) {
    0       { Write-Host 'Installed.' -ForegroundColor Green }
    3010    { Write-Warning 'Installed, but a file was in use. Close every console and run this again.' }
    default { throw "msiexec failed with exit code $($p.ExitCode). Details: $log" }
}

# Developer-only items: copied in by hand, so the MSI neither installs nor removes them.
$actions = Join-Path (Split-Path (Split-Path $env:SMS_ADMIN_UI_PATH)) 'XmlStorage\Extensions\Actions'
$probe = Join-Path $repo 'Extensions.Dev\Layer8Probe.xml'
$nodes = Get-Content (Join-Path $repo 'Extensions.Dev\nodes.txt') | Where-Object { $_ -match '^[0-9a-fA-F-]{36}' } | ForEach-Object { ($_ -split '\s+')[0] }
foreach ($node in $nodes) {
    $target = Join-Path $actions "$node\Layer8Probe.xml"
    if ($WithDevTools) {
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $probe $target -Force
    }
    elseif (Test-Path $target) {
        Remove-Item $target -Force
    }
}
if ($WithDevTools) { Write-Host "Dev tools: Probe Selection added on $($nodes.Count) nodes." }

if (-not $NoOpen) {
    # SMS_ADMIN_UI_PATH is ...\AdminConsole\bin\i386; the console's exe is one folder up.
    Start-Process (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'Microsoft.ConfigurationManagement.exe')
}
