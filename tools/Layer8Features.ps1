<#
.SYNOPSIS
    Shared by Build-Layer8Msi.ps1 and Build-Layer8Extension.ps1: the menu XML a build packages, with any features
    left out. Dot-source it; it defines Get-Layer8Features and Get-Layer8ExtensionsFolder.

.DESCRIPTION
    The features and their menu items are in Layer8Features.psd1. Leaving features out works on a copy of the
    repo's Extensions folder (Build\Extensions-<Configuration>); the repo's own files never change:

      1. Each menu item named by a left-out feature is removed, wherever it is (matched on the name the console
         shows, MnemonicDisplayName, or DisplayName).
      2. A menu left with nothing but separators is removed too, and so on up. A file whose whole menu goes is
         deleted.
      3. In a menu that lost items, separators left at the start, at the end or next to each other are removed.
      4. Every item named in Layer8Features.psd1 for a left-out feature has to have been found, or the build
         stops: a menu item renamed later can't quietly stay in.
#>

function Get-Layer8Features {
    Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Layer8Features.psd1')
}

# The Extensions folder a build packages: the repo's own when nothing is left out, otherwise a filtered copy.
function Get-Layer8ExtensionsFolder {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$WorkFolder,
        [string[]]$Exclude
    )

    $source = Join-Path $Repo 'Extensions'
    if (-not $Exclude) { return $source }

    $features = Get-Layer8Features
    $unknown = @($Exclude | Where-Object { -not $features.ContainsKey($_) })
    if ($unknown) {
        throw "Unknown feature: $($unknown -join ', '). Layer8's features: $(($features.Keys | Sort-Object) -join ', ') (tools\Layer8Features.psd1)."
    }
    $items = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($feature in $Exclude) { foreach ($item in @($features[$feature])) { [void]$items.Add($item) } }

    if (Test-Path $WorkFolder) { Remove-Item $WorkFolder -Recurse -Force }
    Copy-Item $source $WorkFolder -Recurse

    $found = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem (Join-Path $WorkFolder 'Actions') -Filter *.xml -File -Recurse) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $xml = New-Object Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.Load($file.FullName)

        $changedGroups = New-Object System.Collections.Generic.List[Xml.XmlElement]
        $dropFile = $false
        foreach ($action in @($xml.SelectNodes('//ActionDescription'))) {
            $name = @($action.GetAttribute('MnemonicDisplayName'), $action.GetAttribute('DisplayName')) |
                Where-Object { $_ -and $items.Contains($_) } | Select-Object -First 1
            if (-not $name -or -not (Test-Layer8Attached $action)) { continue }
            [void]$found.Add($name)
            if (Remove-Layer8Action $action $changedGroups) { $dropFile = $true; break }
        }

        if ($dropFile) { Remove-Item $file.FullName; continue }
        if ($changedGroups.Count -eq 0) { continue }
        foreach ($group in $changedGroups) { if (Test-Layer8Attached $group) { Remove-Layer8StraySeparators $group } }

        $settings = New-Object Xml.XmlWriterSettings
        $settings.Encoding = New-Object Text.UTF8Encoding($bom)
        $writer = [Xml.XmlWriter]::Create($file.FullName, $settings)
        try { $xml.Save($writer) } finally { $writer.Close() }
    }

    # A console node folder with nothing left in it.
    Get-ChildItem (Join-Path $WorkFolder 'Actions') -Directory | Where-Object { -not (Get-ChildItem $_.FullName -Force) } | Remove-Item

    $missing = @($items | Where-Object { -not $found.Contains($_) })
    if ($missing) {
        throw "tools\Layer8Features.psd1 names menu items no menu has (renamed?): $($missing -join ', ')."
    }
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
    $previousWasSpacer = $true
    $entries = @($groups.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] -and $_.LocalName -eq 'ActionDescription' })
    $kept = New-Object System.Collections.Generic.List[Xml.XmlElement]
    foreach ($entry in $entries) {
        $spacer = $entry.GetAttribute('Class') -eq 'Separator'
        if ($spacer -and $previousWasSpacer) { [void]$groups.RemoveChild($entry); continue }
        $kept.Add($entry)
        $previousWasSpacer = $spacer
    }
    if ($kept.Count -gt 0 -and $kept[$kept.Count - 1].GetAttribute('Class') -eq 'Separator') { [void]$groups.RemoveChild($kept[$kept.Count - 1]) }
}
