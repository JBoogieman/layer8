<#
.SYNOPSIS
    Makes a downloaded Layer8 cab fit your site: leaves out features you don't want, signs it with your own
    code-signing certificate, or both. No Visual Studio, no building.

.DESCRIPTION
    Layer8's released cab (Layer8-<version>.cab) has every feature and no signature. This makes a copy next to it:

      -Exclude LAPS, RemoteControl     Those features' menu items aren't in the copy, so no console that installs
                                       it shows them. A menu left empty goes too. The extension's description in
                                       Console Extensions says what it was built without.
      -CertificateThumbprint <...>     The copy, and Layer8's DLLs inside it, are signed with your certificate,
                                       so your site imports it without allowing unsigned extensions.

    Steps:
      1. Unpacks the cab into a folder next to it, with expand.exe (part of Windows).
      2. Takes the left-out features' menu items out of the menu files; a file whose whole menu goes is dropped.
      3. Signs Layer8's DLLs, if asked. A site checks the DLLs inside a signed cab too: a signed cab with
         unsigned DLLs fails to import with "Failed to verify the signature of the cab or its content".
      4. Updates manifest.xml: every changed file's SHA-256 hash, dropped files, and the description.
      5. Packs the cab again with makecab.exe (part of Windows), leaving room for a signature.
      6. Signs the cab, if asked.

    The copy is named for what was done: Layer8-<version>-custom.cab, -signed.cab or -custom-signed.cab. The
    original isn't changed, and the working folder is removed at the end.

    For a signed cab, your site server has to trust the certificate: its root in Trusted Root Certification
    Authorities, and the certificate itself in Trusted Publishers (Local Computer stores). Import it with
    "Allow extension to be unsigned" cleared. An unsigned one needs "Hierarchy approved console extensions can be
    unsigned" (Hierarchy Settings, General tab) and that box ticked.

    The cab keeps its version, and a site won't import the same version of Layer8 twice. Customize the cab
    before the site's first import of that version.

.PARAMETER Path
    The Layer8 cab.

.PARAMETER Exclude
    Features to leave out. Run the script with -Exclude and a name that isn't a feature to see the list, or see
    docs\INSTALL.md (Leaving features out): LAPS, BitLockerKeys, RemoteControl, RestartComputer, InstallUpdates,
    InstallApplication, ClientRepair, RetryFailed, DeleteDeployment, CollectionMembership, QuickDeployment,
    Cleanup, DriverManager.

.PARAMETER CertificateThumbprint
    A code-signing certificate in your CurrentUser\My or LocalMachine\My store, with its private key. Signing is
    Authenticode, SHA-256, with a timestamp.

.PARAMETER TimestampServer
    The timestamp server, so the signature stays valid after the certificate expires. The computer needs
    internet access to reach it.

.EXAMPLE
    .\Customize-Layer8Cab.ps1 -Path .\Layer8-0.3.0.cab -Exclude LAPS, BitLockerKeys, RemoteControl

.EXAMPLE
    .\Customize-Layer8Cab.ps1 -Path .\Layer8-0.3.0.cab -Exclude LAPS -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567

.EXAMPLE
    .\Customize-Layer8Cab.ps1 -Path .\Layer8-0.3.0.cab -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [string[]]$Exclude,
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# Layer8's features and how menu items are left out. In the released copy of this script, Build-Layer8Extension.ps1
# has replaced everything from BEGIN to END with Layer8Features.ps1 itself, so it's one download.
# LAYER8-FEATURES-BEGIN
$featuresScript = Join-Path $PSScriptRoot 'Layer8Features.ps1'
if (-not (Test-Path -LiteralPath $featuresScript)) {
    throw "Layer8Features.ps1 isn't next to this script. Download Customize-Layer8Cab.ps1 from a Layer8 release (that copy has the feature list built in), or run this one from the repo's tools folder."
}
. $featuresScript
# LAYER8-FEATURES-END

# Layer8's permanent console extension ID (the same in every Layer8 cab).
$extensionId = '3b900076-0ea8-46a7-b3b3-57bc7336c50f'

$Exclude = @($Exclude | Where-Object { $_ })
if (-not $Exclude -and -not $CertificateThumbprint) {
    throw "Nothing to do: give -Exclude (features to leave out: $(@($Layer8Features.Keys) -join ', ')), -CertificateThumbprint, or both."
}
$cab = (Resolve-Path -LiteralPath $Path).ProviderPath

# Checked before anything is unpacked, so a typo fails fast.
$items = $null
if ($Exclude) {
    $items = Get-Layer8FeatureItems $Exclude
    $Exclude = Get-Layer8FeatureNames $Exclude
}
$certificate = $null
if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint -replace '\s', ''
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
    if (-not $certificate) { throw "No code-signing certificate with thumbprint $thumbprint in CurrentUser\My or LocalMachine\My." }
}

$untrustedWarned = $false
function Set-Layer8Signature([string]$file) {
    $signature = Set-AuthenticodeSignature -FilePath $file -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if (-not $signature.SignerCertificate) { throw "Signing $file failed: $($signature.StatusMessage)" }
    # A certificate this computer doesn't trust still signs; the site server is the one that has to trust it.
    if ($signature.Status -ne 'Valid' -and -not $script:untrustedWarned) {
        Write-Warning "Signed, but this computer doesn't trust the certificate: $($signature.StatusMessage)"
        $script:untrustedWarned = $true
    }
}
function Get-Sha256([string]$file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }

$folder = Split-Path $cab
$baseName = [IO.Path]::GetFileNameWithoutExtension($cab)
$work = Join-Path $folder "$baseName-customizing"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

# 1. Unpack. Every file in a Layer8 cab sits at its root.
$expand = Join-Path $env:SystemRoot 'System32\expand.exe'
$output = & $expand $cab -F:* $work 2>&1
$manifestPath = Join-Path $work 'manifest.xml'
if ($LASTEXITCODE -or -not (Test-Path $manifestPath)) { throw "Unpacking $cab found no manifest.xml:`n$($output -join "`n")" }

$manifest = New-Object Xml.XmlDocument
$manifest.PreserveWhitespace = $true
$manifest.Load($manifestPath)
$root = $manifest.DocumentElement
if ($root.GetAttribute('ExtensionID') -ne $extensionId) { throw "$cab isn't a Layer8 cab: its extension ID is '$($root.GetAttribute('ExtensionID'))'." }
$version = $root.GetAttribute('Version')
foreach ($entry in @($manifest.SelectNodes('//File'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $work $entry.GetAttribute('Name')))) { throw "manifest.xml lists $($entry.GetAttribute('Name')), but it isn't in the cab." }
}

# Takes a file out of the manifest, and its console node's deployment if that was its last file.
function Remove-ManifestFile([Xml.XmlElement]$entry) {
    $list = $entry.ParentNode
    [void]$list.RemoveChild($entry)
    if (-not $list.SelectSingleNode('File')) {
        $deployment = $list.ParentNode
        [void]$deployment.ParentNode.RemoveChild($deployment)
    }
}

# 2. Leave features out: only the menu files (one ActionExtensionDeployment per console node).
$changed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
if ($items) {
    $found = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $dropped = 0
    foreach ($entry in @($manifest.SelectNodes('//ActionExtensionDeployment/FileList/File'))) {
        $name = $entry.GetAttribute('Name')
        switch (Remove-Layer8MenuItems (Join-Path $work $name) $items $found) {
            'Dropped' { Remove-ManifestFile $entry; $dropped++ }
            'Changed' { [void]$changed.Add($name) }
        }
    }
    $missing = @($items | Where-Object { -not $found.Contains($_) })
    if ($missing) { Write-Warning "Not on any menu in this cab (left out already?): $($missing -join ', ')" }

    # The description says what's been left out, including by an earlier run.
    $description = $root.GetAttribute('Description')
    $already = @()
    if ($description -match '^(.*?) Built without: (.*)\.$') { $description = $Matches[1]; $already = $Matches[2] -split ',\s*' }
    $without = Get-Layer8FeatureNames (@($already) + @($Exclude))
    $root.SetAttribute('Description', "$description Built without: $($without -join ', ').")
}

# 3. Sign the DLLs.
$dllNames = @($manifest.SelectNodes('//File') | ForEach-Object { $_.GetAttribute('Name') } | Where-Object { $_ -like '*.dll' } | Sort-Object -Unique)
if ($certificate) {
    if (-not $dllNames) { throw 'manifest.xml lists no DLLs.' }
    foreach ($name in $dllNames) { Set-Layer8Signature (Join-Path $work $name); [void]$changed.Add($name) }
}

# 4. New hashes for every changed file. The console rejects a manifest that lists the same hash twice.
foreach ($entry in @($manifest.SelectNodes('//File'))) {
    $name = $entry.GetAttribute('Name')
    if ($changed.Contains($name)) { $entry.SelectSingleNode('Hash').InnerText = Get-Sha256 (Join-Path $work $name) }
}
$repeated = @($manifest.SelectNodes('//File') | Group-Object { $_.SelectSingleNode('Hash').InnerText } | Where-Object Count -gt 1)
if ($repeated) { throw "These files ended up the same, which the console rejects: $(($repeated | ForEach-Object { ($_.Group | ForEach-Object { $_.GetAttribute('Name') }) -join ' = ' }) -join '; ')" }
$settings = New-Object Xml.XmlWriterSettings
$settings.Encoding = New-Object Text.UTF8Encoding($false)
$writer = [Xml.XmlWriter]::Create($manifestPath, $settings)
try { $manifest.Save($writer) } finally { $writer.Close() }

# 5. Pack it again: the manifest and the files it lists, at the root of the cab.
$suffix = @($(if ($items) { 'custom' }), $(if ($certificate) { 'signed' })) | Where-Object { $_ }
$newName = "$baseName-$($suffix -join '-').cab"
$newCab = Join-Path $folder $newName
if (Test-Path $newCab) { Remove-Item $newCab -Force }
$names = @($manifest.SelectNodes('//File') | ForEach-Object { $_.GetAttribute('Name') } | Sort-Object -Unique)
$ddf = Join-Path $work 'Layer8.ddf'
$lines = @(
    '.Option Explicit'
    ".Set CabinetNameTemplate=$newName"
    ".Set DiskDirectoryTemplate=`"$folder`""
    ".Set RptFileName=`"$(Join-Path $work 'Layer8.rpt')`""
    ".Set InfFileName=`"$(Join-Path $work 'Layer8.inf')`""
    '.Set Cabinet=on'
    '.Set Compress=on'
    '.Set CompressionType=MSZIP'
    '.Set MaxDiskSize=0'
    '.Set CabinetFileCountThreshold=0'
    '.Set FolderFileCountThreshold=0'
    '.Set FolderSizeThreshold=0'
    '.Set MaxCabinetSize=0'
    '.Set MaxDiskFileCount=0'
    # Room for an Authenticode signature. Without it Set-AuthenticodeSignature can't sign the cab.
    '.Set ReservePerCabinetSize=6144'
)
$lines += "`"$manifestPath`" `"manifest.xml`""
foreach ($name in $names) { $lines += "`"$(Join-Path $work $name)`" `"$name`"" }
Set-Content -Path $ddf -Value $lines -Encoding ASCII

$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
$output = & $makecab /F $ddf 2>&1
if ($LASTEXITCODE -or -not (Test-Path $newCab)) { throw "makecab failed:`n$($output -join "`n")" }

# 6. Sign the cab.
if ($certificate) { Set-Layer8Signature $newCab }
Remove-Item $work -Recurse -Force

Write-Host ''
Write-Host "Customized Layer8 $version"
Write-Host "  $newCab"
Write-Host "  SHA-256 $(Get-Sha256 $newCab)"
if ($items) { Write-Host "  Left out: $($Exclude -join ', ') ($dropped menu files dropped, $(@($changed | Where-Object { $_ -notlike '*.dll' }).Count) changed)" }
if ($certificate) {
    Write-Host "  Cab and $($dllNames.Count) DLLs signed by $($certificate.Subject)"
    Write-Host "  Import it with 'Allow extension to be unsigned' cleared. The site server has to trust the certificate."
}
else {
    Write-Host "  Unsigned: import it with 'Allow extension to be unsigned' ticked (needs 'Hierarchy approved console extensions can be unsigned')."
}
