<#
.SYNOPSIS
    Signs a downloaded Layer8 cab with your own code-signing certificate, so your site can import it without
    allowing unsigned console extensions.

.DESCRIPTION
    Layer8's released cab (Layer8-<version>.cab) is unsigned: a signature only helps if your site server trusts
    the certificate behind it, and it won't trust Layer8's. This signs it with yours, in five steps:

      1. Unpacks the cab into a folder next to it, with expand.exe (part of Windows).
      2. Signs Layer8's DLLs. A site checks the DLLs inside a signed cab too: a signed cab with unsigned DLLs
         fails to import with "Failed to verify the signature of the cab or its content".
      3. Writes the signed DLLs' SHA-256 hashes into manifest.xml. Signing changes a file, and the manifest
         lists every file's hash.
      4. Packs the cab again with makecab.exe (part of Windows), leaving room for a signature.
      5. Signs the cab.

    The result is Layer8-<version>-signed.cab next to the original, which isn't changed. The working folder is
    removed at the end.

    Your site server has to trust the certificate: its root in Trusted Root Certification Authorities, and the
    certificate itself in Trusted Publishers (Local Computer stores). Then import the signed cab with
    "Allow extension to be unsigned" cleared.

    Building from source? tools\Build-Layer8Extension.ps1 -CertificateThumbprint signs as it builds, so you
    don't need this.

.PARAMETER Path
    The Layer8 cab to sign.

.PARAMETER CertificateThumbprint
    A code-signing certificate in your CurrentUser\My or LocalMachine\My store, with its private key. Signing is
    Authenticode, SHA-256, with a timestamp.

.PARAMETER TimestampServer
    The timestamp server, so the signature stays valid after the certificate expires. The computer needs
    internet access to reach it.

.EXAMPLE
    .\Sign-Layer8Cab.ps1 -Path .\Layer8-0.2.0.cab -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# Layer8's permanent console extension ID (the same in every Layer8 cab).
$extensionId = '3b900076-0ea8-46a7-b3b3-57bc7336c50f'

$cab = (Resolve-Path -LiteralPath $Path).ProviderPath

# The certificate, found first so a typo fails before anything is unpacked.
$thumbprint = $CertificateThumbprint -replace '\s', ''
$certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
if (-not $certificate) { throw "No code-signing certificate with thumbprint $thumbprint in CurrentUser\My or LocalMachine\My." }

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

$folder = Split-Path $cab
$baseName = [IO.Path]::GetFileNameWithoutExtension($cab)
$work = Join-Path $folder "$baseName-signing"
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

$entries = @($manifest.SelectNodes('//File'))
$names = @($entries | ForEach-Object { $_.GetAttribute('Name') } | Sort-Object -Unique)
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $work $name))) { throw "manifest.xml lists $name, but it isn't in the cab." }
}

# 2. and 3. Sign each DLL once, then put its new hash on every manifest entry for it.
$dllNames = @($names | Where-Object { $_ -like '*.dll' })
if (-not $dllNames) { throw 'manifest.xml lists no DLLs.' }
$hashes = @{}
foreach ($name in $dllNames) {
    $dll = Join-Path $work $name
    Set-Layer8Signature $dll
    $hashes[$name] = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
}
foreach ($entry in $entries) {
    $name = $entry.GetAttribute('Name')
    if ($hashes.ContainsKey($name)) { $entry.SelectSingleNode('Hash').InnerText = $hashes[$name] }
}
$settings = New-Object Xml.XmlWriterSettings
$settings.Encoding = New-Object Text.UTF8Encoding($false)
$writer = [Xml.XmlWriter]::Create($manifestPath, $settings)
try { $manifest.Save($writer) } finally { $writer.Close() }

# 4. Pack it again: the manifest and the files it lists, at the root of the cab.
$signedName = "$baseName-signed.cab"
$signedCab = Join-Path $folder $signedName
if (Test-Path $signedCab) { Remove-Item $signedCab -Force }
$ddf = Join-Path $work 'Layer8.ddf'
$lines = @(
    '.Option Explicit'
    ".Set CabinetNameTemplate=$signedName"
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
if ($LASTEXITCODE -or -not (Test-Path $signedCab)) { throw "makecab failed:`n$($output -join "`n")" }

# 5. Sign the cab.
Set-Layer8Signature $signedCab
Remove-Item $work -Recurse -Force

Write-Host ''
Write-Host "Signed Layer8 $version"
Write-Host "  $signedCab"
Write-Host "  SHA-256 $((Get-FileHash -LiteralPath $signedCab -Algorithm SHA256).Hash)"
Write-Host "  Cab and $($dllNames.Count) DLLs signed by $($certificate.Subject)"
Write-Host "  Import it with 'Allow extension to be unsigned' cleared. The site server has to trust the certificate."
