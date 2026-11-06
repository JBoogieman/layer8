<#
.SYNOPSIS
    Creates (or with -Remove, deletes) Layer8's lab test data: a test application made available to a
    collection, an application that always fails, required for that collection, and a fake BitLocker
    recovery key on a computer object in Active Directory.

.DESCRIPTION
    Lab only. It lets you test Layer8's Install Application window, Retry Failed Devices and Copy Failed
    Devices, and the "keys found" path of the BitLocker Recovery Key lookup without packaging an app or
    encrypting a disk.

    "Layer8 Test App" installs by writing HKLM\SOFTWARE\Layer8Test (Installed = 1), is detected by that key,
    and uninstalls by deleting it. It has no content, so there's nothing to distribute. Detection is a plain
    registry rule rather than a script, so the client's PowerShell execution policy can't get in the way.

    "Layer8 Fail App" is required, with a deadline of now, and its install command exits with 1603, so every
    device in the collection ends up in Error for it. That's the deployment to try Retry Failed Devices on
    (Monitoring > Deployments). Deployment status appears after the next summarization.

    "Layer8 Available Fail App" fails the same way but is only made available: install it from Install
    Application to put a device in Error, then try Retry Failed Devices, which installs Available apps directly.

    "Layer8 Broken Package" is a legacy package whose source share (\\CM01\Layer8Missing$) doesn't exist, sent
    to the site's first distribution point, so the DP lands in Error for it. That's the content to try
    Redistribute Failed Content on (Monitoring > Distribution Status > Content Status). To see a redistribution
    succeed, create the share on CM01 and redistribute again; delete it afterwards so the package breaks again.

    "Layer8 Test Baseline" holds two script CIs: "Layer8 Error CI" throws, so its devices end in Error, and
    "Layer8 Drift CI" returns NonCompliant, so they're non-compliant. It's deployed to the collection daily.
    That's the baseline to try Retry Failed Devices and Re-evaluate Non-compliant Devices on.

    "Layer8 Test TS" and "Layer8 Fail TS" are custom task sequences with one Run Command Line step that exits 0
    or 1, made available to the collection: for Task Sequence Export and, later, retrying task sequences.

    The fake recovery key has Key ID 4C415938-0000-4000-8000-000000000001 (its first 8 characters spell "LAY8"
    in hex) and the obviously fake password 111111-222222-333333-444444-555555-666666-777777-888888.

    Run it from an elevated PowerShell on a machine with the ConfigMgr console, as an account that can create
    applications and deployments and write to the computer object in AD.

.PARAMETER Collection
    The device collection the test app is made available to. Default: Layer8 Dev.

.PARAMETER Computer
    The computer that gets the fake recovery key. Default: this computer.

.PARAMETER Remove
    Deletes the test apps, their deployments and the fake key.

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
$failAppName = 'Layer8 Fail App'
$availableFailAppName = 'Layer8 Available Fail App'
$brokenPackageName = 'Layer8 Broken Package'
$baselineName = 'Layer8 Test Baseline'
$baselineCIs = 'Layer8 Error CI', 'Layer8 Drift CI'
$taskSequences = [ordered]@{ 'Layer8 Test TS' = 0; 'Layer8 Fail TS' = 1 }
$keyGuid = [guid]'4C415938-0000-4000-8000-000000000001'
$volumeGuid = [guid]'4C415938-0000-4000-8000-0000000000FF'

# --- ConfigMgr: the test applications ------------------------------------------------------------------------

function Remove-LabApp([string]$Name) {
    if (-not (Get-CMApplication -Name $Name -Fast)) { return }
    # ConfigMgr won't delete an app that's still deployed, and a deleted deployment can take a moment
    # to disappear, so remove it by name and collection and wait until it's really gone.
    if (Get-CMApplicationDeployment -Name $Name -CollectionName $Collection) {
        Remove-CMApplicationDeployment -Name $Name -CollectionName $Collection -Force
    }
    for ($i = 0; $i -lt 15 -and (Get-CMApplicationDeployment -Name $Name); $i++) { Start-Sleep -Seconds 2 }
    Remove-CMApplication -Name $Name -Force
    Write-Host "Removed $Name and its deployment."
}

function New-LabApp([string]$Name, [string]$KeyName, [string]$InstallCommand, [switch]$Required) {
    if (Get-CMApplication -Name $Name -Fast) {
        Write-Host "$Name already exists."
        return
    }
    New-CMApplication -Name $Name -Publisher 'Layer8' -SoftwareVersion '1.0' | Out-Null
    # /reg:64 and -Is64Bit: install and detection both use the 64-bit registry, whichever way the client runs the command.
    $detection = New-CMDetectionClauseRegistryKey -Hive LocalMachine -KeyName $KeyName -Existence -Is64Bit
    Add-CMScriptDeploymentType -ApplicationName $Name -DeploymentTypeName "$Name (script)" `
        -InstallCommand $InstallCommand `
        -UninstallCommand "cmd.exe /c reg delete HKLM\$KeyName /f /reg:64" `
        -AddDetectionClause $detection `
        -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn | Out-Null
    if ($Required) {
        New-CMApplicationDeployment -Name $Name -CollectionName $Collection -DeployAction Install -DeployPurpose Required `
            -AvailableDateTime (Get-Date) -DeadlineDateTime (Get-Date) -UserNotification DisplaySoftwareCenterOnly | Out-Null
        Write-Host "Created $Name and made it required for $Collection (it fails on purpose)."
    }
    else {
        New-CMApplicationDeployment -Name $Name -CollectionName $Collection -DeployAction Install -DeployPurpose Available `
            -UserNotification DisplaySoftwareCenterOnly | Out-Null
        Write-Host "Created $Name and made it available to $Collection."
    }
}

# --- ConfigMgr: a package whose distribution fails ---------------------------------------------------------

function Remove-LabBrokenPackage {
    $package = Get-CMPackage -Name $brokenPackageName -Fast
    if (-not $package) { return }
    Remove-CMPackage -Id $package.PackageID -Force
    Write-Host "Removed $brokenPackageName."
}

function New-LabBrokenPackage {
    if (Get-CMPackage -Name $brokenPackageName -Fast) {
        Write-Host "$brokenPackageName already exists."
        return
    }
    # The source share doesn't exist, so the distribution manager can't copy it and the DP lands in Error.
    $package = New-CMPackage -Name $brokenPackageName -Path '\\CM01\Layer8Missing$\BrokenPackage'
    $dp = (Get-CMDistributionPoint | Select-Object -First 1).NetworkOSPath.TrimStart('\')
    Start-CMContentDistribution -PackageId $package.PackageID -DistributionPointName $dp
    Write-Host "Created $brokenPackageName and sent it to $dp (its source doesn't exist, so it fails)."
}

# --- ConfigMgr: a baseline that errors and drifts --------------------------------------------------------

function New-LabScriptCI([string]$Name, [string]$Discovery) {
    $ci = Get-CMConfigurationItem -Name $Name
    if ($ci) { return $ci }
    $ci = New-CMConfigurationItem -Name $Name -CreationType WindowsOS
    Add-CMComplianceSettingScript -InputObject $ci -SettingName "$Name setting" -DataType String `
        -DiscoveryScriptLanguage PowerShell -DiscoveryScriptText $Discovery `
        -ValueRule -ExpressionOperator IsEquals -ExpectedValue 'Compliant' -RuleName "$Name rule" `
        -NoncomplianceSeverity Warning -Is64Bit | Out-Null
    Get-CMConfigurationItem -Name $Name
}

function New-LabBaseline {
    if (Get-CMBaseline -Name $baselineName) {
        Write-Host "$baselineName already exists."
        return
    }
    $errorCI = New-LabScriptCI $baselineCIs[0] 'throw "Layer8 test: this setting always errors"'
    $driftCI = New-LabScriptCI $baselineCIs[1] "'NonCompliant'"
    New-CMBaseline -Name $baselineName | Out-Null
    Set-CMBaseline -Name $baselineName -AddOSConfigurationItem $errorCI.CI_ID, $driftCI.CI_ID
    New-CMBaselineDeployment -Name $baselineName -CollectionName $Collection `
        -Schedule (New-CMSchedule -RecurInterval Days -RecurCount 1) | Out-Null
    Write-Host "Created $baselineName (one CI errors, one is non-compliant) and deployed it to $Collection."
}

function Remove-LabBaseline {
    if (Get-CMBaseline -Name $baselineName) {
        Get-CMBaselineDeployment -Name $baselineName | Remove-CMBaselineDeployment -Force
        Remove-CMBaseline -Name $baselineName -Force
        Write-Host "Removed $baselineName and its deployment."
    }
    foreach ($name in $baselineCIs) {
        $ci = Get-CMConfigurationItem -Name $name
        if ($ci) { Remove-CMConfigurationItem -Id $ci.CI_ID -Force }
    }
}

# --- ConfigMgr: a task sequence that works and one that fails ---------------------------------------------

function New-LabTaskSequence([string]$Name, [int]$ExitCode) {
    if (Get-CMTaskSequence -Name $Name -Fast) {
        Write-Host "$Name already exists."
        return
    }
    $ts = New-CMTaskSequence -CustomTaskSequence -Name $Name
    $step = New-CMTSStepRunCommandLine -Name "Exit $ExitCode" -CommandLine "cmd.exe /c exit $ExitCode"
    Add-CMTaskSequenceStep -InputObject $ts -Step $step
    New-CMTaskSequenceDeployment -InputObject $ts -CollectionName $Collection -DeployPurpose Available `
        -Availability Clients | Out-Null
    Write-Host "Created $Name (exits $ExitCode) and made it available to $Collection."
}

function Remove-LabTaskSequence([string]$Name) {
    $ts = Get-CMTaskSequence -Name $Name -Fast
    if (-not $ts) { return }
    Get-CMTaskSequenceDeployment -TaskSequenceId $ts.PackageID | Remove-CMTaskSequenceDeployment -Force
    Remove-CMTaskSequence -TaskSequencePackageId $ts.PackageID -Force
    Write-Host "Removed $Name and its deployment."
}

# The console installs its PowerShell module next to its binaries; importing it creates the site drive (LAB:).
Import-Module (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH -Parent) 'ConfigurationManager.psd1')
$siteDrive = Get-PSDrive -PSProvider CMSite -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $siteDrive) { throw 'No ConfigMgr site drive. Open the console once to connect to the site, then run this again.' }

Push-Location "$($siteDrive.Name):"
try {
    if ($Remove) {
        Remove-LabApp $appName
        Remove-LabApp $failAppName
        Remove-LabApp $availableFailAppName
        Remove-LabBrokenPackage
        Remove-LabBaseline
        foreach ($name in $taskSequences.Keys) { Remove-LabTaskSequence $name }
    }
    else {
        New-LabApp $appName 'SOFTWARE\Layer8Test' 'cmd.exe /c reg add HKLM\SOFTWARE\Layer8Test /v Installed /d 1 /f /reg:64'
        # Never writes its detection key, and 1603 is "fatal error during installation", so it always ends in Error.
        New-LabApp $failAppName 'SOFTWARE\Layer8FailTest' 'cmd.exe /c exit 1603' -Required
        New-LabApp $availableFailAppName 'SOFTWARE\Layer8FailTest2' 'cmd.exe /c exit 1603'
        New-LabBrokenPackage
        New-LabBaseline
        foreach ($name in $taskSequences.Keys) { New-LabTaskSequence $name $taskSequences[$name] }
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
