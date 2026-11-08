<#
.SYNOPSIS
    Lab only: opens what Layer8 needs on a test client. WMI (135 and dynamic RPC), file sharing (445),
    Remote Desktop, Remote Assistance, WinRM for the PowerShell session, and the remote management rules
    Computer Management, Event Viewer and Services use. Offering Remote Assistance also needs the
    Configure Offer Remote Assistance policy, which this doesn't set.

.DESCRIPTION
    Run it elevated on the test client itself. On a managed network Group Policy usually does this; in a lab
    a script is quicker and repeatable.

.EXAMPLE
    .\tools\Prepare-Layer8LabClient.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$groups = 'Windows Management Instrumentation (WMI)', 'File and Printer Sharing', 'Remote Desktop', 'Remote Assistance',
          'Remote Event Log Management', 'Remote Service Management', 'COM+ Network Access'
foreach ($group in $groups) {
    Enable-NetFirewallRule -DisplayGroup $group
    Write-Host "Allowed: $group"
}
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance' -Name fAllowToGetHelp -Value 1
# SkipNetworkProfileCheck: a lab client on a network Windows calls Public still gets WinRM (local subnet only).
Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
Write-Host 'Remote Desktop and WinRM are on. Offering Remote Assistance also needs the Configure Offer Remote Assistance policy.'
