<#
.SYNOPSIS
    Lab only: imports device records that don't exist, to test Layer8 runs at scale without the VMs.

.DESCRIPTION
    Creates L8SCALE001, L8SCALE002 and so on with made-up MAC addresses (02: at the start marks them as
    locally administered, so they can't clash with real hardware) in a collection called Layer8 Scale.
    No such computers exist, so every Layer8 action on them ends in "Not found in DNS" or "Offline":
    what's being tested is the progress window, the devices-at-once limit and the offline check.

.PARAMETER Count
    How many records. Default 60.

.PARAMETER Remove
    Deletes the records and the collection.

.EXAMPLE
    .\tools\New-Layer8ScaleRecords.ps1
.EXAMPLE
    .\tools\New-Layer8ScaleRecords.ps1 -Remove
#>
[CmdletBinding()]
param([int]$Count = 60, [switch]$Remove)

$ErrorActionPreference = 'Stop'
$collectionName = 'Layer8 Scale'
Import-Module (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH -Parent) 'ConfigurationManager.psd1')
$siteDrive = Get-PSDrive -PSProvider CMSite -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $siteDrive) { throw 'No ConfigMgr site drive. Open the console once to connect to the site, then run this again.' }

Push-Location "$($siteDrive.Name):"
try {
    $names = 1..$Count | ForEach-Object { 'L8SCALE{0:D3}' -f $_ }
    if ($Remove) {
        foreach ($name in $names) {
            $device = Get-CMDevice -Name $name -Fast
            if ($device) { Remove-CMDevice -InputObject $device -Force }
        }
        $collection = Get-CMDeviceCollection -Name $collectionName
        if ($collection) { Remove-CMDeviceCollection -InputObject $collection -Force }
        Write-Host "Removed the scale records and $collectionName."
        return
    }

    if (-not (Get-CMDeviceCollection -Name $collectionName)) {
        New-CMDeviceCollection -Name $collectionName -LimitingCollectionName 'All Systems' | Out-Null
    }
    $i = 0
    foreach ($name in $names) {
        $i++
        $mac = '02:4C:38:00:{0:X2}:{1:X2}' -f [int][math]::Floor($i / 256), ($i % 256)
        Import-CMComputerInformation -ComputerName $name -MacAddress $mac -CollectionName $collectionName
    }
    Write-Host "Imported $Count records into $collectionName. Update its membership if they don't show at once."
}
finally {
    Pop-Location
}
