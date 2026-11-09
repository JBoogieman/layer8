# Layer8's features, for leaving some out of a build:
#
#   .\tools\Build-Layer8Msi.ps1 -Exclude LAPS, BitLockerKeys
#   .\tools\Build-Layer8Extension.ps1 -Exclude LAPS, BitLockerKeys
#
# Each feature is the menu items left out with it, named as the console shows them. An item is left out of every
# menu it's on (a device's, a collection's, the ribbon). A menu left empty goes too. The code stays in the DLLs,
# but without a menu item the console has no way to start it.
#
# A build stops if an item named here isn't on any menu, so a renamed menu item can't quietly stay in.
@{
    LAPS                 = 'LAPS Password'
    BitLockerKeys        = 'BitLocker Recovery Key'
    RemoteControl        = 'Remote Desktop', 'Remote Assistance', 'PowerShell Session'
    RestartComputer      = 'Restart Computer', 'Cancel Restart'
    InstallUpdates       = 'Install Missing Updates'
    InstallApplication   = 'Install Application'
    ClientRepair         = 'Repair Client', 'Reset Policy', 'Turn Off Provisioning Mode'
    RetryFailed          = 'Retry Failed Devices', 'Retry All Failed Deployments', 'Re-evaluate Non-compliant Devices', 'Redistribute Failed Content'
    DeleteDeployment     = 'Delete'
    CollectionMembership = 'Add Resources List', 'Remove Resources List', 'Add Selected Items to Existing Device Collections'
    QuickDeployment      = 'Quick Deployment'
    Cleanup              = 'Clean Software Update Groups', 'Remove Retired Content'
    DriverManager        = 'Driver Grabber', 'Driver Package Importer', 'Driver Legacy Package Importer', 'Dell Driver Pack', 'HP Driver Pack'
}
