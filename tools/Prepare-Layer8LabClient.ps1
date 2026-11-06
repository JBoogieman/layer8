<#
.SYNOPSIS
    Lab only: opens what Layer8 needs on a test client. WMI (135 and dynamic RPC), file sharing (445),
    Remote Desktop, Remote Assistance, and WinRM for the PowerShell session.

.DESCRIPTION
    Run it elevated on the test client itself. On a managed network Group Policy usually does this; in a lab
    a script is quicker and repeatable.

.EXAMPLE
    .\tools\Prepare-Layer8LabClient.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
foreach ($group in 'Windows Management Instrumentation (WMI)', 'File and Printer Sharing', 'Remote Desktop', 'Remote Assistance') {
    Enable-NetFirewallRule -DisplayGroup $group
    Write-Host "Allowed: $group"
}
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance' -Name fAllowToGetHelp -Value 1
Enable-PSRemoting -Force | Out-Null
Write-Host 'Remote Desktop, Remote Assistance offers and WinRM are on.'
