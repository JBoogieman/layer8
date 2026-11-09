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
                                          console by each admin. Works with that setting on. The console only
                                          loads it while AdminConsole\XmlStorage\Extensions\Actions exists (a
                                          stock console has it, empty); see docs\INSTALL.md.

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

    The console installs the cab into AdminConsole\Extensions\<ID>\<version>\ (Actions\<node>, Forms, bin), not into
    AdminConsole\bin, and where it looks for a DLL named in the XML depends on where the name is:
      Menu XML, <Assembly>          The extension's own bin. Layer8's DLLs are found there; the console's own
                                    (icons and "is this enabled?" checks from AdminUI.*.dll) aren't, so the cab's
                                    copies point those at ..\..\..\..\bin\, which is AdminConsole\bin.
      Dialog XML, <Assembly Name>   The extension's own bin too, also for pages that don't name their own DLL.
                                    Left as it is: Layer8's DLLs are found, and the console finds its own.
      Dialog XML, <Page Assembly>   AdminConsole\bin. Layer8's DLLs aren't there, so the cab's copies point
                                    those at ..\Extensions\<ID>\<version>\bin\.
    Both paths are relative, so they work wherever the console is installed. (Found by testing on console
    5.2509; Microsoft doesn't document it.)

    The repo's Extensions folder stays as it is (Layer8.msi puts everything in AdminConsole\bin, where the names
    work as written); only the cab's copies change.

.PARAMETER Configuration
    Release (default) or Debug: which build's DLLs to package.

.PARAMETER CertificateThumbprint
    Signs Layer8's four DLLs and then the cab with this code-signing certificate from your certificate store
    (Authenticode, SHA-256, with a timestamp). A site checks the DLLs inside a signed cab too: a signed cab with
    unsigned DLLs fails to import with "Failed to verify the signature of the cab or its content". The site server
    has to trust the certificate (its root in Trusted Root Certification Authorities, the certificate itself in
    Trusted Publishers).

    Without it nothing is signed, and a site imports the cab only with "Hierarchy approved console extensions can
    be unsigned" turned on (Hierarchy Settings, General tab) and "Allow extension to be unsigned" ticked in the
    import wizard.

.PARAMETER TimestampServer
    The timestamp server used when signing, so the signature stays valid after the certificate expires.

.PARAMETER Exclude
    Features to leave out, by name (tools\Layer8Features.ps1 lists them and their menu items), for example
    -Exclude LAPS, BitLockerKeys. Their menu items aren't in the cab, so no console that installs it shows them.
    The extension's description in the console says what was left out.

.EXAMPLE
    .\tools\Build-Layer8Msi.ps1
    .\tools\Build-Layer8Extension.ps1

.EXAMPLE
    .\tools\Build-Layer8Extension.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567

.EXAMPLE
    .\tools\Build-Layer8Extension.ps1 -Exclude LAPS, BitLockerKeys, RemoteControl
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string[]]$Exclude
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Layer8Features.ps1')

# Layer8's permanent identity as a console extension: the site recognises a newer cab as the same extension by
# this ID. Every Layer8 cab ever built must keep it. Never change it.
$extensionId = '3b900076-0ea8-46a7-b3b3-57bc7336c50f'

$name = 'Layer8'
$author = 'JBoogieman'
$description = 'Right-click tools for the Configuration Manager console: client actions, remote tools, deployment retries and collection tools. Open source (MIT).'
$Exclude = @($Exclude | Where-Object { $_ })
if ($Exclude) { [void](Get-Layer8FeatureItems $Exclude); $Exclude = Get-Layer8FeatureNames $Exclude }
if ($Exclude) { $description += " Built without: $($Exclude -join ', ')." }

# The same DLLs Layer8.msi installs (Layer8.Setup\Package.wxs).
$dlls = 'Layer8.dll', 'Layer8.Device.dll', 'Layer8.DriverManager.dll', 'Layer8.IniFileParser.dll'

# The signing certificate, found first so a typo fails before anything is built.
$certificate = $null
if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint -replace '\s', ''
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
    if (-not $certificate) { throw "No code-signing certificate with thumbprint $thumbprint in CurrentUser\My or LocalMachine\My." }
}
$untrustedWarned = $false
function Set-Layer8Signature([string]$path) {
    $signature = Set-AuthenticodeSignature -FilePath $path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if (-not $signature.SignerCertificate) { throw "Signing $path failed: $($signature.StatusMessage)" }
    # A certificate whose root this computer doesn't trust (a self-signed lab certificate) still signs; the site
    # server is the one that has to trust it.
    if ($signature.Status -ne 'Valid' -and -not $script:untrustedWarned) {
        Write-Warning "Signed, but this computer doesn't trust the certificate: $($signature.StatusMessage)"
        $script:untrustedWarned = $true
    }
}

$buildDir = Join-Path $repo "Build\$Configuration"
foreach ($dll in $dlls) {
    if (-not (Test-Path (Join-Path $buildDir $dll))) { throw "$dll isn't in $buildDir. Run .\tools\Build-Layer8Msi.ps1 first." }
}
$version = [Version](Get-Item (Join-Path $buildDir 'Layer8.dll')).VersionInfo.FileVersion

# The menu and dialog XML to package: the repo's, or a copy without the left-out features.
$extensionsDir = Get-Layer8ExtensionsFolder -Repo $repo -WorkFolder (Join-Path $repo "Build\Extensions-$Configuration") -Exclude $Exclude

$outDir = Join-Path $repo 'Build\Extension'
$contents = Join-Path $outDir "Layer8-$($version.ToString(3))"
if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
New-Item -ItemType Directory -Force -Path $contents | Out-Null

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# Layer8's own DLLs by the names the XML uses for them ("Layer8.Device" or "Layer8.Device.dll").
$layer8Assemblies = @($dlls | ForEach-Object { $_ -replace '\.dll$', '' })
function Test-Layer8Assembly([string]$assembly) {
    $layer8Assemblies -contains ($assembly.Trim() -replace '\.dll$', '')
}

# Menu XML: the console looks in Layer8's bin, so a console DLL gets a path back to AdminConsole\bin.
# From AdminConsole\Extensions\<ID>\<version>\bin, four levels up is AdminConsole.
function Convert-MenuXml([string]$text) {
    [regex]::Replace($text, '<Assembly>([^<]+)</Assembly>', {
        param($match)
        $assembly = $match.Groups[1].Value.Trim()
        if ((Test-Layer8Assembly $assembly) -or $assembly -match '[\\/]') { return $match.Value }
        "<Assembly>..\..\..\..\bin\$assembly</Assembly>"
    })
}

# Dialog XML: for a page's own Assembly attribute the console looks in AdminConsole\bin, so a Layer8 DLL gets a
# path to Layer8's bin. The form's <Assembly Name> is looked up in Layer8's bin already and stays as it is.
$dialogAssemblyPath = "..\Extensions\$extensionId\$($version.ToString())\bin\"
function Convert-DialogXml([string]$text) {
    [regex]::Replace($text, '(<Page\s[^>]*?\sAssembly\s*=\s*")([^"]+)(")', {
        param($match)
        $assembly = $match.Groups[2].Value
        if (-not (Test-Layer8Assembly $assembly)) { return $match.Value }
        $match.Groups[1].Value + $dialogAssemblyPath + $assembly.Trim() + $match.Groups[3].Value
    })
}

# Writes the cab's copy of an XML file: converted by $convert, optionally with a comment naming its console node
# (so its hash differs from other copies of the same file), keeping the file's byte-order mark if it has one.
function Write-CabCopy([string]$source, [string]$target, [scriptblock]$convert, [string]$node) {
    $bytes = [IO.File]::ReadAllBytes($source)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $skip = 0
    if ($bom) { $skip = 3 }
    $text = & $convert ([Text.Encoding]::UTF8.GetString($bytes, $skip, $bytes.Length - $skip))
    if ($node) {
        $comment = "<!-- Layer8: this copy is for console node $node -->"
        if ($text.StartsWith('<?xml')) {
            $end = $text.IndexOf('?>') + 2
            $text = $text.Insert($end, "`r`n$comment")
        }
        else {
            $text = "$comment`r`n$text"
        }
    }
    [IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding($bom)))
}

# 1. The menu XML, node by node.
$guidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$actions = @(foreach ($nodeDir in Get-ChildItem (Join-Path $extensionsDir 'Actions') -Directory | Sort-Object Name) {
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
    $node = $null
    if ($sharedHashes -contains $action.Hash) { $node = $action.Node }
    Write-CabCopy $action.Source (Join-Path $contents $action.CabName) ${function:Convert-MenuXml} $node
}

# 2. The dialog XML and the DLLs.
$forms = @(Get-ChildItem (Join-Path $extensionsDir 'Forms') -Filter *.xml -File | Sort-Object Name)
foreach ($form in $forms) { Write-CabCopy $form.FullName (Join-Path $contents $form.Name) ${function:Convert-DialogXml} $null }
foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $buildDir $dll) -Destination (Join-Path $contents $dll) }
# Signed before their hashes go into the manifest (signing changes the file). Only the cab's copies are signed.
if ($certificate) { foreach ($dll in $dlls) { Set-Layer8Signature (Join-Path $contents $dll) } }

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
    # Room for an Authenticode signature. Without it Set-AuthenticodeSignature can't sign the cab.
    '.Set ReservePerCabinetSize=6144'
)
$lines += "`"$manifest`" `"manifest.xml`""
foreach ($file in $packaged | Sort-Object Name) { $lines += "`"$($file.FullName)`" `"$($file.Name)`"" }
Set-Content -Path $ddf -Value $lines -Encoding ASCII

$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
$output = & $makecab /F $ddf 2>&1
if ($LASTEXITCODE -or -not (Test-Path $cab)) { throw "makecab failed:`n$($output -join "`n")" }
Remove-Item $ddf, (Join-Path $outDir 'Layer8.rpt'), (Join-Path $outDir 'Layer8.inf') -Force -ErrorAction SilentlyContinue

# 6. Sign the cab, if asked (the DLLs inside were signed in step 2).
$signedBy = $null
if ($certificate) {
    Set-Layer8Signature $cab
    $signedBy = $certificate.Subject
}

# 7. The release copy of Customize-Layer8Cab.ps1, with Layer8Features.ps1 built in at its marker line, so it's one
#    download that leaves out exactly the menu items these builds do.
$customizeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Customize-Layer8Cab.ps1') -Raw
$featuresSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Layer8Features.ps1') -Raw
$marker = [regex]::Escape(". (Join-Path `$PSScriptRoot 'Layer8Features.ps1') # LAYER8-FEATURES")
if ($customizeSource -notmatch $marker) { throw "Customize-Layer8Cab.ps1 has lost its LAYER8-FEATURES line." }
foreach ($feature in $Layer8Features.Keys) {
    if ($customizeSource -notmatch "\b$feature\b") { throw "Customize-Layer8Cab.ps1's help doesn't list the feature $feature." }
}
$customize = Join-Path $outDir 'Customize-Layer8Cab.ps1'
$bundled = [regex]::Replace($customizeSource, $marker, { param($match) $featuresSource.TrimEnd() })
[IO.File]::WriteAllText($customize, $bundled, (New-Object Text.UTF8Encoding($true)))

Write-Host ''
Write-Host "Built Layer8 $version as a console extension"
Write-Host "  $cab"
Write-Host "  SHA-256 $(Get-Sha256 $cab)"
Write-Host "  $($actions.Count) menu files on $(@($actions | Group-Object Node).Count) console nodes, $($forms.Count) dialog files, $($dlls.Count) DLLs"
if ($Exclude) { Write-Host "  Left out: $($Exclude -join ', ')" }
if ($signedBy) { Write-Host "  Cab and DLLs signed by $signedBy" }
else { Write-Host "  Unsigned: import it with 'Allow extension to be unsigned' ticked (needs 'Hierarchy approved console extensions can be unsigned')." }
Write-Host "  What went in: $contents"
Write-Host "  Customize-Layer8Cab.ps1 for the release (Layer8Features.ps1 built in): $customize"
