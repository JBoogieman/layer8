<#
.SYNOPSIS
    Builds Layer8's MSI, installs it and reopens the ConfigMgr console: the whole inner loop in one command.
.PARAMETER NoOpen
    Don't start the console afterwards.
.EXAMPLE
    .\tools\Dev-Loop.ps1
#>
[CmdletBinding()]
param([switch]$NoOpen)

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

if (-not $NoOpen) {
    # SMS_ADMIN_UI_PATH is ...\AdminConsole\bin\i386; the console's exe is one folder up.
    Start-Process (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'Microsoft.ConfigurationManagement.exe')
}
