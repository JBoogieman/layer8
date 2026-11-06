<#
.SYNOPSIS
    Packages Layer8 as an approved console extension: Build\Extension\Layer8-<version>.cab, the file you import
    under Administration > Updates and Servicing > Console Extensions.

.DESCRIPTION
    Two ways to put Layer8 in a console, built from the same files:

      Layer8.msi (Build-Layer8Msi.ps1)    Copies the DLLs and XML straight into the console's folders. Quick to
                                          install, and what tools\Dev-Loop.ps1 uses. A hierarchy with "Only allow
                                          console extensions that are approved for the hierarchy" turned on
                                          ignores extensions installed this way.
      Layer8-<version>.cab (this script)  Imported once on the site, approved there, then installed from the
                                          console by each admin. Works with that setting on.

    Put one or the other on a console, never both: on a hierarchy that allows both, every Layer8 menu would
    show twice.

    This script doesn't build: run tools\Build-Layer8Msi.ps1 first (it builds the DLLs both packages use).

    What's in the cab:
      manifest.xml   Layer8's extension ID, name, version and author, and every file with its SHA-256 hash.
      *.xml          The menu XML (one ActionExtensionDeployment per console node) and the dialog XML.
      *.dll          Layer8's four DLLs.

    The console takes the files from the cab's one folder, and rejects a manifest that lists the same hash twice.
    Layer8's device and collection-member menus share file names, and four of those files are byte-for-byte the
    same. So a file whose name is used on more than one node gets the node's first GUID block added to its name
    (Layer8Devices.ed9dee86.xml), and a file whose content is used more than once gets a comment naming its node.
    The repo's Extensions folder stays as it is; only the cab's copies change.

.PARAMETER Configuration
    Release (default) or Debug: which build's DLLs to package.

.PARAMETER CertificateThumbprint
    Signs the cab with this code-signing certificate from your certificate store (Authenticode, SHA-256, with a
    timestamp). Without it the cab is unsigned, and a site imports it only with "Hierarchy approved console
    extensions can be unsigned" turned on (Hierarchy Settings, General tab).

.PARAMETER TimestampServer
    The timestamp server used when signing, so the signature stays valid after the certificate expires.

.EXAMPLE
    .\tools\Build-Layer8Msi.ps1
    .\tools\Build-Layer8Extension.ps1

.EXAMPLE
    .\tools\Build-Layer8Extension.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# Layer8's permanent identity as a console extension: the site recognises a newer cab as the same extension by
# this ID. Every Layer8 cab ever built must keep it. Never change it.
$extensionId = '3b900076-0ea8-46a7-b3b3-57bc7336c50f'

$name = 'Layer8'
$author = 'JBoogieman'
$description = 'Right-click tools for the Configuration Manager console: client actions, remote tools, deployment retries and collection tools. Open source (MIT).'

# The same DLLs Layer8.msi installs (Layer8.Setup\Package.wxs).
$dlls = 'Layer8.dll', 'Layer8.Device.dll', 'Layer8.DriverManager.dll', 'Layer8.IniFileParser.dll'

$buildDir = Join-Path $repo "Build\$Configuration"
foreach ($dll in $dlls) {
    if (-not (Test-Path (Join-Path $buildDir $dll))) { throw "$dll isn't in $buildDir. Run .\tools\Build-Layer8Msi.ps1 first." }
}
$version = [Version](Get-Item (Join-Path $buildDir 'Layer8.dll')).VersionInfo.FileVersion

$outDir = Join-Path $repo 'Build\Extension'
$contents = Join-Path $outDir "Layer8-$($version.ToString(3))"
if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
New-Item -ItemType Directory -Force -Path $contents | Out-Null

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# Writes a copy of an XML file with a comment naming the console node, so its hash differs from the other copies.
function Copy-WithNodeComment([string]$source, [string]$target, [string]$node) {
    $bytes = [IO.File]::ReadAllBytes($source)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $skip = 0
    if ($bom) { $skip = 3 }
    $text = [Text.Encoding]::UTF8.GetString($bytes, $skip, $bytes.Length - $skip)
    $comment = "<!-- Layer8: this copy is for console node $node -->"
    if ($text.StartsWith('<?xml')) {
        $end = $text.IndexOf('?>') + 2
        $text = $text.Insert($end, "`r`n$comment")
    }
    else {
        $text = "$comment`r`n$text"
    }
    [IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding($bom)))
}

# 1. The menu XML, node by node.
$guidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$actions = @(foreach ($nodeDir in Get-ChildItem (Join-Path $repo 'Extensions\Actions') -Directory | Sort-Object Name) {
    if ($nodeDir.Name -notmatch $guidPattern) { throw "Extensions\Actions\$($nodeDir.Name) isn't named after a console node GUID." }
    foreach ($file in Get-ChildItem $nodeDir.FullName -Filter *.xml -File | Sort-Object Name) {
        [pscustomobject]@{ Node = $nodeDir.Name.ToLowerInvariant(); Source = $file.FullName; Name = $file.Name; Hash = Get-Sha256 $file.FullName; CabName = $null }
    }
})
$sharedNames = @($actions | Group-Object Name | Where-Object Count -gt 1 | ForEach-Object Name)
$sharedHashes = @($actions | Group-Object Hash | Where-Object Count -gt 1 | ForEach-Object Name)

foreach ($action in $actions) {
    $action.CabName = $action.Name
    if ($sharedNames -contains $action.Name) {
        $action.CabName = '{0}.{1}.xml' -f [IO.Path]::GetFileNameWithoutExtension($action.Name), $action.Node.Substring(0, 8)
    }
    $target = Join-Path $contents $action.CabName
    if ($sharedHashes -contains $action.Hash) { Copy-WithNodeComment $action.Source $target $action.Node }
    else { Copy-Item -LiteralPath $action.Source -Destination $target }
}

# 2. The dialog XML and the DLLs.
$forms = @(Get-ChildItem (Join-Path $repo 'Extensions\Forms') -Filter *.xml -File | Sort-Object Name)
foreach ($form in $forms) { Copy-Item -LiteralPath $form.FullName -Destination (Join-Path $contents $form.Name) }
foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $buildDir $dll) -Destination (Join-Path $contents $dll) }

# 3. Every file in the cab needs its own name and its own hash.
$packaged = @(Get-ChildItem $contents -File)
$expected = $actions.Count + $forms.Count + $dlls.Count
if ($packaged.Count -ne $expected) { throw "Expected $expected files in the cab but there are $($packaged.Count): two would share a name." }
$hashes = @{}
foreach ($file in $packaged) { $hashes[$file.Name] = Get-Sha256 $file.FullName }
$repeated = @($hashes.GetEnumerator() | Group-Object Value | Where-Object Count -gt 1)
if ($repeated) { throw "These files have the same content, which the console rejects: $(($repeated | ForEach-Object { $_.Group.Name -join ' = ' }) -join '; ')" }

# 4. manifest.xml.
function Write-FileList($writer, [string[]]$fileNames) {
    $writer.WriteStartElement('FileList')
    foreach ($fileName in $fileNames) {
        $writer.WriteStartElement('File')
        $writer.WriteAttributeString('Name', $fileName)
        $writer.WriteStartElement('Hash')
        $writer.WriteAttributeString('Algorithm', 'sha256')
        $writer.WriteString($hashes[$fileName])
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
}

$manifest = Join-Path $contents 'manifest.xml'
$settings = New-Object Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object Text.UTF8Encoding($false)
$writer = [Xml.XmlWriter]::Create($manifest, $settings)
try {
    $writer.WriteStartElement('CustomExtensionManifest')
    $writer.WriteAttributeString('ExtensionID', $extensionId)
    $writer.WriteAttributeString('Name', $name)
    $writer.WriteAttributeString('Description', $description)
    $writer.WriteAttributeString('Version', $version.ToString())
    $writer.WriteAttributeString('Author', $author)
    $writer.WriteStartElement('Deployments')
    foreach ($node in $actions | Group-Object Node | Sort-Object Name) {
        $writer.WriteStartElement('ActionExtensionDeployment')
        $writer.WriteAttributeString('ParentNode', $node.Name)
        Write-FileList $writer @($node.Group | ForEach-Object CabName)
        $writer.WriteEndElement()
    }
    # The dialogs, and the DLLs every menu and dialog loads (each file once: a hash may appear only once).
    $writer.WriteStartElement('FormExtensionDeployment')
    Write-FileList $writer (@($forms | ForEach-Object Name) + $dlls)
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
}
finally {
    $writer.Close()
}

# 5. The cab, with makecab (part of Windows). The manifest and every file sit at the root of the cab.
$cabName = "Layer8-$($version.ToString(3)).cab"
$cab = Join-Path $outDir $cabName
if (Test-Path $cab) { Remove-Item $cab -Force }
$ddf = Join-Path $outDir 'Layer8.ddf'
$lines = @(
    '.Option Explicit'
    ".Set CabinetNameTemplate=$cabName"
    ".Set DiskDirectoryTemplate=`"$outDir`""
    ".Set RptFileName=`"$(Join-Path $outDir 'Layer8.rpt')`""
    ".Set InfFileName=`"$(Join-Path $outDir 'Layer8.inf')`""
    '.Set Cabinet=on'
    '.Set Compress=on'
    '.Set CompressionType=MSZIP'
    '.Set MaxDiskSize=0'
    '.Set CabinetFileCountThreshold=0'
    '.Set FolderFileCountThreshold=0'
    '.Set FolderSizeThreshold=0'
    '.Set MaxCabinetSize=0'
    '.Set MaxDiskFileCount=0'
)
$lines += "`"$manifest`" `"manifest.xml`""
foreach ($file in $packaged | Sort-Object Name) { $lines += "`"$($file.FullName)`" `"$($file.Name)`"" }
Set-Content -Path $ddf -Value $lines -Encoding ASCII

$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
$output = & $makecab /F $ddf 2>&1
if ($LASTEXITCODE -or -not (Test-Path $cab)) { throw "makecab failed:`n$($output -join "`n")" }
Remove-Item $ddf, (Join-Path $outDir 'Layer8.rpt'), (Join-Path $outDir 'Layer8.inf') -Force -ErrorAction SilentlyContinue

# 6. Sign, if asked.
$signedBy = $null
if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint -replace '\s', ''
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
    if (-not $certificate) { throw "No code-signing certificate with thumbprint $thumbprint in CurrentUser\My or LocalMachine\My." }
    $signature = Set-AuthenticodeSignature -FilePath $cab -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if (-not $signature.SignerCertificate) { throw "Signing failed: $($signature.StatusMessage)" }
    # A certificate whose root this computer doesn't trust (a self-signed lab certificate) still signs; the
    # site server is the one that has to trust it.
    if ($signature.Status -ne 'Valid') { Write-Warning "Signed, but this computer doesn't trust the certificate: $($signature.StatusMessage)" }
    $signedBy = $certificate.Subject
}

Write-Host ''
Write-Host "Built Layer8 $version as a console extension"
Write-Host "  $cab"
Write-Host "  SHA-256 $(Get-Sha256 $cab)"
Write-Host "  $($actions.Count) menu files on $(@($actions | Group-Object Node).Count) console nodes, $($forms.Count) dialog files, $($dlls.Count) DLLs"
if ($signedBy) { Write-Host "  Signed by $signedBy" }
else { Write-Host "  Unsigned: the site needs 'Hierarchy approved console extensions can be unsigned' turned on to import it." }
Write-Host "  What went in: $contents"
