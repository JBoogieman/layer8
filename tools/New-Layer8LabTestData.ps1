<#
.SYNOPSIS
    Creates (or with -Remove, deletes) Layer8's lab test data: a test application made available to a
    collection, and a fake BitLocker recovery key on a computer object in Active Directory.

.DESCRIPTION
    Lab only. It lets you test Layer8's Install Application window and the "keys found" path of the BitLocker
    Recovery Key lookup without packaging an app or encrypting a disk.

    "Layer8 Test App" installs by writing HKLM\SOFTWARE\Layer8Test (Installed = 1), is detected by that key,
    and uninstalls by deleting it. It has no content, so there's nothing to distribute. Detection is a plain
    registry rule rather than a script, so the client's PowerShell execution policy can't get in the way.

    The fake recovery key has Key ID 4C415938-0000-4000-8000-000000000001 (its first 8 characters spell "LAY8"
    in hex) and the obviously fake password 111111-222222-333333-444444-555555-666666-777777-888888.

    Run it from an elevated PowerShell on a machine with the ConfigMgr console, as an account that can create
    applications and deployments and write to the computer object in AD.

.PARAMETER Collection
    The device collection the test app is made available to. Default: Layer8 Dev.

.PARAMETER Computer
    The computer that gets the fake recovery key. Default: this computer.

.PARAMETER Remove
    Deletes the test app, its deployment and the fake key.

.EXAMPLE
    .\tools\New-Layer8LabTestData.ps1

.EXAMPLE
    .\tools\New-Layer8LabTestData.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$Collection = 'Layer8 Dev',
    [string]$Computer = $env:COMPUTERNAME,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$appName = 'Layer8 Test App'
$keyGuid = [guid]'4C415938-0000-4000-8000-000000000001'
$volumeGuid = [guid]'4C415938-0000-4000-8000-0000000000FF'

# --- ConfigMgr: the test application -------------------------------------------------------------------------

# The console installs its PowerShell module next to its binaries; importing it creates the site drive (LAB:).
Import-Module (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH -Parent) 'ConfigurationManager.psd1')
$siteDrive = Get-PSDrive -PSProvider CMSite -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $siteDrive) { throw 'No ConfigMgr site drive. Open the console once to connect to the site, then run this again.' }

Push-Location "$($siteDrive.Name):"
try {
    $app = Get-CMApplication -Name $appName -Fast
    if ($Remove) {
        if ($app) {
            Get-CMApplicationDeployment -Name $appName | Remove-CMApplicationDeployment -Force
            $app | Remove-CMApplication -Force
            Write-Host "Removed $appName and its deployment."
        }
    }
    elseif ($app) {
        Write-Host "$appName already exists."
    }
    else {
        New-CMApplication -Name $appName -Publisher 'Layer8' -SoftwareVersion '1.0' | Out-Null
        # /reg:64 and -Is64Bit: install and detection both use the 64-bit registry, whichever way the client runs the command.
        $detection = New-CMDetectionClauseRegistryKey -Hive LocalMachine -KeyName 'SOFTWARE\Layer8Test' -Existence -Is64Bit
        Add-CMScriptDeploymentType -ApplicationName $appName -DeploymentTypeName "$appName (script)" `
            -InstallCommand 'cmd.exe /c reg add HKLM\SOFTWARE\Layer8Test /v Installed /d 1 /f /reg:64' `
            -UninstallCommand 'cmd.exe /c reg delete HKLM\SOFTWARE\Layer8Test /f /reg:64' `
            -AddDetectionClause $detection `
            -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn | Out-Null
        New-CMApplicationDeployment -Name $appName -CollectionName $Collection -DeployAction Install -DeployPurpose Available `
            -UserNotification DisplaySoftwareCenterOnly | Out-Null
        Write-Host "Created $appName and made it available to $Collection."
    }
}
finally {
    Pop-Location
}

# --- Active Directory: the fake BitLocker recovery key -------------------------------------------------------

$computerResult = ([adsisearcher]"(&(objectCategory=computer)(cn=$Computer))").FindOne()
if (-not $computerResult) { throw "$Computer isn't in Active Directory." }
$computerEntry = $computerResult.GetDirectoryEntry()

$keyId = $keyGuid.ToString().ToUpperInvariant()
$keySearch = [System.DirectoryServices.DirectorySearcher]::new($computerEntry, "(&(objectClass=msFVE-RecoveryInformation)(cn=*{$keyId}))")
$keySearch.SearchScope = 'OneLevel'
$existingKey = $keySearch.FindOne()

if ($Remove) {
    if ($existingKey) {
        $existingKey.GetDirectoryEntry().DeleteTree()
        Write-Host "Removed the test BitLocker key from $Computer."
    }
}
elseif ($existingKey) {
    Write-Host "The test BitLocker key is already on $Computer."
}
else {
    # Real keys are named <when stored>{<key ID>}; a "+" in the time-zone offset must be escaped in the name.
    $stored = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz').Replace('+', '\+')
    $key = $computerEntry.Children.Add("CN=$($stored){$keyId}", 'msFVE-RecoveryInformation')
    $key.Properties['msFVE-RecoveryPassword'].Value = '111111-222222-333333-444444-555555-666666-777777-888888'
    $key.Properties['msFVE-RecoveryGuid'].Value = [byte[]]$keyGuid.ToByteArray()
    $key.Properties['msFVE-VolumeGuid'].Value = [byte[]]$volumeGuid.ToByteArray()
    $key.CommitChanges()
    Write-Host "Added a test BitLocker key to $Computer (Key ID starts 4C415938)."
}
