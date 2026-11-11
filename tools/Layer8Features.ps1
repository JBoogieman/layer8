<#
.SYNOPSIS
    Layer8's features, and how a package leaves some out. Dot-sourced by Build-Layer8Msi.ps1,
    Build-Layer8Extension.ps1 and Customize-Layer8Cab.ps1 (the released copy of Customize-Layer8Cab.ps1 has this
    file built into it, so it's one download).

.DESCRIPTION
    Each feature is the menu items left out with it, named as the console shows them. Leaving a feature out:

      1. Removes each of its menu items, wherever it is (matched on the name the console shows,
         MnemonicDisplayName, or DisplayName): a device's menu, a collection's, the ribbon.
      2. Removes a menu left with nothing but separators too, and so on up. A file whose whole menu goes is
         dropped.
      3. In a menu that lost items, removes separators left at the start, at the end or next to each other.

    The code stays in the DLLs, but without a menu item the console has no way to start it.
#>

# Feature name, and its menu items as the console shows them. A build stops if an item here isn't on any menu,
# so a renamed menu item can't quietly stay in.
$Layer8Features = [ordered]@{
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
    DriverManager        = 'Driver Grabber', 'Driver Package Importer', 'Driver Legacy Package Importer', 'Dell Driver Pack', 'HP Driver Pack', 'Lenovo Driver Pack'
}

# The menu items of the named features. Throws on a name that isn't a feature, listing the ones that are.
function Get-Layer8FeatureItems([string[]]$Exclude) {
    $unknown = @($Exclude | Where-Object { -not $Layer8Features.Contains($_) })
    if ($unknown) {
        throw "Unknown feature: $($unknown -join ', '). Layer8's features: $(@($Layer8Features.Keys) -join ', ')."
    }
    $items = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($feature in $Exclude) { foreach ($item in @($Layer8Features[$feature])) { [void]$items.Add($item) } }
    , $items
}

# The feature names as Layer8 spells them, for a list typed in any case.
function Get-Layer8FeatureNames([string[]]$Exclude) {
    @(@($Layer8Features.Keys) | Where-Object { $Exclude -contains $_ })
}

# Takes the given menu items out of one menu XML file. Returns 'Dropped' (the whole menu went, the file is deleted),
# 'Changed' or 'Unchanged', and adds each item it found to $Found.
function Remove-Layer8MenuItems([string]$Path, $Items, $Found) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $xml = New-Object Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($Path)

    $changedGroups = New-Object System.Collections.Generic.List[Xml.XmlElement]
    foreach ($action in @($xml.SelectNodes('//ActionDescription'))) {
        $name = @($action.GetAttribute('MnemonicDisplayName'), $action.GetAttribute('DisplayName')) |
            Where-Object { $_ -and $Items.Contains($_) } | Select-Object -First 1
        if (-not $name -or -not (Test-Layer8Attached $action)) { continue }
        [void]$Found.Add($name)
        if (Remove-Layer8Action $action $changedGroups) {
            Remove-Item -LiteralPath $Path
            return 'Dropped'
        }
    }
    if ($changedGroups.Count -eq 0) { return 'Unchanged' }

    foreach ($group in $changedGroups) { if (Test-Layer8Attached $group) { Remove-Layer8StraySeparators $group } }
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($bom)
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try { $xml.Save($writer) } finally { $writer.Close() }
    'Changed'
}

# The Extensions folder a build packages: the repo's own when nothing is left out, otherwise a filtered copy in
# $WorkFolder (the repo's own files never change).
function Get-Layer8ExtensionsFolder {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$WorkFolder,
        [string[]]$Exclude
    )

    $source = Join-Path $Repo 'Extensions'
    if (-not $Exclude) { return $source }
    $items = Get-Layer8FeatureItems $Exclude

    if (Test-Path $WorkFolder) { Remove-Item $WorkFolder -Recurse -Force }
    Copy-Item $source $WorkFolder -Recurse

    $found = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem (Join-Path $WorkFolder 'Actions') -Filter *.xml -File -Recurse) {
        [void](Remove-Layer8MenuItems $file.FullName $items $found)
    }
    # A console node folder with nothing left in it.
    Get-ChildItem (Join-Path $WorkFolder 'Actions') -Directory | Where-Object { -not (Get-ChildItem $_.FullName -Force) } | Remove-Item

    $missing = @($items | Where-Object { -not $found.Contains($_) })
    if ($missing) { throw "tools\Layer8Features.ps1 names menu items no menu has (renamed?): $($missing -join ', ')." }
    return $WorkFolder
}

# Separators and placeholders: entries that aren't menu items.
function Test-Layer8Spacer([Xml.XmlElement]$action) {
    $action.GetAttribute('Class') -in 'Separator', 'None'
}

function Test-Layer8Attached([Xml.XmlNode]$node) {
    while ($node.ParentNode) { $node = $node.ParentNode }
    $node -is [Xml.XmlDocument]
}

# Removes a menu item, and its menu if nothing but separators is left in it, and so on up. Records the menus that
# lost an item in $changedGroups (their <ActionGroups>). True when the file's whole menu is gone.
function Remove-Layer8Action([Xml.XmlElement]$action, $changedGroups) {
    if ([object]::ReferenceEquals($action, $action.OwnerDocument.DocumentElement)) { return $true }
    $groups = $action.ParentNode
    [void]$groups.RemoveChild($action)
    $remaining = @($groups.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] -and $_.LocalName -eq 'ActionDescription' -and -not (Test-Layer8Spacer $_) })
    $owner = $groups.ParentNode
    if ($remaining.Count -eq 0 -and $owner -is [Xml.XmlElement] -and $owner.LocalName -eq 'ActionDescription') {
        return (Remove-Layer8Action $owner $changedGroups)
    }
    $changedGroups.Add($groups)
    return $false
}

# In a menu that lost items: no separator first, last, or right after another one. (Placeholders stay.)
function Remove-Layer8StraySeparators([Xml.XmlElement]$groups) {
    $previousWasSeparator = $true
    $entries = @($groups.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] -and $_.LocalName -eq 'ActionDescription' })
    $kept = New-Object System.Collections.Generic.List[Xml.XmlElement]
    foreach ($entry in $entries) {
        $separator = $entry.GetAttribute('Class') -eq 'Separator'
        if ($separator -and $previousWasSeparator) { [void]$groups.RemoveChild($entry); continue }
        $kept.Add($entry)
        $previousWasSeparator = $separator
    }
    if ($kept.Count -gt 0 -and $kept[$kept.Count - 1].GetAttribute('Class') -eq 'Separator') { [void]$groups.RemoveChild($kept[$kept.Count - 1]) }
}
