<#
.SYNOPSIS
    Layer8's regression checks that don't need the console. docs\REGRESSION.md is the checklist around it: install
    the test build, run this, do the console steps, then run it again with -Since.

.DESCRIPTION
    Read only: nothing changes on any device, on the site or in Active Directory. It writes its report to
    Build\Regression and works in a temporary folder, deleted at the end. Loading Layer8's DLLs for the Library
    checks writes a few lines to Layer8.log, as the console does when it loads them.

    The sections, in the order they run:

      Build      The DLLs carry SharedAssemblyInfo.cs's version and are newer than their code; the cab and the MSI
                 are from that build; the cab's manifest matches its files; the cab and its DLLs are signed by one
                 certificate; every XML file in the cab parses.
      Installed  The console's newest copy of Layer8 is this build's cab, file for file; Layer8.msi's files aren't
                 there as well; the console last loaded this version.
      Customize  The one-file release copy of Customize-Layer8Cab.ps1, alone in a folder with a space in its name,
                 leaves LAPS, BitLockerKeys and RestartComputer out of a copy of the cab: none of their menu items
                 left, every other item kept, no stray separators, the manifest's hashes right.
      Library    In 32-bit PowerShell, as the console runs: Error Lookup's parsing and its text from the console's
                 catalog, the signature check on HP and Lenovo downloads, the Lenovo catalog's signature check on
                 a good copy and on changed ones, and the checks behind deletes and writes whose paths come from a
                 device or a catalog (Client Cache, Driver Grabber, driver packs, legacy packages), the driver
                 importers' folder hash, Export's formula guard, how a copied LAPS or BitLocker password goes
                 onto the clipboard, WQL escaping, the computer-name check, picking a computer object, XML read
                 without DTDs, the hash and lock on driver packs, Dell's and HP's catalog signatures on the copies
                 their wizards keep (and that every Dell pack has a hash), the list copy and an empty error.
      Settings   Layer8 Settings at their defaults (HKCU\SOFTWARE\Layer8).
      Devices    That the SMS Provider reads names escaped the way Layer8 escapes them. Every member of the
                 collection, read from the same places the Health Dashboard reads: what each column should show,
                 each device's issues and the tile counts.
      Tools      In 32-bit PowerShell, as the console runs them: Online Status, Quick Status, Client Info, Check
                 Pending Restart, BitLocker Status, Defender Status and List Missing Updates on every member of the
                 collection, through the code the Client Tools menu and its progress window use. Each answer is
                 checked against the Devices section's reading of that device, and the registry view Check Pending
                 Restart reads against Windows' own.
      Log        With -Since: the errors, warnings and slow steps Layer8 logged since then, and the last Health
                 Dashboard run on the collection compared with the Devices section.
      Changes    The files changed since the last commit that changed docs\STATUS.md (the last recorded run), and
                 the sections of docs\REGRESSION.md whose Files lines name them.

    Each check prints PASS, FAIL, WARN, SKIP or INFO. The exit code is the number of FAILs.

    The Devices section reads the same WMI classes and registry values as the dashboard, but in 64-bit PowerShell,
    so it reads the registry as Windows itself does. It's a second copy of the same reads: it catches the dashboard
    drifting from what the device says, not a mistake made the same way in both. Started from 32-bit PowerShell,
    the script runs itself again in 64-bit for that reason; the Library and Tools sections start their own 32-bit
    PowerShell either way.

.PARAMETER Since
    For the second run, after the console steps: adds the Log section from this time on, and skips Build, Customize,
    Library and Tools (unless -Skip says otherwise). The first run prints the command to use.

.PARAMETER Collection
    The collection the Devices and Log sections look at. Default: All Systems.

.PARAMETER SiteServer
    The SMS Provider to read the collection from. Default: the server the console last connected to.

.PARAMETER SiteCode
    The site code. Default: the site the console last connected to.

.PARAMETER LenovoCatalog
    A copy of Lenovo's catalogv2.xml. Default: the copy the Lenovo Driver Pack wizard keeps in the temp folder.
    Without one, the catalog checks are skipped.

.PARAMETER Skip
    Sections to leave out: Build, Installed, Customize, Library, Settings, Devices, Tools, Log, Changes.

.EXAMPLE
    .\tools\Test-Layer8Regression.ps1

.EXAMPLE
    .\tools\Test-Layer8Regression.ps1 -Since '2026-11-20 19:25:00'

.EXAMPLE
    .\tools\Test-Layer8Regression.ps1 -Skip Customize, Library
#>
[CmdletBinding()]
param(
    [datetime]$Since,
    [string]$Collection = 'All Systems',
    [string]$SiteServer,
    [string]$SiteCode,
    [string]$LenovoCatalog = (Join-Path ([IO.Path]::GetTempPath()) 'LenovoCatalogV2.xml'),
    [ValidateSet('Build', 'Installed', 'Customize', 'Library', 'Settings', 'Devices', 'Tools', 'Log', 'Changes')]
    [string[]]$Skip
)

# The Devices section has to read the registry as Windows does: from 32-bit PowerShell, WMI's registry provider
# answers with the 32-bit view (WOW6432Node), the blind spot the Tools section looks for in Layer8. So run again in
# 64-bit, with the same parameters. The 32-bit parts start their own 32-bit PowerShell.
if ([IntPtr]::Size -eq 4 -and [Environment]::Is64BitOperatingSystem) {
    $quote = { param($text) "'" + ([string]$text).Replace("'", "''") + "'" }
    $command = '& ' + (& $quote $PSCommandPath)
    foreach ($name in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$name]
        if ($value -is [datetime]) { $value = $value.ToString('yyyy-MM-dd HH:mm:ss') }
        $command += " -$name " + ((@($value) | ForEach-Object { & $quote $_ }) -join ', ')
    }
    Write-Host 'Started in 32-bit PowerShell: running in 64-bit PowerShell instead.' -ForegroundColor DarkGray
    & (Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -Command ($command + '; exit $LASTEXITCODE')
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$started = Get-Date
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Layer8Features.ps1')
Import-Module CimCmdlets
Add-Type -AssemblyName System.Management

$sinceGiven = $PSBoundParameters.ContainsKey('Since')
# The second run comes after the console steps, which don't change the build or the devices' answers, so by default
# it doesn't repeat those checks.
if ($sinceGiven -and -not $PSBoundParameters.ContainsKey('Skip')) { $Skip = 'Build', 'Customize', 'Library', 'Tools' }

# Layer8's permanent console extension ID, the same in every Layer8 cab (Build-Layer8Extension.ps1).
$extensionId = '3b900076-0ea8-46a7-b3b3-57bc7336c50f'
$dlls = 'Layer8.dll', 'Layer8.Device.dll', 'Layer8.DriverManager.dll', 'Layer8.IniFileParser.dll'
# The code each DLL is built from, for "built after the last change". Directory.Build.* goes into every one of them,
# and SharedAssemblyInfo.cs into Layer8's own three.
$dllSources = @{
    'Layer8.dll'               = 'Layer8', 'SharedAssemblyInfo.cs', 'Directory.Build.props', 'Directory.Build.targets'
    'Layer8.Device.dll'        = 'Layer8.Device', 'SharedAssemblyInfo.cs', 'Directory.Build.props', 'Directory.Build.targets'
    'Layer8.DriverManager.dll' = 'Layer8.DriverManager', 'SharedAssemblyInfo.cs', 'Directory.Build.props', 'Directory.Build.targets'
    'Layer8.IniFileParser.dll' = 'IniFileParser', 'Directory.Build.props', 'Directory.Build.targets'
}
# The ini parser is a copy of a library with its own version (2.2.3, in its AssemblyInfo.cs), not SharedAssemblyInfo.cs's,
# and msbuild doesn't rebuild it when only Layer8's version changes.
$ownVersion = 'Layer8.IniFileParser.dll'

$buildDir = Join-Path $repo 'Build\Release'
$sharedInfo = Get-Content -LiteralPath (Join-Path $repo 'SharedAssemblyInfo.cs') -Raw
$version = [version][regex]::Match($sharedInfo, 'AssemblyFileVersion\("([0-9.]+)"\)').Groups[1].Value
# A signed test build is named with the whole version, an unsigned (release) build with three parts
# (Build-Layer8Extension.ps1). The test build is the one the console steps install.
$cabPath = Join-Path $repo ('Build\Extension\Layer8-{0}.cab' -f $version)
$releaseCabPath = Join-Path $repo ('Build\Extension\Layer8-{0}.cab' -f $version.ToString(3))
if (-not (Test-Path -LiteralPath $cabPath) -and (Test-Path -LiteralPath $releaseCabPath)) { $cabPath = $releaseCabPath }
$msiPath = Join-Path $repo 'Build\Installer\Release\Layer8.msi'

# SMS_ADMIN_UI_PATH is the console's bin\i386 folder.
$adminConsole = $null
$consoleBin = $null
if ($env:SMS_ADMIN_UI_PATH) {
    $consoleBin = Split-Path $env:SMS_ADMIN_UI_PATH.TrimEnd('\')
    $adminConsole = Split-Path $consoleBin
}
$logFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Layer8\Logs'

$work = Join-Path ([IO.Path]::GetTempPath()) ('Layer8 regression ' + $started.ToString('yyyyMMdd-HHmmss'))

# --- Results ---------------------------------------------------------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]
$resultColors = @{ PASS = 'Green'; FAIL = 'Red'; WARN = 'Yellow'; SKIP = 'DarkGray'; INFO = 'Gray' }
$section = ''

function Add-Result([string]$Result, [string]$Check, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Section = $script:section; Result = $Result; Check = $Check; Detail = $Detail })
    $line = '  {0}  {1}' -f $Result, $Check
    if ($Detail) { $line += ': ' + $Detail }
    Write-Host $line -ForegroundColor $resultColors[$Result]
}

function Test-Equal([string]$Check, $Expected, $Actual) {
    if ("$Expected" -ceq "$Actual") { Add-Result PASS $Check "$Actual" }
    else { Add-Result FAIL $Check "expected '$Expected', got '$Actual'" }
}

# --- Helpers -----------------------------------------------------------------------------------------------------

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Get-NewestFile([string[]]$Paths) {
    $names = @(& git -C $repo ls-files --cached --others --exclude-standard -- $Paths | Where-Object { $_ })
    $names | ForEach-Object { Get-Item -LiteralPath (Join-Path $repo $_) -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Expand-Cab([string]$Cab, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $output = & (Join-Path $env:SystemRoot 'System32\expand.exe') $Cab -F:* $Destination 2>&1
    if ($LASTEXITCODE -or -not (Test-Path -LiteralPath (Join-Path $Destination 'manifest.xml'))) {
        throw "Unpacking $Cab found no manifest.xml: $($output -join ' ')"
    }
}

# This build's cab, unpacked once for every section that needs it.
$builtCab = $null
function Get-BuiltCab {
    if ($script:builtCab) { return $script:builtCab }
    if (-not (Test-Path -LiteralPath $cabPath)) { throw "$cabPath isn't there: run tools\Build-Layer8Extension.ps1" }
    $folder = Join-Path $work 'built cab'
    Expand-Cab $cabPath $folder
    $manifest = New-Object Xml.XmlDocument
    $manifest.Load((Join-Path $folder 'manifest.xml'))
    $script:builtCab = [pscustomobject]@{ Path = $cabPath; Folder = $folder; Manifest = $manifest }
    $script:builtCab
}

# Every file a cab's manifest lists: menu files by console node, then the dialogs and DLLs.
function Get-ManifestFiles([Xml.XmlDocument]$Manifest) {
    foreach ($deployment in @($Manifest.DocumentElement.SelectNodes('Deployments/*'))) {
        $node = $deployment.GetAttribute('ParentNode')
        foreach ($file in @($deployment.SelectNodes('FileList/File'))) {
            $name = $file.GetAttribute('Name')
            $kind = 'Dialog'
            if ($deployment.LocalName -eq 'ActionExtensionDeployment') { $kind = 'Menu' }
            elseif ($name -like '*.dll') { $kind = 'DLL' }
            [pscustomobject]@{ Name = $name; Hash = $file.SelectSingleNode('Hash').InnerText; Kind = $kind; Node = $node }
        }
    }
}

# A manifest's files against the folder it was unpacked to: missing files, wrong hashes, a hash used twice.
function Test-Manifest([string]$Label, [string]$Folder, [Xml.XmlDocument]$Manifest) {
    $files = @(Get-ManifestFiles $Manifest)
    $bad = @(foreach ($file in $files) {
        $path = Join-Path $Folder $file.Name
        if (-not (Test-Path -LiteralPath $path)) { "$($file.Name) missing" }
        elseif ((Get-Sha256 $path) -ne $file.Hash) { "$($file.Name) has a different hash" }
    })
    $listed = @($files | ForEach-Object Name) + 'manifest.xml'
    $extra = @(Get-ChildItem -LiteralPath $Folder -File | Where-Object { $listed -notcontains $_.Name } | ForEach-Object { "$($_.Name) not in the manifest" })
    # The console rejects a manifest that lists the same hash twice.
    $twice = @($files | Group-Object Hash | Where-Object Count -gt 1 | ForEach-Object { ($_.Group.Name -join ' = ') + ' share a hash' })
    $problems = @($bad) + @($extra) + @($twice)
    if ($problems) { Add-Result FAIL "$Label manifest" (($problems | Select-Object -First 5) -join '; ') }
    else { Add-Result PASS "$Label manifest" "$($files.Count) files, each with its hash" }
}

# Reads a file that the console may be writing to.
function Read-SharedText([string]$Path) {
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try {
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
        $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

# Layer8.log's entries, oldest first: the rolled-over Layer8.lo_ (Layer8Log keeps one, 2 MB), then Layer8.log.
$logEntries = $null
function Get-LogEntries {
    if ($null -ne $script:logEntries) { return $script:logEntries }
    $entries = New-Object System.Collections.Generic.List[object]
    # CMTrace format. A message can span lines (an exception's stack trace), so the whole file is matched at once.
    $pattern = [regex]'(?s)<!\[LOG\[(.*?)\]LOG\]!><time="([0-9:.]+)[+-][0-9]+" date="([0-9-]+)" component="([^"]*)" context="[^"]*" type="([0-9])"'
    foreach ($name in 'Layer8.lo_', 'Layer8.log') {
        $path = Join-Path $logFolder $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($match in $pattern.Matches((Read-SharedText $path))) {
            $when = [datetime]::ParseExact($match.Groups[3].Value + ' ' + $match.Groups[2].Value, 'MM-dd-yyyy HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
            $entries.Add([pscustomobject]@{ Time = $when; Text = $match.Groups[1].Value; Component = $match.Groups[4].Value; Type = [int]$match.Groups[5].Value })
        }
    }
    $script:logEntries = $entries
    $entries
}

function Get-FirstLine([string]$Text, [int]$Length = 220) {
    $line = ($Text -split "`r?`n")[0].Trim()
    if ($line.Length -gt $Length) { $line = $line.Substring(0, $Length) + '...' }
    $line
}

# --- Build ---------------------------------------------------------------------------------------------------------

function Test-Build {
    Add-Result INFO 'Version' "$version (SharedAssemblyInfo.cs)"

    foreach ($dll in $dlls) {
        $path = Join-Path $buildDir $dll
        if (-not (Test-Path -LiteralPath $path)) { Add-Result FAIL "$dll built" 'not in Build\Release: run tools\Build-Layer8Msi.ps1'; continue }
        $item = Get-Item -LiteralPath $path
        if ($dll -eq $ownVersion) { Add-Result INFO "$dll version" "$($item.VersionInfo.FileVersion), its own" }
        else { Test-Equal "$dll version" $version $item.VersionInfo.FileVersion }
        $newest = Get-NewestFile $dllSources[$dll]
        if ($newest -and $newest.LastWriteTime -gt $item.LastWriteTime) {
            Add-Result WARN "$dll newer than its code" ("{0} changed at {1:g}, after the build at {2:g}: build again" -f $newest.FullName.Substring($repo.Length + 1), $newest.LastWriteTime, $item.LastWriteTime)
        }
        else { Add-Result PASS "$dll newer than its code" ("built {0:g}" -f $item.LastWriteTime) }
    }
    $layer8Dll = Get-Item -LiteralPath (Join-Path $buildDir 'Layer8.dll') -ErrorAction SilentlyContinue

    # The cab: from this build, and from the current menu XML.
    if (-not (Test-Path -LiteralPath $cabPath)) { Add-Result FAIL 'Cab built' "$cabPath isn't there: run tools\Build-Layer8Extension.ps1"; return }
    $cabItem = Get-Item -LiteralPath $cabPath
    $newestXml = Get-NewestFile 'Extensions'
    if ($layer8Dll -and $cabItem.LastWriteTime -lt $layer8Dll.LastWriteTime) {
        Add-Result FAIL 'Cab newer than the DLLs' ("cab {0:g}, Layer8.dll {1:g}: run tools\Build-Layer8Extension.ps1" -f $cabItem.LastWriteTime, $layer8Dll.LastWriteTime)
    }
    elseif ($newestXml -and $cabItem.LastWriteTime -lt $newestXml.LastWriteTime) {
        Add-Result WARN 'Cab newer than the menu XML' ("{0} changed at {1:g}, after the cab: build it again" -f $newestXml.FullName.Substring($repo.Length + 1), $newestXml.LastWriteTime)
    }
    else { Add-Result PASS 'Cab newer than the DLLs and the menu XML' ("{0} ({1:g})" -f $cabItem.Name, $cabItem.LastWriteTime) }

    $cab = Get-BuiltCab
    $root = $cab.Manifest.DocumentElement
    Test-Equal 'Cab extension ID' $extensionId $root.GetAttribute('ExtensionID')
    Test-Equal 'Cab version' $version $root.GetAttribute('Version')
    if ($root.GetAttribute('Description') -match 'Built without: (.*)\.$') { Add-Result WARN 'Cab has every feature' "built without $($Matches[1])" }
    else { Add-Result PASS 'Cab has every feature' 'nothing left out' }
    Test-Manifest 'Cab' $cab.Folder $cab.Manifest
    foreach ($dll in $dlls) {
        $path = Join-Path $cab.Folder $dll
        if ($dll -eq $ownVersion -or -not (Test-Path -LiteralPath $path)) { continue }
        Test-Equal "$dll version in the cab" $version (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    }

    # Signatures. A site checks the DLLs in a signed cab too: unsigned DLLs fail the import (Build-Layer8Extension.ps1).
    $cabSignature = Get-AuthenticodeSignature -LiteralPath $cabPath
    if ($cabSignature.Status -eq 'NotSigned') {
        Add-Result WARN 'Cab signed' 'not signed: right for a release build, but a test build is signed (CertificateThumbprint)'
    }
    elseif ($cabSignature.Status -ne 'Valid') {
        Add-Result FAIL 'Cab signed' "$($cabSignature.Status): $($cabSignature.StatusMessage)"
    }
    else {
        $signer = $cabSignature.SignerCertificate
        $stamped = 'no timestamp'
        if ($cabSignature.TimeStamperCertificate) { $stamped = 'timestamped' }
        Add-Result PASS 'Cab signed' "$($signer.Subject), $stamped"
        foreach ($dll in $dlls) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $cab.Folder $dll)
            if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Thumbprint -eq $signer.Thumbprint) { Add-Result PASS "$dll in the cab signed" 'same certificate as the cab' }
            elseif ($signature.Status -eq 'NotSigned') { Add-Result FAIL "$dll in the cab signed" 'not signed, so the site refuses the signed cab' }
            else { Add-Result FAIL "$dll in the cab signed" "$($signature.Status), $($signature.SignerCertificate.Subject)" }
        }
    }

    # Every XML file parses: a menu file that doesn't is dropped by the console without a word.
    $xmlFiles = @(Get-ChildItem -LiteralPath $cab.Folder -Filter *.xml -File)
    $broken = @(foreach ($file in $xmlFiles) {
        try { (New-Object Xml.XmlDocument).Load($file.FullName) } catch { "$($file.Name): $($_.Exception.InnerException.Message)" }
    })
    if ($broken) { Add-Result FAIL 'Cab XML parses' ($broken -join '; ') }
    else {
        $files = @(Get-ManifestFiles $cab.Manifest)
        $menus = @($files | Where-Object Kind -eq 'Menu')
        Add-Result PASS 'Cab XML parses' ("{0} menu files on {1} console nodes, {2} dialog files" -f $menus.Count, @($menus | Group-Object Node).Count, @($files | Where-Object Kind -eq 'Dialog').Count)
    }

    # The MSI from the same build.
    if (-not (Test-Path -LiteralPath $msiPath)) { Add-Result FAIL 'MSI built' "$msiPath isn't there: run tools\Build-Layer8Msi.ps1"; return }
    $msiItem = Get-Item -LiteralPath $msiPath
    if ($layer8Dll -and $msiItem.LastWriteTime -lt $layer8Dll.LastWriteTime) { Add-Result FAIL 'MSI newer than the DLLs' ("MSI {0:g}, Layer8.dll {1:g}" -f $msiItem.LastWriteTime, $layer8Dll.LastWriteTime) }
    else { Add-Result PASS 'MSI newer than the DLLs' ("{0:g}" -f $msiItem.LastWriteTime) }
    Test-Equal 'MSI ProductVersion' $version (Get-MsiProperty $msiPath 'ProductVersion')
}

# One property from an MSI's Property table, opened read-only.
function Get-MsiProperty([string]$Path, [string]$Property) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null; $view = $null; $record = $null
    try {
        $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property = '$Property'"))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($record) { $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1)) }
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    }
    finally {
        # Released, or the MSI stays locked until PowerShell exits.
        foreach ($comObject in $record, $view, $database, $installer) { if ($comObject) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($comObject) } }
    }
}

# --- Installed ------------------------------------------------------------------------------------------------------

function Test-Installed {
    if (-not $adminConsole) { Add-Result SKIP 'Console' 'no ConfigMgr console on this computer (SMS_ADMIN_UI_PATH)'; return }

    # The console only loads approved extensions while this folder exists (docs\INSTALL.md).
    $xmlStorageActions = Join-Path $adminConsole 'XmlStorage\Extensions\Actions'
    if (Test-Path -LiteralPath $xmlStorageActions) { Add-Result PASS 'XmlStorage\Extensions\Actions exists' 'the console loads approved extensions' }
    else { Add-Result FAIL 'XmlStorage\Extensions\Actions exists' 'missing, so the console loads no approved extension (docs\INSTALL.md)' }

    # Never both: on a hierarchy that allows both, every Layer8 menu would show twice.
    $msiFiles = @()
    if (Test-Path -LiteralPath (Join-Path $consoleBin 'Layer8.dll')) { $msiFiles += 'bin\Layer8.dll' }
    $msiMenus = @(Get-ChildItem -LiteralPath $xmlStorageActions -Recurse -Filter 'Layer8*.xml' -File -ErrorAction SilentlyContinue | Where-Object Name -ne 'Layer8Probe.xml')
    if ($msiMenus) { $msiFiles += "$($msiMenus.Count) menu files in XmlStorage" }
    if ($msiFiles) { Add-Result FAIL "Layer8.msi isn't installed as well" "$($msiFiles -join ', '): uninstall the MSI (tools\Uninstall-Layer8Dev.ps1 or msiexec /x)" }
    else { Add-Result PASS "Layer8.msi isn't installed as well" 'no Layer8 files in the console''s own bin or XmlStorage' }
    $probe = @(Get-ChildItem -LiteralPath $xmlStorageActions -Recurse -Filter 'Layer8Probe.xml' -File -ErrorAction SilentlyContinue)
    if ($probe) { Add-Result INFO 'Developer items' "Probe Selection is on $($probe.Count) nodes (tools\Dev-Loop.ps1 -WithDevTools)" }

    $installRoot = Join-Path $adminConsole "Extensions\$extensionId"
    $installed = @(Get-ChildItem -LiteralPath $installRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -as [version] } | Sort-Object { [version]$_.Name })
    if (-not $installed) { Add-Result FAIL 'Installed as an approved extension' "nothing in $installRoot"; return }
    $newest = $installed[-1]
    if ($newest.Name -ne "$version") {
        Add-Result FAIL 'Newest installed version' "the console has $($newest.Name), this build is $version`: import, approve and install the cab"
    }
    else {
        Add-Result PASS 'Newest installed version' ("{0}, installed {1:g}" -f $newest.Name, $newest.CreationTime)
        # Installed where the console puts each kind of file: Actions\<node>, Forms and bin.
        $cab = Get-BuiltCab
        $files = @(Get-ManifestFiles $cab.Manifest)
        $different = @(foreach ($file in $files) {
            $relative = switch ($file.Kind) { 'Menu' { "Actions\$($file.Node)\$($file.Name)" } 'Dialog' { "Forms\$($file.Name)" } default { "bin\$($file.Name)" } }
            $path = Join-Path $newest.FullName $relative
            if (-not (Test-Path -LiteralPath $path)) { "$relative missing" }
            elseif ((Get-Sha256 $path) -ne (Get-Sha256 (Join-Path $cab.Folder $file.Name))) { "$relative differs" }
        })
        if ($different) { Add-Result FAIL 'Installed files match the cab' ("{0} of {1}: {2}. A cab with the same version was installed before this one: delete it on the site, import again" -f $different.Count, $files.Count, (($different | Select-Object -First 3) -join '; ')) }
        else { Add-Result PASS 'Installed files match the cab' "all $($files.Count)" }
    }

    # What the console last loaded. Layer8Log writes this line whenever a process loads Layer8; only the console's
    # have a 5.x version (the Library checks' 32-bit PowerShell writes one too).
    $loaded = Get-LogEntries | Where-Object { $_.Text -match '^Layer8 (\S+) loaded in console (5\.\S+)' } | Select-Object -Last 1
    if (-not $loaded) { Add-Result WARN 'Version the console loaded' 'Layer8.log has no line from the console loading Layer8'; return }
    $null = $loaded.Text -match '^Layer8 (\S+) loaded in console (5\.\S+)'
    $what = "Layer8 $($Matches[1]) in console $($Matches[2]) at $($loaded.Time.ToString('g'))"
    # Layer8Log writes the line the first time the console runs something of Layer8's, not when the console starts, so
    # a load from before this copy was installed only means nothing has run since. The dashboard run will.
    if ($Matches[1] -ne "$version" -and $newest.Name -eq "$version" -and $loaded.Time -lt $newest.CreationTime) {
        Add-Result WARN 'Version the console loaded' "nothing of Layer8's has run since $version was installed (last: $what). The -Since run checks it after the dashboard"
    }
    elseif ($Matches[1] -ne "$version") { Add-Result FAIL 'Version the console loaded' "$what`: close the console and open it again" }
    elseif ($newest.Name -eq "$version" -and $loaded.Time -lt $newest.CreationTime) { Add-Result WARN 'Version the console loaded' "$what, before this copy was installed: close the console and open it again" }
    else { Add-Result PASS 'Version the console loaded' $what }
}

# --- Customize ------------------------------------------------------------------------------------------------------

# A menu item's place: its name and the names of the menus it's in, "Remote Tools > Ping".
function Get-MenuPath([Xml.XmlNode]$Node) {
    $names = @()
    while ($Node -is [Xml.XmlElement]) {
        if ($Node.LocalName -eq 'ActionDescription') {
            $name = $Node.GetAttribute('DisplayName')
            if (-not $name) { $name = $Node.GetAttribute('MnemonicDisplayName') }
            $names = @($name) + $names
        }
        $Node = $Node.ParentNode
    }
    $names -join ' > '
}

# Every menu item in a set of menu files, and every menu whose separators are out of place.
function Get-MenuShape([string]$Folder, $MenuFiles) {
    $items = New-Object System.Collections.Generic.List[object]
    $strays = New-Object System.Collections.Generic.List[string]
    foreach ($file in $MenuFiles) {
        $xml = New-Object Xml.XmlDocument
        $xml.Load((Join-Path $Folder $file.Name))
        foreach ($action in @($xml.SelectNodes('//ActionDescription'))) {
            if ($action.GetAttribute('Class') -in 'Separator', 'None') { continue }
            $items.Add([pscustomobject]@{
                Key   = $file.Name + '|' + (Get-MenuPath $action)
                Names = @($action.GetAttribute('DisplayName'), $action.GetAttribute('MnemonicDisplayName')) | Where-Object { $_ }
                Leaf  = $null -eq $action.SelectSingleNode('ActionGroups/ActionDescription')
            })
        }
        foreach ($groups in @($xml.SelectNodes('//ActionGroups'))) {
            $entries = @($groups.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] -and $_.LocalName -eq 'ActionDescription' })
            if (-not $entries) { continue }
            $where = $file.Name + ': ' + (Get-MenuPath $groups.ParentNode)
            $separator = @($entries | ForEach-Object { $_.GetAttribute('Class') -eq 'Separator' })
            if ($separator[0]) { $strays.Add("$where starts with a separator") }
            if ($separator[-1]) { $strays.Add("$where ends with a separator") }
            for ($i = 1; $i -lt $separator.Count; $i++) {
                if ($separator[$i] -and $separator[$i - 1]) { $strays.Add("$where has two separators together"); break }
            }
            if (-not @($entries | Where-Object { $_.GetAttribute('Class') -notin 'Separator', 'None' })) { $strays.Add("$where has nothing but separators") }
        }
    }
    [pscustomobject]@{ Items = $items; Strays = $strays }
}

function Test-Customize {
    $cab = Get-BuiltCab
    # The release copy: Layer8Features.ps1 built in, so it runs with nothing next to it.
    $releaseCopy = Join-Path $repo 'Build\Extension\Customize-Layer8Cab.ps1'
    if (-not (Test-Path -LiteralPath $releaseCopy)) { Add-Result FAIL 'Release copy of Customize-Layer8Cab.ps1' "not in Build\Extension: run tools\Build-Layer8Extension.ps1"; return }

    $exclude = 'LAPS', 'BitLockerKeys', 'RestartComputer'
    $excludedItems = @($exclude | ForEach-Object { $Layer8Features[$_] })
    # A downloads folder with a space in its name, as a release's users have.
    $downloads = Join-Path $work 'My Downloads'
    New-Item -ItemType Directory -Path $downloads | Out-Null
    Copy-Item -LiteralPath $releaseCopy, $cab.Path -Destination $downloads
    $copy = Join-Path $downloads (Split-Path $cab.Path -Leaf)
    # Each line's own text: Out-String wraps at the window's width, which in the ISE broke the summary line in two.
    $output = (& (Join-Path $downloads 'Customize-Layer8Cab.ps1') -Path $copy -Exclude $exclude *>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.InformationRecord]) { [string]$_.MessageData } else { [string]$_ }
    }) -join "`n"
    $counts = [regex]::Match($output, '\((\d+) menu files dropped, (\d+) changed\)')
    if ($counts.Success) { Add-Result INFO 'Customize-Layer8Cab.ps1 ran' "without $($exclude -join ', '): $($counts.Groups[1].Value) menu files dropped, $($counts.Groups[2].Value) changed" }
    else { Add-Result WARN 'Customize-Layer8Cab.ps1 ran' "its summary line wasn't there: $(Get-FirstLine $output.Trim())" }

    $customCab = Join-Path $downloads ('{0}-custom.cab' -f [IO.Path]::GetFileNameWithoutExtension($copy))
    if (-not (Test-Path -LiteralPath $customCab)) { Add-Result FAIL 'Customized cab' "no $(Split-Path $customCab -Leaf)"; return }
    $customFolder = Join-Path $work 'custom cab'
    Expand-Cab $customCab $customFolder
    $customManifest = New-Object Xml.XmlDocument
    $customManifest.Load((Join-Path $customFolder 'manifest.xml'))
    Test-Manifest 'Customized cab' $customFolder $customManifest

    $description = $customManifest.DocumentElement.GetAttribute('Description')
    if ($description -like "* Built without: $($exclude -join ', ').") { Add-Result PASS 'Customized cab says what it left out' "Built without: $($exclude -join ', ')." }
    else { Add-Result FAIL 'Customized cab says what it left out' "its description: $description" }

    $before = Get-MenuShape $cab.Folder @(Get-ManifestFiles $cab.Manifest | Where-Object Kind -eq 'Menu')
    $after = Get-MenuShape $customFolder @(Get-ManifestFiles $customManifest | Where-Object Kind -eq 'Menu')
    $isExcluded = { param($item) @($item.Names | Where-Object { $excludedItems -contains $_ }).Count -gt 0 }

    $hadThem = @($before.Items | Where-Object { & $isExcluded $_ })
    if (-not $hadThem) { Add-Result WARN 'Left-out items were in the cab' 'the built cab has none of them, so this proves little' }
    $left = @($after.Items | Where-Object { & $isExcluded $_ } | ForEach-Object Key)
    if ($left) { Add-Result FAIL 'Left-out items are gone' (($left | Select-Object -First 5) -join '; ') }
    else { Add-Result PASS 'Left-out items are gone' "$($hadThem.Count) items, among them $(($excludedItems | Select-Object -First 3) -join ', ')" }

    $afterKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($item in $after.Items) { [void]$afterKeys.Add($item.Key) }
    $kept = @($before.Items | Where-Object { $_.Leaf -and -not (& $isExcluded $_) })
    $lost = @($kept | Where-Object { -not $afterKeys.Contains($_.Key) } | ForEach-Object Key)
    if ($lost) { Add-Result FAIL 'Every other item kept' ("{0} lost: {1}" -f $lost.Count, (($lost | Select-Object -First 5) -join '; ')) }
    else { Add-Result PASS 'Every other item kept' "all $($kept.Count) menu items" }

    $newStrays = @($after.Strays | Where-Object { $before.Strays -notcontains $_ })
    if ($newStrays) { Add-Result FAIL 'No stray separators' (($newStrays | Select-Object -First 5) -join '; ') }
    else { Add-Result PASS 'No stray separators' 'none at the start or end of a menu, none together' }

    # Leaving features out only changes menu files; the DLLs go through untouched.
    $changedDlls = @(foreach ($dll in $dlls) { if ((Get-Sha256 (Join-Path $customFolder $dll)) -ne (Get-Sha256 (Join-Path $cab.Folder $dll))) { $dll } })
    if ($changedDlls) { Add-Result FAIL 'DLLs untouched' ($changedDlls -join ', ') }
    else { Add-Result PASS 'DLLs untouched' 'same hashes as the built cab' }
}

# --- Library --------------------------------------------------------------------------------------------------------

# Layer8's DLLs reference the console's own (the wizard pages' and dialogs' base classes, the SMS Provider's
# classes), which are in the console's bin folder, not next to Layer8's. The 32-bit jobs register this first.
$resolverSource = @'
using System;
using System.IO;
using System.Reflection;
public static class Layer8RegressionResolver
{
    static string[] folders;
    public static void Register(string[] searchFolders)
    {
        folders = searchFolders;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        foreach (string folder in folders)
        {
            string path = Path.Combine(folder, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
'@

# Runs in a 32-bit PowerShell job: Layer8's DLLs are built for x86, like the console. It returns its checks as objects.
$libraryChecks = {
    param([string]$BuildDir, [string]$ConsoleBin, [string]$Catalog, [string]$Work, [string]$ResolverSource)
    $ErrorActionPreference = 'Stop'
    $out = New-Object System.Collections.Generic.List[object]
    function Add-Check([string]$Result, [string]$Check, [string]$Detail = '') { $out.Add([pscustomobject]@{ Result = $Result; Check = $Check; Detail = $Detail }) }
    # What a method called by reflection threw, without the wrappers reflection and PowerShell put round it.
    function Get-Message($Exception) {
        while ($Exception.InnerException -and ($Exception -is [Management.Automation.MethodInvocationException] -or $Exception -is [Reflection.TargetInvocationException])) { $Exception = $Exception.InnerException }
        $Exception.Message
    }
    function Test-Text([string]$Check, [string]$Actual, [string]$Expected) {
        if (-not $Actual) { Add-Check FAIL $Check "expected '$Expected', got nothing (for a refusal: it was accepted)" }
        elseif ($Actual -like "*$Expected*") { Add-Check PASS $Check $Actual }
        else { Add-Check FAIL $Check "expected '$Expected', got '$Actual'" }
    }

    try {
        if ([IntPtr]::Size -ne 4) { throw 'this has to run in 32-bit PowerShell' }
        Add-Type -TypeDefinition $ResolverSource
        [Layer8RegressionResolver]::Register([string[]]@($BuildDir, $ConsoleBin))
        $flags = [Reflection.BindingFlags]'Static, Public, NonPublic'
        $device = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDir 'Layer8.Device.dll'))
        $driver = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDir 'Layer8.DriverManager.dll'))
        function Get-Method($Type, [string]$Name) {
            $method = $Type.GetMethod($Name, $flags)
            if (-not $method) { throw "$($Type.FullName).$Name isn't there (renamed?)" }
            $method
        }

        # Error Lookup: a code however it was copied (the Error Lookup row in STATUS.md).
        $codes = $device.GetType('Layer8.Device.ErrorCodes', $true)
        $tryParse = Get-Method $codes 'TryParse'
        $describe = Get-Method $codes 'Describe'
        $formatAndDescribe = Get-Method $codes 'FormatAndDescribe'
        $code = [Convert]::ToUInt32('87D00668', 16)
        $signed = [BitConverter]::ToInt32([BitConverter]::GetBytes($code), 0)
        $cases = @(
            @('0x87D00668', $code, 'hex'),
            @('87D00668', $code, 'bare hex'),
            @([string]$signed, $code, 'a signed HRESULT'),
            @([string]$code, $code, 'decimal'),
            @('1603', [uint32]1603, 'an installer code'),
            @(' 0x643 ', [uint32]1603, 'hex with spaces round it'),
            @('not a code', $null, 'junk text'),
            @('', $null, 'nothing')
        )
        foreach ($case in $cases) {
            [object[]]$arguments = @($case[0], [uint32]0)
            $parsed = $tryParse.Invoke($null, $arguments)
            $label = "Error Lookup reads $($case[2]) ('$($case[0])')"
            if ($null -eq $case[1]) {
                if ($parsed) { Add-Check FAIL $label ('took it as 0x{0:X8}' -f $arguments[1]) } else { Add-Check PASS $label 'not a code' }
            }
            elseif ($parsed -and [uint32]$arguments[1] -eq $case[1]) { Add-Check PASS $label ('0x{0:X8}' -f $arguments[1]) }
            else {
                $got = 'not a code'
                if ($parsed) { $got = '0x{0:X8}' -f $arguments[1] }
                Add-Check FAIL $label ('expected 0x{0:X8}, got {1}' -f $case[1], $got)
            }
        }
        # 0x87D00668's text is only in the console's catalog (not in Layer8's own list, and Windows has none).
        Test-Text 'Error text from the console''s catalog (0x87D00668)' ($describe.Invoke($null, @([Convert]::ToUInt32('87D00668', 16)))) 'still detected as actionable after apply'
        Test-Text 'Error text for a Windows Update code (0x80240022)' ($describe.Invoke($null, @([Convert]::ToUInt32('80240022', 16)))) 'Operation failed for all the updates'
        Test-Text 'Error text for an application code (0x87D00324)' ($describe.Invoke($null, @([Convert]::ToUInt32('87D00324', 16)))) 'not detected after installation completed'
        # As Copy Failed Devices and Install Application show it.
        $copied = $formatAndDescribe.Invoke($null, @([uint32]1603))
        if ($copied -eq '1603 (0x643): Fatal error during installation') { Add-Check PASS 'An installer code as the lists show it' $copied }
        else { Add-Check FAIL 'An installer code as the lists show it' "expected '1603 (0x643): Fatal error during installation', got '$copied'" }

        # The signature check on downloaded HP SoftPaqs and Lenovo packs, on a file Microsoft signed.
        $authenticode = $driver.GetType('Layer8.DriverManager.Authenticode', $true)
        $require = Get-Method $authenticode 'RequireSignedBy'
        $lenovoSigner = $authenticode.GetField('LenovoSigner', $flags).GetValue($null)
        $hpSigner = $authenticode.GetField('HPSigner', $flags).GetValue($null)
        function Test-Signed([string]$Path, $Signer, [string]$Vendor) {
            # Every parameter, the optional refusal text too: reflection doesn't fill in defaults.
            try { [void]$require.Invoke($null, @($Path, $Signer, $Vendor, 'run it')); $null } catch { Get-Message $_.Exception }
        }
        $signedFile = Join-Path $ConsoleBin 'Microsoft.ConfigurationManagement.exe'
        $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
        if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
            Add-Check SKIP 'Download signature check' "$signedFile has no signature of its own to test with"
        }
        else {
            $problem = Test-Signed $signedFile ([regex]'^Microsoft Corporation$') 'Microsoft'
            if ($null -eq $problem) { Add-Check PASS 'Download signature check: the right signer passes' 'Microsoft.ConfigurationManagement.exe, signed by Microsoft' }
            else { Add-Check FAIL 'Download signature check: the right signer passes' $problem }
            Test-Text 'Download signature check: another signer is refused (Lenovo)' (Test-Signed $signedFile $lenovoSigner 'Lenovo') 'is signed by "Microsoft Corporation", not Lenovo'
            Test-Text 'Download signature check: another signer is refused (HP)' (Test-Signed $signedFile $hpSigner 'HP') 'not HP'
            # One byte changed in the middle, well clear of the signature at the end of the file.
            $bytes = [IO.File]::ReadAllBytes($signedFile)
            $middle = [int]($bytes.Length / 2)
            $bytes[$middle] = [byte]($bytes[$middle] -bxor 0xFF)
            $changed = Join-Path $Work 'changed.exe'
            [IO.File]::WriteAllBytes($changed, $bytes)
            Test-Text 'Download signature check: a changed file is refused' (Test-Signed $changed $lenovoSigner 'Lenovo') "isn't validly signed (WinVerifyTrust 0x"
            Test-Text 'Download signature check: an unsigned file is refused' (Test-Signed (Join-Path $BuildDir 'Layer8.IniFileParser.dll') $lenovoSigner 'Lenovo') "isn't validly signed"
        }

        # Lenovo's catalog: signed by Lenovo as a whole, or not used (the Lenovo row in STATUS.md).
        if (-not $Catalog -or -not (Test-Path -LiteralPath $Catalog)) {
            Add-Check SKIP 'Lenovo catalog checks' "no copy at $Catalog`: open the Lenovo Driver Pack wizard once, or pass -LenovoCatalog"
        }
        else {
            $page = $driver.GetType('Layer8.DriverManager.LenovoDriverPackGeneralPage', $true)
            $load = Get-Method $page 'LoadCatalog'
            function Test-Catalog([string]$Path) { try { [void]$load.Invoke($null, @($Path)); $null } catch { Get-Message $_.Exception } }

            $raw = [IO.File]::ReadAllBytes($Catalog)
            $bom = $raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF
            $skip = 0
            if ($bom) { $skip = 3 }
            $text = [Text.Encoding]::UTF8.GetString($raw, $skip, $raw.Length - $skip)
            $encoding = New-Object Text.UTF8Encoding($bom)
            function Save-Variant([string]$Name, [string]$Content) { $path = Join-Path $Work $Name; [IO.File]::WriteAllText($path, $Content, $encoding); $path }
            function Test-Refused([string]$Check, [string]$Content, [string]$Expected) {
                if ($Content -ceq $text) { Add-Check SKIP $Check 'couldn''t make the change: the catalog''s layout is different'; return }
                Test-Text $Check (Test-Catalog (Save-Variant (($Check -replace '\W', '') + '.xml') $Content)) $Expected
            }

            $problem = Test-Catalog $Catalog
            $copyDate = (Get-Item -LiteralPath $Catalog).LastWriteTime.ToString('g')
            if ($null -eq $problem) { Add-Check PASS 'Lenovo catalog: Lenovo''s copy passes' "downloaded $copyDate" }
            else { Add-Check FAIL 'Lenovo catalog: Lenovo''s copy passes' "$problem (downloaded $copyDate)" }

            $link = ([regex]'(<SCCM\b[^>]*>)https://download\.lenovo\.com/').Replace($text, '${1}https://download.example.com/', 1)
            Test-Refused 'Lenovo catalog: a changed download link is refused' $link 'has been changed since Lenovo signed it'
            $hash = ([regex]'(<SCCM\b[^>]*\bcrc=")([0-9a-fA-F])').Replace($text, [Text.RegularExpressions.MatchEvaluator]{
                param($match)
                $digit = '0'
                if ($match.Groups[2].Value -eq '0') { $digit = '1' }
                $match.Groups[1].Value + $digit
            }, 1)
            Test-Refused 'Lenovo catalog: a changed hash is refused' $hash 'has been changed since Lenovo signed it'
            $spaced = $text
            $firstModel = $text.IndexOf('<Model ')
            if ($firstModel -ge 0) { $spaced = $text.Insert($firstModel, ' ') }
            Test-Refused 'Lenovo catalog: extra white space is refused' $spaced 'has been changed since Lenovo signed it'
            $unsigned = ([regex]'<Signature\b[\s\S]*?</Signature>').Replace($text, '', 1)
            Test-Refused 'Lenovo catalog: no signature is refused' $unsigned "isn't signed"

            # Signed again with a certificate made here and kept in memory only: one named Lenovo that Windows doesn't
            # trust, and one from another company.
            function Save-Resigned([string]$Name, [string]$Subject) {
                Add-Type -AssemblyName System.Security
                $document = New-Object Xml.XmlDocument
                $document.PreserveWhitespace = $true
                $document.LoadXml($unsigned)
                $key = [Security.Cryptography.RSACng]::new(2048)
                $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($Subject, $key, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $usage = New-Object Security.Cryptography.OidCollection
                [void]$usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
                $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $false))
                $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1))
                $signedXml = [Security.Cryptography.Xml.SignedXml]::new($document)
                $signedXml.SigningKey = $key
                $signedXml.SignedInfo.SignatureMethod = 'http://www.w3.org/2001/04/xmldsig-more#rsa-sha256'
                $reference = [Security.Cryptography.Xml.Reference]::new('')
                $reference.DigestMethod = 'http://www.w3.org/2001/04/xmlenc#sha256'
                $reference.AddTransform([Security.Cryptography.Xml.XmlDsigEnvelopedSignatureTransform]::new())
                $signedXml.AddReference($reference)
                $keyInfo = [Security.Cryptography.Xml.KeyInfo]::new()
                $keyInfo.AddClause([Security.Cryptography.Xml.KeyInfoX509Data]::new([Security.Cryptography.X509Certificates.X509Certificate]$certificate))
                $signedXml.KeyInfo = $keyInfo
                $signedXml.ComputeSignature()
                [void]$document.DocumentElement.AppendChild($document.ImportNode($signedXml.GetXml(), $true))
                $path = Join-Path $Work $Name
                $document.Save($path)
                $path
            }
            foreach ($resign in @(
                    @('Lenovo catalog: a forged Lenovo certificate is refused', 'CN=Lenovo', "certificate Windows doesn't trust"),
                    @('Lenovo catalog: another company''s signature is refused', 'CN=Contoso Ltd', 'is signed by "Contoso Ltd", not Lenovo'))) {
                # Without Lenovo's signature taken off first, the copy would carry two and fail for that instead.
                if ($unsigned -ceq $text) { Add-Check SKIP $resign[0] 'couldn''t take Lenovo''s signature off: the catalog''s layout is different'; continue }
                try { $path = Save-Resigned (($resign[0] -replace '\W', '') + '.xml') $resign[1] }
                catch { Add-Check SKIP $resign[0] "couldn't sign the test copy: $(Get-Message $_.Exception)"; continue }
                Test-Text $resign[0] (Test-Catalog $path) $resign[2]
            }
        }

        # The checks behind every delete or write whose path comes from a device, a vendor's catalog or a setting
        # (the deletes and overwrites scan, 1.1.1.20): Client Cache, Driver Grabber, the driver packs, legacy packages.
        $layer8 = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDir 'Layer8.dll'))
        $utility = $layer8.GetType('Layer8.Utility', $true)
        function Test-Same([string]$Check, $Expected, $Actual) {
            if ("$Expected" -ceq "$Actual") { Add-Check PASS $Check $(if ($null -eq $Actual) { '(none)' } else { "$Actual" }) }
            else { Add-Check FAIL $Check "expected '$Expected', got '$Actual'" }
        }

        $isInside = Get-Method $utility 'IsInside'
        foreach ($case in @(
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\1a', $false, $true, 'a folder in it'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\', $false, $false, 'the folder itself'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache', $true, $true, 'the folder itself, when allowed'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\..\System32', $false, $false, 'climbing out with ..'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcacheOld\1a', $false, $false, 'a folder whose name starts the same'),
                @('\\CM01\Drivers', '\\cm01\drivers\Dell\Latitude', $false, $true, 'a share, in another case'),
                @('\\CM01\Drivers', 'D:\Drivers', $false, $false, 'another drive'),
                @('\\CM01\Drivers', 'Dell\Latitude', $false, $false, 'a relative path'),
                @('\\CM01\Drivers', 'C:\Bad:Name', $false, $false, 'a path Windows refuses'))) {
            Test-Same "Path check: $($case[4])" $case[3] $isInside.Invoke($null, [object[]]@($case[0], $case[1], $case[2]))
        }

        $safeName = Get-Method $utility 'SafeName'
        foreach ($case in @(
                @('Latitude 7450', 'Latitude 7450', 'a plain name stays'),
                @('..\..\Windows', '..-..-Windows', 'separators become dashes'),
                @('C:\Users\Public', 'C--Users-Public', 'a rooted name becomes one name'),
                @('..', '-', '".." becomes a dash'),
                @('EliteBook 840 G7 / 850 G7', 'EliteBook 840 G7 - 850 G7', 'a slash in a model name'),
                @('Name. ', 'Name', 'trailing dots and spaces go'),
                @($null, '-', 'nothing becomes a dash'))) {
            Test-Same "Folder name: $($case[2])" $case[1] $safeName.Invoke($null, [object[]]@($case[0]))
        }

        $plainFile = Get-Method $utility 'IsPlainFileName'
        foreach ($case in @(
                @('oem12.inf', $true, 'an INF name'),
                @('..\..\evil.dll', $false, 'a name with ..'),
                @('C:\Users\Public\x.bat', $false, 'a rooted name'),
                @('*.cat', $false, 'a wildcard'),
                @('..', $false, '".."'),
                @('', $false, 'nothing'))) {
            Test-Same "Plain file name: $($case[2])" $case[1] $plainFile.Invoke($null, [object[]]@($case[0]))
        }

        $cacheShare = Get-Method ($device.GetType('Layer8.Device.ClientActions', $true)) 'CacheItemShare'
        foreach ($case in @(
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\1a', '\\DEV02\C$\Windows\ccmcache\1a', 'an item in the cache'),
                @('D:\ccmcache', 'D:\ccmcache\b', '\\DEV02\D$\ccmcache\b', 'a cache on another drive'),
                @('C:\Windows\ccmcache', 'C:', $null, 'a Location of C: is refused'),
                @('C:\Windows\ccmcache', 'C:\', $null, 'the root of C: is refused'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache', $null, 'the cache folder itself is refused'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\1a\sub', $null, 'a folder two down is refused'),
                @('C:\Windows\ccmcache', 'C:\Windows\ccmcache\..\System32', $null, 'climbing out with .. is refused'),
                @('C:\', 'C:\Users', $null, 'a cache folder at the root of a drive is refused'),
                @('C:\Windows\ccmcache', '\\CM01\Share\x', $null, 'a share is refused'),
                @($null, 'C:\Windows\ccmcache\1a', $null, 'no cache folder: refused'))) {
            Test-Same "Client Cache delete: $($case[3])" $case[2] $cacheShare.Invoke($null, [object[]]@('DEV02', $case[0], $case[1]))
        }

        $package = $driver.GetType('Layer8.DriverManager.Package', $true)
        $targetProblem = Get-Method $package 'TargetProblem'
        Test-Same 'Legacy package folder: a folder of its own passes' $null $targetProblem.Invoke($null, [object[]]@('\\CM01\Legacy', '\\CM01\Drivers\Dell-Latitude-x64', '\\CM01\Legacy\Dell-Latitude-x64'))
        Test-Text 'Legacy package folder: the same as the source is refused' $targetProblem.Invoke($null, [object[]]@('\\CM01\Drivers', '\\CM01\Drivers\Dell-Latitude-x64', '\\CM01\Drivers\Dell-Latitude-x64')) 'overlap'
        Test-Text 'Legacy package folder: outside the setting is refused' $targetProblem.Invoke($null, [object[]]@('\\CM01\Legacy', '\\CM01\Drivers\x', '\\CM01\Other\x')) "isn't inside the package folder"
        $pack = [Activator]::CreateInstance($package, $true)
        $package.GetProperty('Vendor').SetValue($pack, 'Dell')
        $package.GetProperty('Model').GetSetMethod($true).Invoke($pack, [object[]]@('..\..\Windows')) | Out-Null
        Test-Same 'Driver pack folder: a catalog name with ..\ stays one folder' 'Dell\..-..-Windows\Windows 11' $package.GetMethod('GenerateModelFolderName').Invoke($pack, [object[]]@('Windows 11', 'True'))

        # The hash the driver importers compare: a file that changes size changes it, and it no longer goes through %TEMP%.
        # [string]: a cmdlet's output is a PSObject, and reflection's Invoke won't turn that into a string.
        [string]$hashFolder = Join-Path $Work 'hash'
        New-Item -ItemType Directory -Path $hashFolder | Out-Null
        [string]$hashFile = Join-Path $hashFolder 'driver.inf'
        $hash = Get-Method $utility 'CreateHashForFolder'
        Test-Same 'Folder hash: an empty folder has none' $null $hash.Invoke($null, [object[]]@($hashFolder))
        [IO.File]::WriteAllText($hashFile, 'DriverVer=1')
        [IO.File]::SetLastWriteTimeUtc($hashFile, [datetime]'2026-01-01')
        $first = $hash.Invoke($null, [object[]]@($hashFolder))
        [IO.File]::WriteAllText($hashFile, 'DriverVer=1.2')
        [IO.File]::SetLastWriteTimeUtc($hashFile, [datetime]'2026-01-01')
        $second = $hash.Invoke($null, [object[]]@($hashFolder))
        if ($first -and $second -and $first -ne $second) { Add-Check PASS 'Folder hash: a file that changed size changes it' "$($first.Substring(0, 12))... to $($second.Substring(0, 12))..." }
        else { Add-Check FAIL 'Folder hash: a file that changed size changes it' "the same hash before and after: $first" }
        [IO.File]::WriteAllText((Join-Path $hashFolder 'version.version'), '')
        Test-Same 'Folder hash: .version files are left out' $second $hash.Invoke($null, [object[]]@($hashFolder))

        # A pack that fails deletes its temp folder, but only inside the download folder's extract folder.
        $deleteTemp = Get-Method $utility 'DeleteTempFolder'
        [string]$extract = Join-Path $Work 'extract'
        [string]$inside = Join-Path $extract 'Dell-Latitude-Windows 11'
        [string]$outside = Join-Path $Work 'not-temp'
        New-Item -ItemType Directory -Path $inside, $outside | Out-Null
        [void]$deleteTemp.Invoke($null, [object[]]@($extract, $outside))
        if (Test-Path -LiteralPath $outside) { Add-Check PASS 'Temp cleanup: a folder outside the extract folder stays' $outside }
        else { Add-Check FAIL 'Temp cleanup: a folder outside the extract folder stays' "$outside was deleted" }
        [void]$deleteTemp.Invoke($null, [object[]]@($extract, $inside))
        if (-not (Test-Path -LiteralPath $inside)) { Add-Check PASS 'Temp cleanup: a pack''s temp folder goes' $inside }
        else { Add-Check FAIL 'Temp cleanup: a pack''s temp folder goes' "$inside is still there" }

        $csvField = Get-Method ($device.GetType('Layer8.Device.DeviceProgressDialog', $true)) 'CsvField'
        foreach ($case in @(
                @('=1+2', "'=1+2", 'a formula becomes text'),
                @('@SUM(A1)', "'@SUM(A1)", 'an @ formula becomes text'),
                @('-2+3', "'-2+3", 'a minus formula becomes text'),
                @('=HYPERLINK("x")', '"''=HYPERLINK(""x"")"', 'a formula with quotes'),
                @('-5', '-5', 'a negative number stays'),
                @('Notepad++', 'Notepad++', 'a plus at the end stays'),
                @('a,b', '"a,b"', 'a comma is quoted'))) {
            Test-Same "Export field: $($case[2])" $case[1] $csvField.Invoke($null, [object[]]@($case[0]))
        }

        # Secrets on the clipboard (the secrets pass, 1.1.2.23): a copied LAPS or BitLocker password carries the
        # formats that keep it out of clipboard history and the cloud clipboard, and BitLocker passwords are hidden.
        $secret = '111111-222222'
        $secretData = (Get-Method $utility 'SecretDataObject').Invoke($null, [object[]]@($secret))
        Test-Same 'Copied secret: the text is the secret' $secret $secretData.GetData('UnicodeText')
        foreach ($format in 'ExcludeClipboardContentFromMonitorProcessing', 'CanIncludeInClipboardHistory', 'CanUploadToCloudClipboard') {
            Test-Same "Copied secret: marked $format" $true $secretData.GetDataPresent($format)
        }
        foreach ($format in 'CanIncludeInClipboardHistory', 'CanUploadToCloudClipboard') {
            Test-Same "Copied secret: $format is 0 (no)" 0 ([BitConverter]::ToInt32($secretData.GetData($format).ToArray(), 0))
        }
        $mask = Get-Method ($device.GetType('Layer8.Device.BitLockerKeys+KeyDialog', $true)) 'Mask'
        $dots = ([string][char]0x25CF) * 6
        Test-Same 'BitLocker password hidden: digits become dots, the groups stay' "$dots-$dots" $mask.Invoke($null, [object[]]@($secret))

        # The security pass (1.1.2.24). WQL text, as every query with a name in it escapes it. The SMS Provider
        # side is checked in the Devices section, with the same escaping.
        $wqlText = Get-Method $utility 'WqlText'
        foreach ($case in @(
                @("O'Brien", "O\'Brien", 'a quote'),
                @('C:\Drivers', 'C:\\Drivers', 'a backslash'),
                @("x' OR Name LIKE '%", "x\' OR Name LIKE \'%", 'a name built to change the query'),
                @('Layer8_Test', 'Layer8_Test', 'an underscore stays as it is'))) {
            Test-Same "WQL text: $($case[2])" $case[1] $wqlText.Invoke($null, [object[]]@($case[0]))
        }
        Test-Same 'WQL text: nothing' '' $wqlText.Invoke($null, [object[]]@($null))
        $wqlLike = Get-Method $utility 'WqlEscapeString'
        foreach ($case in @(
                @('50%_[x]', '50[%][_][[]x]', 'wildcards and a bracket taken literally'),
                @("O'Brien\", "O\'Brien\\", 'a quote and a backslash'))) {
            Test-Same "WQL LIKE text: $($case[2])" $case[1] $wqlLike.Invoke($null, [object[]]@($case[0]))
        }

        # Device names before a \\name path or a WMI connection.
        $isComputer = Get-Method $utility 'IsComputerName'
        foreach ($case in @(
                @('DEV02', $true, 'a NetBIOS name'),
                @('dev02.lab.local', $true, 'an FQDN'),
                @('..\evil', $false, 'a name with ..\'),
                @('\\CM01\Share', $false, 'a share'),
                @('x64 Unknown Computer (x64 Unknown Computer)', $false, 'an unknown computer record'),
                @($null, $false, 'nothing'))) {
            Test-Same "Computer name: $($case[2])" $case[1] $isComputer.Invoke($null, [object[]]@($case[0]))
        }
        $getWmiScope = Get-Method $utility 'GetWMIScope'
        try { [void]$getWmiScope.Invoke($null, [object[]]@('..\..\evil', 'cimv2', $null, $null)); $refused = $null } catch { $refused = Get-Message $_.Exception }
        Test-Text 'WMI connection: a name that isn''t a computer name is refused' $refused "isn't a computer name"

        # LAPS and BitLocker: which of the computer objects found is the device.
        $matching = Get-Method ($device.GetType('Layer8.Device.BitLockerKeys', $true)) 'Matching'
        foreach ($case in @(
                @([string[]]@('DEV02$'), '0', 'one object'),
                @([string[]]@('DEV02-OLD$', 'DEV02$'), '1', 'the one whose account name is the name'),
                @([string[]]@('DEV02$', 'dev02$'), '0,1', 'two with the account name: both, so neither is shown'),
                @([string[]]@('PC1$', 'PC2$'), '0,1', 'two named it, neither by account: both, so neither is shown'),
                @([string[]]@(), '', 'none'))) {
            Test-Same "Computer object: $($case[2])" $case[1] (@($matching.Invoke($null, [object[]]@($case[0].PSObject.BaseObject, 'DEV02'))) -join ',')
        }

        # XML from vendors and devices: no DTDs.
        $loadXml = $utility.GetMethod('LoadXml', [Type[]]@([string]))
        [string]$plainXml = Join-Path $Work 'plain.xml'
        [IO.File]::WriteAllText($plainXml, '<Report><Check Description="x">Passed</Check></Report>')
        Test-Same 'XML: a plain file is read' 'Passed' ($loadXml.Invoke($null, [object[]]@($plainXml))).Value
        [string]$dtdXml = Join-Path $Work 'dtd.xml'
        [IO.File]::WriteAllText($dtdXml, '<?xml version="1.0"?><!DOCTYPE r [<!ENTITY e "expanded">]><r>&e;</r>')
        try { [void]$loadXml.Invoke($null, [object[]]@($dtdXml)); $refused = $null } catch { $refused = Get-Message $_.Exception }
        Test-Text 'XML: a file with a DTD is refused' $refused 'DTD'

        # The hash a Dell pack (and a Lenovo pack) has to match.
        $requireHash = Get-Method $authenticode 'RequireHash'
        function Test-Hash([string]$Path, [string]$Expected, [bool]$Required) {
            try { [void]$requireHash.Invoke($null, [object[]]@($Path, $Expected, 'Dell', $Required)); $null } catch { Get-Message $_.Exception }
        }
        [string]$hashed = Join-Path $Work 'pack.cab'
        [IO.File]::WriteAllText($hashed, 'not really a pack')
        $sha256 = (Get-FileHash -LiteralPath $hashed -Algorithm SHA256).Hash
        $md5 = (Get-FileHash -LiteralPath $hashed -Algorithm MD5).Hash
        Test-Same 'Pack hash: the right SHA-256 passes' $null (Test-Hash $hashed $sha256 $true)
        Test-Same 'Pack hash: the right MD5 passes, in lower case' $null (Test-Hash $hashed $md5.ToLowerInvariant() $true)
        Test-Text 'Pack hash: another hash is refused' (Test-Hash $hashed ('0' * 64) $true) 'doesn''t match the hash in Dell''s catalog'
        Test-Text 'Pack hash: no hash, where one is needed, is refused' (Test-Hash $hashed '' $true) 'has no hash'
        Test-Same 'Pack hash: no hash, where the signature checks it (Lenovo), passes' $null (Test-Hash $hashed '' $false)

        # A pack is locked from its checks until it has run: still readable and runnable, not changeable.
        $lock = Get-Method $authenticode 'Lock'
        [string]$lockedExe = Join-Path $Work 'locked.exe'
        Copy-Item -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'hostname.exe') -Destination $lockedExe
        $held = $lock.Invoke($null, [object[]]@($lockedExe))
        try {
            try { $writer = [IO.File]::Open($lockedExe, 'Open', 'Write', 'ReadWrite'); $writer.Dispose(); $opened = 'it opened for writing' } catch { $opened = $null }
            Test-Same 'Locked pack: it can''t be written' $null $opened
            try { [IO.File]::Move($lockedExe, (Join-Path $Work 'moved.exe')); $moved = 'it was renamed' } catch { $moved = $null }
            Test-Same 'Locked pack: it can''t be renamed or replaced' $null $moved
            $start = New-Object Diagnostics.ProcessStartInfo $lockedExe
            $start.UseShellExecute = $true
            $start.WindowStyle = 'Hidden'
            $ran = [Diagnostics.Process]::Start($start)
            if ($ran.WaitForExit(30000) -and $ran.ExitCode -eq 0) { Add-Check PASS 'Locked pack: it still runs (the way the packs start)' 'hostname.exe copy, exit code 0' }
            else { Add-Check FAIL 'Locked pack: it still runs (the way the packs start)' 'it didn''t finish with exit code 0' }
            $ran.Dispose()
        }
        finally { $held.Dispose() }
        if ($signature.Status -eq 'Valid') {
            [string]$lockedSigned = Join-Path $Work 'locked-signed.exe'
            Copy-Item -LiteralPath $signedFile -Destination $lockedSigned
            $held = $lock.Invoke($null, [object[]]@($lockedSigned))
            try { Test-Same 'Locked pack: the signature check still reads it' $null (Test-Signed $lockedSigned ([regex]'^Microsoft Corporation$') 'Microsoft') }
            finally { $held.Dispose() }
        }

        # Dell's and HP's catalogs, the copies their wizards keep: signed by the vendor, and every Dell pack has a hash.
        $extractSigned = Get-Method $authenticode 'ExtractSignedCab'
        $dellSigner = $authenticode.GetField('DellSigner', $flags).GetValue($null)
        foreach ($vendor in @(
                @('Dell', 'DriverPackCatalog.cab', $dellSigner, 'the Dell Driver Packs wizard'),
                @('HP', 'HPClientDriverPackCatalog.cab', $hpSigner, 'the HP Driver Packs wizard'))) {
            [string]$catalogCab = Join-Path ([IO.Path]::GetTempPath()) $vendor[1]
            if (-not (Test-Path -LiteralPath $catalogCab)) { Add-Check SKIP "$($vendor[0]) catalog checks" "no copy at $catalogCab`: open $($vendor[3]) once"; continue }
            $copyDate = '{0:yyyy-MM-dd HH:mm}' -f (Get-Item -LiteralPath $catalogCab).LastWriteTime
            [string]$catalogFolder = Join-Path $Work "$($vendor[0])-catalog"
            try { $files = @($extractSigned.Invoke($null, [object[]]@($catalogCab, $catalogFolder, $vendor[2], $vendor[0]))); $problem = $null } catch { $files = @(); $problem = Get-Message $_.Exception }
            $signer = (Get-AuthenticodeSignature -LiteralPath $catalogCab).SignerCertificate
            $signerName = if ($signer) { $signer.GetNameInfo('SimpleName', $false) } else { 'no signature' }
            if ($null -eq $problem) { Add-Check PASS "$($vendor[0]) catalog: $($vendor[0])'s copy passes" "signed by $signerName, downloaded $copyDate" }
            else { Add-Check FAIL "$($vendor[0]) catalog: $($vendor[0])'s copy passes" "$problem (signer: $signerName, downloaded $copyDate)" }
            $bytes = [IO.File]::ReadAllBytes($catalogCab)
            $middle = [int]($bytes.Length / 2)
            $bytes[$middle] = [byte]($bytes[$middle] -bxor 0xFF)
            [string]$changedCab = Join-Path $Work "changed-$($vendor[1])"
            [IO.File]::WriteAllBytes($changedCab, $bytes)
            [string]$changedFolder = Join-Path $Work "$($vendor[0])-changed"
            try { [void]$extractSigned.Invoke($null, [object[]]@($changedCab, $changedFolder, $vendor[2], $vendor[0])); $refused = $null } catch { $refused = Get-Message $_.Exception }
            Test-Text "$($vendor[0]) catalog: a changed copy is refused" $refused "isn't validly signed (WinVerifyTrust 0x"
            if ($vendor[0] -eq 'Dell' -and $files) {
                $packageType = $driver.GetType('Layer8.DriverManager.DellDriverPackage', $true)
                $xml = $utility.GetMethod('LoadXml', [Type[]]@([string])).Invoke($null, [object[]]@([string]$files[0]))
                $read = 0; $unread = 0; $sha = 0; $noHash = New-Object System.Collections.Generic.List[string]
                foreach ($node in @($xml.Elements() | Where-Object { $_.Name.LocalName -eq 'DriverPackage' })) {
                    try { $pack = [Activator]::CreateInstance($packageType, [object[]]@($node.PSObject.BaseObject)) } catch { $unread++; continue }
                    $read++
                    $hash = $packageType.GetProperty('CatalogHash').GetValue($pack)
                    if ($hash.Length -eq 64) { $sha++ } elseif ($hash.Length -ne 32) { $noHash.Add($packageType.GetProperty('Model').GetValue($pack)) }
                }
                if ($read -eq 0) { Add-Check FAIL 'Dell catalog: every pack has a hash' "no packs read from $($files[0]) ($unread couldn't be)" }
                elseif ($noHash.Count -eq 0) { Add-Check PASS 'Dell catalog: every pack has a hash' "$read packs: $sha SHA-256, $($read - $sha) MD5" }
                else { Add-Check FAIL 'Dell catalog: every pack has a hash' "$($noHash.Count) of $read have none, so they'd be refused: $(@($noHash | Select-Object -First 5) -join ', ')" }
            }
        }

        # Ctrl+C in a list, and a failure with no message.
        Add-Type -AssemblyName System.Windows.Forms
        $list = New-Object Windows.Forms.ListView
        # Selection is only kept once the list has a window handle, as it always has in the console.
        [void]$list.Handle
        foreach ($row in @(@('Contoso', 'Driver A', '1.0'), @('Fabrikam', 'Driver B', '2.0'))) { [void]$list.Items.Add((New-Object Windows.Forms.ListViewItem -ArgumentList (, [string[]]$row))) }
        $rowsText = Get-Method $utility 'SelectedRowsText'
        Test-Same 'List copy: nothing selected copies nothing' '' $rowsText.Invoke($null, [object[]]@($list.PSObject.BaseObject))
        $list.Items[0].Selected = $true
        $list.Items[1].Selected = $true
        Test-Same 'List copy: rows by line, columns by tab' "Contoso`tDriver A`t1.0`r`nFabrikam`tDriver B`t2.0" $rowsText.Invoke($null, [object[]]@($list.PSObject.BaseObject))
        $list.Dispose()
        $describeFailure = Get-Method ($device.GetType('Layer8.Device.DeviceProgressDialog', $true)) 'DescribeFailure'
        [object[]]$failure = @('DEV02', [Exception]::new(''), $false)
        Test-Same 'Progress dialog: a failure with no message names what failed' 'Error: Exception' $describeFailure.Invoke($null, $failure)
    }
    catch {
        Add-Check FAIL 'The library checks stopped' "script error: $(Get-Message $_.Exception)"
    }
    $out
}

function Test-Library {
    if (-not $consoleBin) { Add-Result SKIP 'Library' 'no ConfigMgr console on this computer (SMS_ADMIN_UI_PATH)'; return }
    foreach ($dll in $dlls) { if (-not (Test-Path -LiteralPath (Join-Path $buildDir $dll))) { Add-Result FAIL 'Library' "$dll isn't in Build\Release: run tools\Build-Layer8Msi.ps1"; return } }
    $libraryWork = Join-Path $work 'library'
    New-Item -ItemType Directory -Path $libraryWork | Out-Null
    # A job's PowerShell process is a fresh one: -RunAs32 makes it 32-bit, and it loads no profile.
    $job = Start-Job -RunAs32 -ScriptBlock $libraryChecks -ArgumentList $buildDir, $consoleBin, $LenovoCatalog, $libraryWork, $resolverSource
    foreach ($check in @(Receive-Job -Job $job -Wait -AutoRemoveJob)) { Add-Result $check.Result $check.Check $check.Detail }
}

# --- Settings ---------------------------------------------------------------------------------------------------------

# Layer8 Settings as Layer8 reads them: its defaults and limits from Layer8Settings.cs, its values from HKCU.
function Get-Layer8Settings {
    $source = Get-Content -LiteralPath (Join-Path $repo 'Layer8\Layer8Settings.cs') -Raw
    $const = @{}
    foreach ($match in [regex]::Matches($source, 'public const int (\w+) = (\d+);')) { $const[$match.Groups[1].Value] = [int]$match.Groups[2].Value }
    $defaultBuild = [regex]::Match($source, 'DefaultWindowsBuild = "([0-9.]+)"').Groups[1].Value
    $defaultReachable = [regex]::Match($source, 'DefaultCheckReachable = (true|false)').Groups[1].Value -eq 'true'
    $key = Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Layer8' -ErrorAction SilentlyContinue

    $readInt = {
        param([string]$Name, [int]$Fallback, [int]$Min, [int]$Max)
        $value = $null
        if ($key) { $value = $key.$Name }
        $number = 0
        if ($value -is [int]) { $number = $value }
        elseif (-not ($value -is [string] -and [int]::TryParse($value.Trim(), [ref]$number))) { return $Fallback }
        [Math]::Max($Min, [Math]::Min($Max, $number))
    }
    $settings = [ordered]@{}
    foreach ($name in 'LowDiskGB', 'LongUptimeDays', 'NotReportingDays', 'BlueScreenDays', 'NotScanningDays', 'DevicesAtOnce', 'ConfirmAbove', 'ReachableSeconds') {
        $settings[$name] = & $readInt $name $const["Default$name"] $const["Min$name"] $const["Max$name"]
        $settings["Default$name"] = $const["Default$name"]
    }
    $reachableDefault = 0
    if ($defaultReachable) { $reachableDefault = 1 }
    $settings['CheckReachable'] = (& $readInt 'CheckReachable' $reachableDefault 0 1) -eq 1

    # A build alone or a build and revision ("26100", "26300.9999"), as Layer8Settings.TryParseWindowsBuild takes it.
    $buildText = $null
    if ($key) { $buildText = $key.WindowsBuild }
    $buildMatch = [regex]::Match([string]$buildText, '^\s*(\d{5})(?:\.(\d{1,6}))?\s*$')
    if (-not $buildMatch.Success -or [int]$buildMatch.Groups[1].Value -lt $const['MinWindowsBuild']) { $buildText = $defaultBuild; $buildMatch = [regex]::Match($defaultBuild, '^(\d{5})(?:\.(\d{1,6}))?$') }
    $revision = 0
    if ($buildMatch.Groups[2].Success) { $revision = [int]$buildMatch.Groups[2].Value }
    $settings['WindowsBuildText'] = $buildText.Trim()
    $settings['DefaultWindowsBuild'] = $defaultBuild
    $settings['WindowsBuild'] = New-Object Version([int]$buildMatch.Groups[1].Value, $revision)

    $level = 'Info'
    if ($key -and $key.LogLevel -and $key.LogLevel.Trim() -in 'Debug', 'Info', 'Warn', 'Error') { $level = $key.LogLevel.Trim() }
    $settings['LogLevel'] = $level
    [pscustomobject]$settings
}

function Test-Settings {
    $settings = Get-Layer8Settings
    $labels = [ordered]@{
        LowDiskGB        = 'Low disk under (GB)'
        LongUptimeDays   = 'Long uptime (days)'
        NotReportingDays = 'Not reporting after (days)'
        BlueScreenDays   = 'Blue screens in the last (days)'
        NotScanningDays  = 'Not scanning after (days)'
    }
    # Several tested rows end "set back to ..."; a value left changed would skew the next dashboard run.
    foreach ($name in $labels.Keys) {
        $value = $settings.$name
        $default = $settings."Default$name"
        if ($value -eq $default) { Add-Result PASS $labels[$name] "$value (the default)" }
        else { Add-Result WARN $labels[$name] "$value, not the default $default`: set it back if a test changed it" }
    }
    if ($settings.WindowsBuildText -eq $settings.DefaultWindowsBuild) { Add-Result PASS 'Windows older than build' "$($settings.WindowsBuildText) (the default)" }
    else { Add-Result WARN 'Windows older than build' "$($settings.WindowsBuildText), not the default $($settings.DefaultWindowsBuild): set it back if a test changed it" }
    $reachable = 'off'
    if ($settings.CheckReachable) { $reachable = "on, $($settings.ReachableSeconds) s" }
    Add-Result INFO 'General' "$($settings.DevicesAtOnce) devices at once, ask above $($settings.ConfirmAbove), offline check $reachable, log level $($settings.LogLevel)"
}

# --- Devices -----------------------------------------------------------------------------------------------------------

$healthIssues = $null
# The dashboard's issues, in its order, and their names, from HealthCheck.cs: a tile added there shows up here.
function Get-HealthIssues {
    if ($script:healthIssues) { return $script:healthIssues }
    $source = Get-Content -LiteralPath (Join-Path $repo 'Layer8.Device\HealthCheck.cs') -Raw
    $body = [regex]::Match($source, '(?s)internal enum HealthIssue\s*\{(.*?)\}').Groups[1].Value
    $body = [regex]::Replace($body, '(?m)^\s*///.*$', '')
    $order = @($body -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\w+$' })
    $names = @{}
    foreach ($match in [regex]::Matches($source, 'case HealthIssue\.(\w+): return "([^"]+)";')) { $names[$match.Groups[1].Value] = $match.Groups[2].Value }
    $otherwise = [regex]::Match($source, 'default: return "([^"]+)";').Groups[1].Value
    foreach ($issue in $order) { if (-not $names.ContainsKey($issue)) { $names[$issue] = $otherwise } }
    $script:healthIssues = [pscustomobject]@{
        Order                 = $order
        Names                 = $names
        DefenderSignatureDays = [int][regex]::Match($source, 'DefenderSignatureDays = (\d+);').Groups[1].Value
    }
    $script:healthIssues
}
# The issues this script knows how to read. One the dashboard has and this doesn't gets "?" instead of a count.
$readableIssues = 'RestartPending', 'LowDisk', 'BitLockerOff', 'Defender', 'BlueScreen', 'MissingUpdates', 'NotScanning', 'WindowsBehind', 'Client', 'NotReporting', 'LongUptime', 'CouldNotCheck'

# RemoteTools.IsComputerName: placeholder records ("x64 Unknown Computer (x64 Unknown Computer)") aren't machines.
$computerName = '^[A-Za-z0-9][A-Za-z0-9.\-_]{0,254}$'
$currentVersionKey = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$conversionStates = 'fully decrypted', 'fully encrypted', 'encrypting', 'decrypting', 'encryption paused', 'decryption paused'

function Format-Bytes([double]$Bytes, [string]$Format) {
    $units = 'B', 'KB', 'MB', 'GB', 'TB', 'PB'
    $value = $Bytes
    $unit = 0
    while ([Math]::Abs($value) -ge 1024 -and $unit -lt $units.Count - 1) { $value /= 1024; $unit++ }
    $value.ToString($Format, [Globalization.CultureInfo]::CurrentCulture) + ' ' + $units[$unit]
}

# ClientActions.FormatUptime. [int] in PowerShell rounds where C# cuts off, so Floor.
function Format-Uptime([TimeSpan]$Uptime) {
    if ($Uptime.TotalDays -ge 1) { return '{0}d {1}h' -f [Math]::Floor($Uptime.TotalDays), $Uptime.Hours }
    if ($Uptime.TotalHours -ge 1) { return '{0}h {1}m' -f [Math]::Floor($Uptime.TotalHours), $Uptime.Minutes }
    '{0}m' -f [Math]::Max(0, [Math]::Floor($Uptime.TotalMinutes))
}

# HealthCheck.FormatAge.
function Format-Age([TimeSpan]$Age) {
    if ($Age -lt [TimeSpan]::Zero) { $Age = [TimeSpan]::Zero }
    if ($Age.TotalMinutes -lt 1) { return 'just now' }
    if ($Age.TotalHours -lt 1) { return '{0}m ago' -f [Math]::Floor($Age.TotalMinutes) }
    if ($Age.TotalHours -lt 48) { return '{0}h ago' -f [Math]::Floor($Age.TotalHours) }
    '{0}d ago' -f [Math]::Floor($Age.TotalDays)
}

# A site date: WMI text in UTC with no offset ("20261120155812.000000+***"). Null when blank (year 1 or 1970).
function ConvertFrom-SiteTime([string]$Text) {
    if (-not $Text -or $Text.Length -lt 14) { return $null }
    $styles = [Globalization.DateTimeStyles]'AssumeUniversal, AdjustToUniversal'
    $utc = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($Text.Substring(0, 14), 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$utc)) { return $null }
    if ($utc.Year -lt 2000) { return $null }
    $utc
}

function Test-AnyPort($Address, [int[]]$Ports, [int]$TimeoutMs) {
    foreach ($port in $Ports) {
        $client = New-Object Net.Sockets.TcpClient
        try { if ($client.ConnectAsync($Address, $port).Wait($TimeoutMs) -and $client.Connected) { return $true } }
        catch { }
        finally { $client.Dispose() }
    }
    $false
}

# Online Status (Ping) as Layer8 words it: two pings, then port 445 when ping is blocked.
function Get-OnlineStatus([string]$Name) {
    try { $address = [Net.Dns]::GetHostAddresses($Name) | Sort-Object { $_.AddressFamily -ne 'InterNetwork' } | Select-Object -First 1 }
    catch { return 'Not found in DNS' }
    if (-not $address) { return 'Not found in DNS' }
    $ping = New-Object Net.NetworkInformation.Ping
    try {
        for ($attempt = 0; $attempt -lt 2; $attempt++) {
            try {
                $reply = $ping.Send($address, 1000)
                if ($reply.Status -eq 'Success') { return "Online: ping $($reply.RoundtripTime) ms ($address)" }
            }
            catch { }
        }
    }
    finally { $ping.Dispose() }
    if (Test-AnyPort $address @(445) 1000) { return "Online: ping blocked, port 445 answers ($address)" }
    "Offline: no ping reply, port 445 closed ($address)"
}

function Get-RegistryValue($Session, [string]$Method, [string]$Key, [string]$Value) {
    $result = Invoke-CimMethod -CimSession $Session -Namespace 'root\default' -ClassName StdRegProv -MethodName $Method -Arguments @{ hDefKey = [uint32]2147483650; sSubKeyName = $Key; sValueName = $Value }
    if ($result.ReturnValue -ne 0) { return $null }
    if ($Method -eq 'GetDWORDValue') { return $result.uValue }
    $result.sValue
}

function Test-SubKey($Session, [string]$Key, [string]$SubKey) {
    $result = Invoke-CimMethod -CimSession $Session -Namespace 'root\default' -ClassName StdRegProv -MethodName EnumKey -Arguments @{ hDefKey = [uint32]2147483650; sSubKeyName = $Key }
    @($result.sNames) -contains $SubKey
}

function Test-CimError($ErrorRecord, [string[]]$Codes) {
    $ErrorRecord.Exception -is [Microsoft.Management.Infrastructure.CimException] -and "$($ErrorRecord.Exception.NativeErrorCode)" -in $Codes
}

# HealthCheck.StopCode: the stop code from the System log's BugCheck event (1001) after the dump.
function Get-StopCode($Session, [datetime]$Written) {
    try {
        $from = [Management.ManagementDateTimeConverter]::ToDmtfDateTime($Written.AddMinutes(-10))
        foreach ($entry in Get-CimInstance -CimSession $Session -Query "SELECT InsertionStrings FROM Win32_NTLogEvent WHERE Logfile = 'System' AND EventCode = 1001 AND TimeGenerated >= '$from'") {
            $first = @($entry.InsertionStrings)[0]
            if ($first -match '^\s*0x([0-9A-Fa-f]{1,8})\b') { return '0x' + $Matches[1].ToUpperInvariant().PadLeft(8, '0') }
        }
    }
    catch { }
    $null
}

# One check, as HealthCheck.Read runs it: a check that can't be read shows "?" and is named under Couldn't check.
function Invoke-Check([string]$CheckName, $IssueSet, $UnreadList, [scriptblock]$Reader) {
    try { & $Reader }
    catch {
        $exception = $_.Exception
        $why = $exception.Message.Trim().TrimEnd('.').ToLowerInvariant()
        if ($exception -is [UnauthorizedAccessException] -or ($exception -is [Microsoft.Management.Infrastructure.CimException] -and "$($exception.NativeErrorCode)" -eq 'AccessDenied')) { $why = 'access denied' }
        $UnreadList.Add("$CheckName ($why)")
        [void]$IssueSet.Add('CouldNotCheck')
        '?'
    }
}

# One device, read the way HealthCheck.Check reads it, in the same order.
function Read-Device([string]$Name, $Settings) {
    $issues = New-Object 'System.Collections.Generic.HashSet[string]'
    $unread = New-Object System.Collections.Generic.List[string]
    $row = [ordered]@{
        Device = $Name; State = 'Checked'; Status = ''; Online = (Get-OnlineStatus $Name)
        'Signed in' = ''; Up = ''; 'C: free' = ''; 'Blue screen' = ''; Windows = ''; Client = ''
        Restart = ''; BitLocker = ''; Defender = ''; Updates = ''; 'Update scan' = ''; 'Policy request' = ''; Issues = ''
        IssueSet = $issues; Unread = $unread; NotReporting = $false
        # The values behind the text, for the Tools section: uptime, bytes, BitLocker's and Defender's own fields.
        Raw = @{ ClientSdk = $false }
    }
    $done = { [pscustomobject]$row }

    # The quick offline check Layer8 Settings turns on: port 135 or 445 within the set time.
    if ($Settings.CheckReachable -and -not (Test-AnyPort $Name @(135, 445) ($Settings.ReachableSeconds * 1000))) {
        $row.State = 'Offline'; $row.Status = "Offline: ports 135 and 445 don't answer"
        return & $done
    }
    try {
        $session = New-CimSession -ComputerName $Name -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 60
        [void](Get-CimInstance -CimSession $session -ClassName Win32_OperatingSystem -Property BuildNumber)
    }
    catch {
        $row.State = 'Failed'; $row.Status = "Error: $($_.Exception.Message.Trim())"
        return & $done
    }

    try {
        $state = @{ ClientSdk = $false }
        $row['Signed in'] = Invoke-Check 'Signed in' $issues $unread {
            $users = New-Object System.Collections.Generic.List[string]
            foreach ($explorer in Get-CimInstance -CimSession $session -ClassName Win32_Process -Filter "Name = 'explorer.exe'") {
                $owner = Invoke-CimMethod -CimSession $session -InputObject $explorer -MethodName GetOwner
                if (-not $owner.User) { continue }
                $who = $owner.User
                if ($owner.Domain) { $who = "$($owner.Domain)\$($owner.User)" }
                if (-not ($users -contains $who)) { $users.Add($who) }
            }
            $row.Raw.SignedIn = @($users)
            if ($users.Count -eq 0) { 'No one' } else { $users -join ', ' }
        }

        $row.Up = Invoke-Check 'Uptime' $issues $unread {
            # The boot time first, as the dashboard reads it: the uptime counter's first read took 43 seconds on a server.
            $os = Get-CimInstance -CimSession $session -ClassName Win32_OperatingSystem -Property LastBootUpTime, LocalDateTime
            $uptime = $null
            if ($os.LastBootUpTime -and $os.LocalDateTime -and $os.LocalDateTime -ge $os.LastBootUpTime) { $uptime = $os.LocalDateTime - $os.LastBootUpTime }
            else {
                $counter = Get-CimInstance -CimSession $session -ClassName Win32_PerfFormattedData_PerfOS_System -Property SystemUpTime
                if ($counter.SystemUpTime -gt 0) { $uptime = [TimeSpan]::FromSeconds($counter.SystemUpTime) }
            }
            if ($null -eq $uptime) { throw 'no uptime counter or boot time' }
            $row.Raw.Uptime = $uptime
            if ($uptime.TotalDays -ge $Settings.LongUptimeDays) { [void]$issues.Add('LongUptime') }
            Format-Uptime $uptime
        }

        $row['C: free'] = Invoke-Check 'Disk' $issues $unread {
            $disk = Get-CimInstance -CimSession $session -ClassName Win32_LogicalDisk -Filter "DeviceID = 'C:'" -Property FreeSpace, Size
            if (-not $disk) { return 'No C: drive' }
            $free = [double]$disk.FreeSpace
            $size = [double]$disk.Size
            $row.Raw.FreeBytes = $free
            $row.Raw.SizeBytes = $size
            if ($free -lt $Settings.LowDiskGB * 1GB) { [void]$issues.Add('LowDisk') }
            $text = Format-Bytes $free '0.#'
            if ($size -gt 0) { $text += ' of ' + (Format-Bytes $size '0') }
            $text
        }

        $row['Blue screen'] = Invoke-Check 'Blue screen' $issues $unread {
            $window = (Get-Date).AddDays(-$Settings.BlueScreenDays)
            $dumps = @(Get-CimInstance -CimSession $session -Query "SELECT LastModified FROM CIM_DataFile WHERE Drive = 'C:' AND Path = '\\Windows\\Minidump\\' AND Extension = 'dmp'" |
                ForEach-Object LastModified | Where-Object { $_ -ge $window })
            if ($dumps.Count -eq 0) { return 'None' }
            [void]$issues.Add('BlueScreen')
            $newest = $dumps | Sort-Object -Descending | Select-Object -First 1
            $code = Get-StopCode $session $newest
            $text = ''
            if ($dumps.Count -gt 1) { $text = "$($dumps.Count), last " }
            $text += $newest.ToString('g')
            if ($code) { $text += " ($code)" }
            $text
        }

        $row.Windows = Invoke-Check 'Windows' $issues $unread {
            $os = Get-CimInstance -CimSession $session -ClassName Win32_OperatingSystem -Property Caption, BuildNumber, ProductType
            $build = 0
            if (-not $os -or -not [int]::TryParse([string]$os.BuildNumber, [ref]$build)) { throw 'no Windows build number' }
            $revision = Get-RegistryValue $session 'GetDWORDValue' $currentVersionKey 'UBR'
            $revisionNumber = 0
            if ($null -ne $revision) { $revisionNumber = [int][uint32]$revision }
            $windows = New-Object Version($build, $revisionNumber)
            $buildText = "$build"
            if ($null -ne $revision) { $buildText = $windows.ToString() }
            $productType = 1
            if ($null -ne $os.ProductType) { $productType = [int]$os.ProductType }
            # Servers and domain controllers follow their own build line and aren't held to a workstation build.
            if ($productType -ne 1) {
                $server = [regex]::Match([string]$os.Caption, 'Server \d{4}( R2)?')
                $serverName = 'Server'
                if ($server.Success) { $serverName = $server.Value }
                return (@($serverName, $buildText) | Where-Object { $_ }) -join ', '
            }
            $release = Get-RegistryValue $session 'GetStringValue' $currentVersionKey 'DisplayVersion'
            if (-not $release) { $release = Get-RegistryValue $session 'GetStringValue' $currentVersionKey 'ReleaseId' }
            if ($windows -lt $Settings.WindowsBuild) { [void]$issues.Add('WindowsBehind') }
            (@($release, $buildText) | Where-Object { $_ }) -join ', '
        }

        $row.Client = Invoke-Check 'Client' $issues $unread {
            $service = Get-CimInstance -CimSession $session -ClassName Win32_Service -Filter "Name = 'CcmExec'" -Property State
            if (-not $service) { [void]$issues.Add('Client'); return 'Not installed' }
            $serviceState = 'in an unknown state'
            if ($service.State) { $serviceState = $service.State }
            if ($serviceState -ne 'Running') { [void]$issues.Add('Client'); return 'Service ' + $serviceState.ToLowerInvariant() }
            try { $client = Get-CimInstance -CimSession $session -Namespace 'root\ccm' -ClassName SMS_Client -Property ClientVersion }
            catch {
                # A site server's management point runs CcmExec too, with no client.
                if (Test-CimError $_ 'InvalidNamespace', 'InvalidClass') { [void]$issues.Add('Client'); return 'Not installed' }
                throw
            }
            $state.ClientSdk = $true
            $row.Raw.ClientSdk = $true
            if ((Get-RegistryValue $session 'GetStringValue' 'SOFTWARE\Microsoft\CCM\CcmExec' 'ProvisioningMode') -eq 'true') { [void]$issues.Add('Client'); return 'Provisioning mode' }
            if ($client.ClientVersion) { $client.ClientVersion } else { 'Running' }
        }

        $row.Restart = Invoke-Check 'Restart' $issues $unread {
            $reasons = New-Object System.Collections.Generic.List[string]
            if ($state.ClientSdk) {
                $pending = Invoke-CimMethod -CimSession $session -Namespace 'root\ccm\ClientSDK' -ClassName CCM_ClientUtilities -MethodName DetermineIfRebootPending
                if ($pending.RebootPending -or $pending.IsHardRebootPending) {
                    $reason = 'ConfigMgr'
                    if ($pending.IsHardRebootPending) { $reason = 'ConfigMgr (hard restart)' }
                    if ($pending.RebootDeadline -is [datetime] -and $pending.RebootDeadline.Year -gt 2000) { $reason += ', deadline ' + $pending.RebootDeadline.ToString('g') }
                    $reasons.Add($reason)
                }
            }
            if (Test-SubKey $session 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing' 'RebootPending') { $reasons.Add('Windows servicing') }
            if (Test-SubKey $session 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update' 'RebootRequired') { $reasons.Add('Windows Update') }
            if ($reasons.Count -eq 0) { return 'No' }
            [void]$issues.Add('RestartPending')
            $reasons -join '; '
        }

        $row.BitLocker = Invoke-Check 'BitLocker' $issues $unread {
            # Read only, like BitLocker Status: protection and conversion, never a recovery password.
            try { $volume = Get-CimInstance -CimSession $session -Namespace 'root\cimv2\Security\MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -Filter "DriveLetter = 'C:'" }
            catch { if (Test-CimError $_ 'InvalidNamespace') { $row.Raw.BitLocker = 'NoNamespace'; return 'Not available' }; throw }
            if (-not $volume) { $row.Raw.BitLocker = 'NoVolume'; return 'C: not encryptable' }
            $protection = 2
            if ($null -ne $volume.ProtectionStatus) { $protection = [int]$volume.ProtectionStatus }
            $conversion = ''
            $status = Invoke-CimMethod -CimSession $session -InputObject $volume -MethodName GetConversionStatus
            if ($status -and $status.ReturnValue -eq 0) {
                $number = [int]$status.ConversionStatus
                $conversion = "conversion state $number"
                if ($number -lt $conversionStates.Count) { $conversion = $conversionStates[$number] }
                if ($number -ge 2 -and $number -le 5) { $conversion += " $($status.EncryptionPercentage)%" }
            }
            $row.Raw.BitLocker = @{ Protection = $protection; Conversion = $conversion }
            if ($protection -eq 0) {
                [void]$issues.Add('BitLockerOff')
                if ($conversion -eq 'fully encrypted') { return 'Suspended (encrypted)' }
                if ($conversion -eq '' -or $conversion -eq 'fully decrypted') { return 'Off' }
                return "Off, $conversion"
            }
            if ($protection -eq 1) {
                if ($conversion -eq '' -or $conversion -eq 'fully encrypted') { return 'On' }
                return "On, $conversion"
            }
            'Unknown (locked)'
        }

        $row.Defender = Invoke-Check 'Defender' $issues $unread {
            try { $status = Get-CimInstance -CimSession $session -Namespace 'root\Microsoft\Windows\Defender' -ClassName MSFT_MpComputerStatus }
            catch { if (Test-CimError $_ 'InvalidNamespace') { $row.Raw.Defender = 'NoNamespace'; return 'Not installed' }; throw }
            if (-not $status) { $row.Raw.Defender = 'NoStatus'; return 'No status' }
            $row.Raw.Defender = $status
            # Another antivirus is in charge and Defender stands back on purpose.
            if ($status.CimInstanceProperties['AMRunningMode'] -and "$($status.AMRunningMode)" -like '*Passive*') { return 'Passive' }
            $problems = New-Object System.Collections.Generic.List[string]
            if (-not $status.AntivirusEnabled) { $problems.Add('antivirus off') }
            if (-not $status.RealTimeProtectionEnabled) { $problems.Add('real-time off') }
            $days = (Get-HealthIssues).DefenderSignatureDays
            if ($null -ne $status.AntivirusSignatureAge -and $status.AntivirusSignatureAge -gt $days) { $problems.Add("signatures $($status.AntivirusSignatureAge) days old") }
            if ($problems.Count -eq 0) { return 'OK' }
            [void]$issues.Add('Defender')
            $text = $problems -join ', '
            $text.Substring(0, 1).ToUpperInvariant() + $text.Substring(1)
        }

        $row.Updates = Invoke-Check 'Updates' $issues $unread {
            if (-not $state.ClientSdk) { return 'No client' }
            # Counted by KB, as List Missing Updates counts them (a client's store can list one KB twice).
            $missing = New-Object System.Collections.Generic.List[string]
            foreach ($update in Get-CimInstance -CimSession $session -Namespace 'root\ccm\ClientSDK' -Query 'SELECT ArticleID, Name FROM CCM_SoftwareUpdate WHERE ComplianceState = 0') {
                $updateKey = '(unnamed update)'
                if ($update.ArticleID) { $updateKey = 'KB' + $update.ArticleID } elseif ($update.Name) { $updateKey = $update.Name }
                if (-not ($missing -contains $updateKey)) { $missing.Add($updateKey) }
            }
            if ($missing.Count -eq 0) { return 'None' }
            [void]$issues.Add('MissingUpdates')
            $text = "$($missing.Count): " + (($missing | Select-Object -First 3) -join ', ')
            if ($missing.Count -gt 3) { $text += ', ...' }
            $text
        }

        $row['Update scan'] = Invoke-Check 'Update scan' $issues $unread {
            if (-not $state.ClientSdk) { return 'No client' }
            # Client settings can turn software updates off: such a client never scans, on purpose.
            $config = Get-CimInstance -CimSession $session -Namespace 'root\ccm\Policy\Machine\ActualConfig' -ClassName CCM_SoftwareUpdatesClientConfig -Property Enabled | Select-Object -First 1
            if ($config -and $config.Enabled -eq $false) { return 'Turned off' }
            try { $history = @(Get-CimInstance -CimSession $session -Namespace 'root\ccm\ScanAgent' -ClassName CCM_ScanUpdateSourceHistory -Property LastCompletionTime) }
            catch { if (Test-CimError $_ 'InvalidNamespace', 'InvalidClass') { return 'Turned off' }; throw }
            # The newest completion over every update source; CIM hands it over in local time (it's stored as real UTC).
            $last = $history | ForEach-Object LastCompletionTime | Where-Object { $_ -is [datetime] -and $_.Year -gt 2000 } | Sort-Object -Descending | Select-Object -First 1
            if (-not $last) { [void]$issues.Add('NotScanning'); return 'Never' }
            $age = (Get-Date) - $last
            if ($age.TotalDays -ge $Settings.NotScanningDays) { [void]$issues.Add('NotScanning') }
            Format-Age $age
        }
    }
    finally {
        Remove-CimSession -CimSession $session -ErrorAction SilentlyContinue
    }
    & $done
}

# Each device's issues as HealthReport.IssueText words them, the text the dashboard logs for it.
function Get-IssueText($Row) {
    $health = Get-HealthIssues
    $parts = foreach ($issue in $health.Order) {
        if (-not $Row.IssueSet.Contains($issue)) { continue }
        if ($issue -eq 'CouldNotCheck') { $health.Names[$issue] + ': ' + ($Row.Unread -join ', ') } else { $health.Names[$issue] }
    }
    @($parts) -join ', '
}

# A device's issues as a kept dashboard run holds them (Row.IssueKeys): Offline or Couldn't check, then each issue by tile name.
function Get-IssueKeys($Row) {
    $health = Get-HealthIssues
    $keys = New-Object System.Collections.Generic.List[string]
    if ($Row.State -eq 'Offline') { $keys.Add('Offline') }
    elseif ($Row.State -eq 'Failed') { $keys.Add($health.Names['CouldNotCheck']) }
    foreach ($issue in $health.Order) {
        $name = $health.Names[$issue]
        if ((Test-RowHas $Row $issue) -and -not $keys.Contains($name)) { $keys.Add($name) }
    }
    $keys
}

# Row.Has: the site's Not reporting, or one of the device's own issues.
function Test-RowHas($Row, [string]$Issue) {
    if ($Issue -eq 'NotReporting') { return [bool]$Row.NotReporting }
    $Row.IssueSet.Contains($Issue)
}

# The site and the collection: the console's last connection unless told otherwise. Read once, for the Devices and
# Tools sections. Throws when there's no site to read or no such collection.
$siteInfo = $null
function Get-Site {
    if ($script:siteInfo) { return $script:siteInfo }
    $server = $SiteServer
    $code = $SiteCode
    if (-not $server -or -not $code) {
        $last = Get-ChildItem 'HKCU:\Software\Microsoft\ConfigMgr10\AdminUI\MRU' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.PSChildName -replace '\D', '') } |
            ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } | Where-Object { $_.ServerName } | Select-Object -First 1
        if (-not $server -and $last) { $server = $last.ServerName }
        if (-not $code -and $last) { $code = $last.SiteCode }
    }
    if (-not $server -or -not $code) { throw 'no -SiteServer and -SiteCode, and the console has no last connection' }
    $namespace = "root\sms\site_$code"
    $quoted = $Collection.Replace('\', '\\').Replace("'", "\'")
    $record = @(Get-WmiObject -ComputerName $server -Namespace $namespace -Query "SELECT CollectionID, Name FROM SMS_Collection WHERE Name = '$quoted'")
    if ($record.Count -ne 1) { throw "collection $Collection not found on $server" }
    $script:siteInfo = [pscustomobject]@{ Server = $server; Code = $code; Namespace = $namespace; CollectionId = $record[0].CollectionID }
    $script:siteInfo
}

$expected = $null
function Test-Devices {
    $settings = Get-Layer8Settings
    $health = Get-HealthIssues
    $unknown = @($health.Order | Where-Object { $readableIssues -notcontains $_ })
    foreach ($issue in $unknown) { Add-Result WARN "The $($health.Names[$issue]) tile" "this script doesn't read it yet, so its count shows ?" }

    try { $siteRead = Get-Site }
    catch { Add-Result FAIL 'Site' $_.Exception.Message; return }
    $server = $siteRead.Server
    $code = $siteRead.Code
    # Get-WmiObject, not CIM: it hands the site's dates over as the provider's own text, which is UTC with no offset.
    $namespace = $siteRead.Namespace

    # Names in queries (the security pass, 1.1.2.24): text escaped the way Utility.WqlText and WqlEscapeString escape
    # it (the Library section checks those) is read by the SMS Provider as the name, quotes and backslashes included.
    $odd = "Layer8 regression O'Brien \ [x] _%"
    $escaped = $odd.Replace('\', '\\').Replace("'", "\'")
    $likeEscaped = -join @($odd.ToCharArray() | ForEach-Object {
            $c = [string]$_
            switch ($c) { '[' { '[[]' } '%' { '[%]' } '_' { '[_]' } '\' { '\\' } "'" { "\'" } default { $c } }
        })
    foreach ($query in @(
            @('WQL text: the SMS Provider reads an escaped quote and backslash', "SELECT PackageID FROM SMS_Package WHERE Name = '$escaped'"),
            @('WQL LIKE text: the SMS Provider reads escaped wildcards', "SELECT ContainerNodeID FROM SMS_ObjectContainerNode WHERE Name LIKE '$likeEscaped'"))) {
        try {
            $found = @(Get-WmiObject -ComputerName $server -Namespace $namespace -Query $query[1])
            if ($found.Count -eq 0) { Add-Result PASS $query[0] "no match for $odd, as expected" }
            else { Add-Result FAIL $query[0] "$($found.Count) matches for $odd" }
        }
        catch { Add-Result FAIL $query[0] "$($_.Exception.Message) ($($query[1]))" }
    }
    $collectionId = $siteRead.CollectionId
    $members = @(Get-WmiObject -ComputerName $server -Namespace $namespace -Query "SELECT Name, ResourceID, SMSID FROM SMS_FullCollectionMembership WHERE CollectionID = '$collectionId'" | Sort-Object Name)
    Add-Result INFO 'Collection' "$Collection ($collectionId): $($members.Count) members, from site $code on $server"

    # When the site last heard from each client: SMS_CombinedDeviceResources, 300 at a time, as HealthCheck.ReadSiteContact.
    $contact = @{}
    $nowUtc = [datetime]::UtcNow
    for ($start = 0; $start -lt $members.Count; $start += 300) {
        $ids = ($members[$start..([Math]::Min($start + 299, $members.Count - 1))] | ForEach-Object ResourceID) -join ','
        foreach ($device in Get-WmiObject -ComputerName $server -Namespace $namespace -Query "SELECT ResourceID, IsClient, LastPolicyRequest FROM SMS_CombinedDeviceResources WHERE ResourceID IN ($ids)") {
            $lastRequest = ConvertFrom-SiteTime $device.LastPolicyRequest
            $isClient = [bool]$device.IsClient
            $contact[[int]$device.ResourceID] = [pscustomobject]@{
                LastPolicyRequest = $lastRequest
                NotReporting      = $isClient -and ($null -eq $lastRequest -or ($nowUtc - $lastRequest).TotalDays -ge $settings.NotReportingDays)
            }
        }
    }

    $rows = foreach ($member in $members) {
        if ($member.Name -notmatch $computerName) {
            $row = [pscustomobject]@{ Device = $member.Name; State = 'Skipped'; Status = 'Skipped: not a computer name'; IssueSet = (New-Object 'System.Collections.Generic.HashSet[string]'); Unread = @(); NotReporting = $false; 'Policy request' = ''; Issues = ''; Raw = @{} }
        }
        else {
            Write-Host "  ...  reading $($member.Name)" -ForegroundColor DarkGray
            $row = Read-Device $member.Name $settings
        }
        # The client's GUID as the site has it, which Client Info reads from the client.
        $row.Raw.SMSID = $member.SMSID
        $site = $contact[[int]$member.ResourceID]
        if ($site) {
            $row.NotReporting = $site.NotReporting
            if ($site.LastPolicyRequest) { $row.'Policy request' = Format-Age ($nowUtc - $site.LastPolicyRequest) }
        }
        # The Issues column: Offline or Couldn't check first, then each issue in the tiles' order.
        $names = New-Object System.Collections.Generic.List[string]
        if ($row.State -eq 'Offline') { $names.Add('Offline') }
        elseif ($row.State -eq 'Failed') { $names.Add($health.Names['CouldNotCheck']) }
        foreach ($issue in $health.Order) {
            if (-not (Test-RowHas $row $issue)) { continue }
            if ($issue -eq 'CouldNotCheck') { $names.Add($health.Names[$issue] + ': ' + ($row.Unread -join ', ')) } else { $names.Add($health.Names[$issue]) }
        }
        $row.Issues = $names -join ', '
        if ($row.State -eq 'Checked' -and -not $row.Status) {
            $row.Status = 'Healthy'
            if ($names.Count -gt 0) { $row.Status = "$($names.Count) issue" + $(if ($names.Count -gt 1) { 's' }) }
        }
        $row
    }
    $rows = @($rows)

    foreach ($row in $rows) {
        $detail = $row.Status
        if ($row.Issues) { $detail += ": $($row.Issues)" }
        Add-Result INFO $row.Device $detail
    }
    $shown = $rows | Where-Object State -ne 'Skipped'
    if ($shown) {
        Write-Host ''
        Write-Host '  What the dashboard should show (Up and Policy request move on with the clock):' -ForegroundColor Gray
        $shown | Format-List Device, Online, 'Signed in', Up, 'C: free', 'Blue screen', Windows, Client, Restart, BitLocker, Defender, Updates, 'Update scan', 'Policy request', Issues |
            Out-String -Width 160 | ForEach-Object { $_.TrimEnd() } | Write-Host
    }

    # The tiles, as HealthDashboardDialog counts them.
    $tiles = [ordered]@{}
    $healthy = @($rows | Where-Object { $candidate = $_; $candidate.State -eq 'Checked' -and -not @($health.Order | Where-Object { Test-RowHas $candidate $_ }) }).Count
    if ($unknown) { $healthy = '?' }
    $tiles['healthy'] = $healthy
    $tiles['offline'] = @($rows | Where-Object State -eq 'Offline').Count
    foreach ($issue in $health.Order) {
        if ($issue -eq 'CouldNotCheck') { continue }
        $count = '?'
        if ($readableIssues -contains $issue) { $count = @($rows | Where-Object { Test-RowHas $_ $issue }).Count }
        $tiles[$health.Names[$issue].ToLowerInvariant()] = $count
    }
    $tiles[$health.Names['CouldNotCheck'].ToLowerInvariant()] = @($rows | Where-Object { $_.State -eq 'Failed' -or (Test-RowHas $_ 'CouldNotCheck') }).Count
    $tileText = ($tiles.Keys | ForEach-Object { "$_ $($tiles[$_])" }) -join ', '
    Add-Result INFO 'Tiles' "$($rows.Count) devices; $tileText"

    $script:expected = [pscustomobject]@{
        Server       = $server
        SiteCode     = $code
        CollectionId = $collectionId
        Rows         = $rows
        Tiles        = $tiles
        TileText     = $tileText
        Contacted    = $contact.Count
        NotReporting = @($rows | Where-Object NotReporting).Count
        Settings     = $settings
    }
}

# --- Tools ----------------------------------------------------------------------------------------------------------------

# The client tools this section runs, by the method name their menu XML gives as DialogId, in the order each device
# runs them. Quick Status last: it reads the uptime counter first, which took 43.1 s the first time on the site server,
# and last it holds up no other tool, even past the time limit.
$clientTools = [ordered]@{
    OnlineStatus        = 'Online Status'
    ClientInfo          = 'Client Info'
    CheckPendingRestart = 'Check Pending Restart'
    BitLockerStatus     = 'BitLocker Status'
    DefenderStatus      = 'Defender Status'
    MissingUpdates      = 'List Missing Updates'
    QuickStatus         = 'Quick Status'
}
# The dashboard logs a step over 10 seconds as slow, at any log level; a tool call over it gets the same flag here.
$slowSeconds = 10
# Calls still running after this are reported as unfinished, so one device that hangs can't hold up the run.
$toolTimeoutSeconds = 45
$toolCalls = $null
# The job is started before the first section and collected in this one. Its longest call is Quick Status on the
# site server: Quick Status reads the uptime counter first, and that read took 43.1 s on the first run of this
# section (STATUS.md's step timing row measured the same on the dashboard). One WMI call can't be split, so the run
# waits it out alongside the other sections instead of after them.
$toolJob = $null
function Start-ToolJob {
    if ($script:toolJob -or -not $consoleBin) { return }
    foreach ($dll in 'Layer8.dll', 'Layer8.Device.dll') { if (-not (Test-Path -LiteralPath (Join-Path $buildDir $dll))) { return } }
    $siteRead = Get-Site
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $job = Start-Job -RunAs32 -ScriptBlock $toolChecks -ArgumentList $buildDir, $consoleBin, $siteRead.Server, $siteRead.CollectionId, @($clientTools.Keys), $toolTimeoutSeconds, $resolverSource, $toolRunnerSource
    $script:toolJob = [pscustomobject]@{ Job = $job; Watch = $watch }
}

# DeviceProgressDialog.RunAction, step for step, without the window: a placeholder name is skipped, the port check
# (Layer8 Settings' offline check) comes first except for Online Status, then the tool, and a failure is unwrapped
# and worded by DeviceProgressDialog.DescribeFailure. Devices run at once on the thread pool, as the window runs
# them; each device's tools run one after another, as someone working down the Client Tools menu runs them.
$toolRunnerSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

public sealed class Layer8ToolCall
{
    public string Tool;
    public string Device;
    // Completed, Failed, Offline, Skipped, or Unfinished when it was still running at the time limit.
    public string State = "Unfinished";
    // What the progress window's Status column shows.
    public string Text;
    // The tool's own answer, for Quick Status's columns.
    public object Result;
    // A failure's exception, unwrapped as the window unwraps it.
    public string Error;
    public double Seconds;

    public Layer8ToolCall Copy() { return (Layer8ToolCall)MemberwiseClone(); }
}

public static class Layer8ToolRunner
{
    public static Layer8ToolCall[] Run(object[] records, string[] names, string[] tools, Delegate[] runs, bool checkReachable, int reachableMs,
        MethodInfo isComputerName, MethodInfo ensureReachable, MethodInfo waitForResult, MethodInfo describeFailure, MethodInfo unwrap, int timeoutSeconds)
    {
        Layer8ToolCall[] calls = new Layer8ToolCall[records.Length * tools.Length];
        for (int d = 0; d < records.Length; d++)
            for (int t = 0; t < tools.Length; t++)
                calls[d * tools.Length + t] = new Layer8ToolCall { Tool = tools[t], Device = names[d] };

        List<Task> devices = new List<Task>();
        for (int d = 0; d < records.Length; d++)
        {
            int device = d;
            devices.Add(Task.Run(() =>
            {
                for (int t = 0; t < tools.Length; t++)
                {
                    RunOne(calls[device * tools.Length + t], records[device], runs[t], checkReachable && tools[t] != "OnlineStatus", reachableMs,
                        isComputerName, ensureReachable, waitForResult, describeFailure, unwrap);
                }
            }));
        }
        Task.WaitAll(devices.ToArray(), TimeSpan.FromSeconds(timeoutSeconds));

        // Copies, so a call that finishes after the time limit doesn't change what's being handed back.
        Layer8ToolCall[] copies = new Layer8ToolCall[calls.Length];
        for (int i = 0; i < calls.Length; i++) lock (calls[i]) copies[i] = calls[i].Copy();
        return copies;
    }

    static void RunOne(Layer8ToolCall call, object record, Delegate run, bool checkReachable, int reachableMs,
        MethodInfo isComputerName, MethodInfo ensureReachable, MethodInfo waitForResult, MethodInfo describeFailure, MethodInfo unwrap)
    {
        string state, text, error = null;
        object result = null;
        Stopwatch watch = Stopwatch.StartNew();
        if (!(bool)isComputerName.Invoke(null, new object[] { call.Device }))
        {
            state = "Skipped";
            text = "Skipped: not a computer name";
        }
        else
        {
            try
            {
                if (checkReachable) ensureReachable.Invoke(null, new object[] { call.Device, reachableMs });
                result = run.DynamicInvoke(record);
                string outcome = result != null && result.GetType().Name == "DeviceColumns"
                    ? (string)result.GetType().GetProperty("Status").GetValue(result, null)
                    : (string)waitForResult.Invoke(null, new object[] { result });
                state = "Completed";
                text = string.IsNullOrEmpty(outcome) ? "Completed" : outcome;
            }
            catch (Exception ex)
            {
                Exception root = (Exception)unwrap.Invoke(null, new object[] { ex });
                error = root.GetType().FullName + ": " + root.Message;
                try
                {
                    object[] arguments = { call.Device, root, false };
                    text = (string)describeFailure.Invoke(null, arguments);
                    state = (bool)arguments[2] ? "Offline" : "Failed";
                }
                catch (Exception describing)
                {
                    state = "Failed";
                    text = "DescribeFailure threw " + describing.GetType().Name + " on: " + root.Message;
                }
            }
        }
        watch.Stop();
        lock (call)
        {
            call.State = state;
            call.Text = text;
            call.Result = result;
            call.Error = error;
            call.Seconds = watch.Elapsed.TotalSeconds;
        }
    }
}
'@

# Runs in a 32-bit PowerShell job, as the console runs: the tools' WMI calls get the providers the console's calls
# get. Returns one object: every call, the registry views, how long each part took, or what stopped it.
$toolChecks = {
    param([string]$BuildDir, [string]$ConsoleBin, [string]$Server, [string]$CollectionId, [string[]]$Tools, [int]$TimeoutSeconds, [string]$ResolverSource, [string]$RunnerSource)
    $ErrorActionPreference = 'Stop'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $answer = [pscustomobject]@{ Calls = @(); Registry = @(); Values = @(); Error = $null; ReadySeconds = 0; Seconds = 0; Reachable = '' }
    function Get-Message($Exception) {
        while ($Exception.InnerException -and ($Exception -is [Management.Automation.MethodInvocationException] -or $Exception -is [Reflection.TargetInvocationException])) { $Exception = $Exception.InnerException }
        $Exception.Message
    }
    try {
        if ([IntPtr]::Size -ne 4) { throw 'this has to run in 32-bit PowerShell' }
        Add-Type -TypeDefinition $ResolverSource
        [Layer8RegressionResolver]::Register([string[]]@($BuildDir, $ConsoleBin))
        Add-Type -TypeDefinition $RunnerSource
        $flags = [Reflection.BindingFlags]'Static, Public, NonPublic'
        $layer8 = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDir 'Layer8.dll'))
        $device = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDir 'Layer8.Device.dll'))
        function Get-Method($Type, [string]$Name) {
            $method = $Type.GetMethod($Name, $flags)
            if (-not $method) { throw "$($Type.FullName).$Name isn't there (renamed?)" }
            $method
        }
        $actions = $device.GetType('Layer8.Device.ClientActions', $true)
        $settings = $layer8.GetType('Layer8.Layer8Settings', $true)
        $checkReachable = [bool]$settings.GetProperty('CheckReachable').GetValue($null)
        $reachableMs = [int]$settings.GetProperty('ReachableSeconds').GetValue($null) * 1000
        $answer.Reachable = if ($checkReachable) { "port check first, $($reachableMs / 1000) s" } else { 'no port check (Layer8 Settings)' }

        # Each tool as the menus and the dashboard get it ready to run on a device.
        $prepare = Get-Method $actions 'PrepareClientTool'
        $runs = New-Object System.Collections.Generic.List[Delegate]
        foreach ($tool in $Tools) {
            [object[]]$arguments = @($tool, $null, $null, $null)
            if (-not $prepare.Invoke($null, $arguments)) { throw "PrepareClientTool didn't get $tool ready" }
            $runs.Add($arguments[1])
        }

        # The collection's members as a collection run gets them (ClientActions.ProcessAction): SMS_FullCollectionMembership
        # records, through the console's own connection to the SMS Provider.
        [void][Reflection.Assembly]::LoadFrom((Join-Path $ConsoleBin 'Microsoft.ConfigurationManagement.ManagementProvider.dll'))
        [void][Reflection.Assembly]::LoadFrom((Join-Path $ConsoleBin 'AdminUI.WqlQueryEngine.dll'))
        $connection = New-Object Microsoft.ConfigurationManagement.ManagementProvider.WqlQueryEngine.WqlConnectionManager (New-Object Microsoft.ConfigurationManagement.ManagementProvider.SmsNamedValuesDictionary)
        try {
            if (-not $connection.Connect($Server)) { throw "couldn't connect to the SMS Provider on $Server" }
            $records = New-Object System.Collections.Generic.List[object]
            $names = New-Object System.Collections.Generic.List[string]
            $members = $connection.QueryProcessor.ExecuteQuery("SELECT * FROM SMS_FullCollectionMembership WHERE CollectionID = '$CollectionId'")
            # IResultObject works with C#'s foreach but isn't an IEnumerable, so PowerShell walks its enumerator.
            $each = $members.GetEnumerator()
            while ($each.MoveNext()) {
                $records.Add($each.Current)
                $names.Add($each.Current['Name'].StringValue)
            }
            $answer.ReadySeconds = $watch.Elapsed.TotalSeconds

            $remoteTools = $device.GetType('Layer8.Device.RemoteTools', $true)
            $dialog = $device.GetType('Layer8.Device.DeviceProgressDialog', $true)
            $safeAction = $layer8.GetType('Layer8.SafeAction', $true)
            $calls = [Layer8ToolRunner]::Run($records.ToArray(), $names.ToArray(), $Tools, $runs.ToArray(), $checkReachable, $reachableMs,
                (Get-Method $remoteTools 'IsComputerName'), (Get-Method $actions 'EnsureReachable'), (Get-Method $actions 'WaitForResult'),
                (Get-Method $dialog 'DescribeFailure'), (Get-Method $safeAction 'Unwrap'), $TimeoutSeconds)
        }
        finally {
            if ($connection -is [IDisposable]) { $connection.Dispose() }
        }

        # Plain objects, so they come back from the job as they are: Quick Status's columns with the values they sort by.
        $answer.Calls = @(foreach ($call in $calls) {
            $cells = @()
            $summary = $null
            if ($null -ne $call.Result -and $call.Result.GetType().Name -eq 'DeviceColumns') {
                $summary = $call.Result.Summary
                $cells = @(foreach ($cell in $call.Result.Cells) {
                    $key = $cell.SortKey
                    if ($key -is [TimeSpan]) { $key = $key.TotalSeconds }
                    elseif ($null -ne $key -and -not ($key -is [double] -or $key -is [long])) { $key = "$key" }
                    [pscustomobject]@{ Column = $cell.Column; Text = $cell.Text; Key = $key }
                })
            }
            [pscustomobject]@{ Tool = $call.Tool; Device = $call.Device; State = $call.State; Text = $call.Text; Error = $call.Error; Seconds = $call.Seconds; Cells = $cells; Summary = $summary }
        })

        # Check Pending Restart's registry reads (ClientActions.WindowsRestartReasons): its own HasSubKey, on the registry
        # class made as it makes it, asked about every subkey Windows has under the two keys it looks in. Windows' list
        # comes from the 64-bit provider. A HasSubKey that reads another view (the 32-bit one, as the console is) misses
        # some, and would miss the restart flag too. Nothing on the lab's devices is waiting for a restart, so the flags
        # themselves can't be the test.
        $hasSubKey = Get-Method $actions 'HasSubKey'
        $utility = $layer8.GetType('Layer8.Utility', $true)
        $getScope = $utility.GetMethod('GetWMIScope')
        $context64 = $utility.GetMethod('Provider64Context').Invoke($null, $null)
        $restartKeys = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing', 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update'
        function Get-SubKeys($Registry, [string]$Key, $Context) {
            $in = $Registry.GetMethodParameters('EnumKey')
            $in['hDefKey'] = [uint32]2147483650
            $in['sSubKeyName'] = $Key
            $options = $null
            if ($Context) { $options = New-Object Management.InvokeMethodOptions; $options.Context = $Context }
            $out = $Registry.InvokeMethod('EnumKey', $in, $options)
            [pscustomobject]@{ Found = [uint32]$out['ReturnValue'] -eq 0; Names = @(@($out['sNames']) | Where-Object { $_ } | Sort-Object) -join '|' }
        }
        $reached = @($answer.Calls | Where-Object { $_.Tool -eq 'OnlineStatus' -and $_.State -eq 'Completed' } | ForEach-Object Device)
        $answer.Registry = @(foreach ($name in $reached) {
            try {
                # [string]: the name comes out of a PowerShell object, and reflection won't take the wrapper for a string.
                $scope = $getScope.Invoke($null, @([string]$name, 'default', $null, $null))
                $registry = New-Object Management.ManagementClass($scope, (New-Object Management.ManagementPath('StdRegProv')), $null)
                foreach ($key in $restartKeys) {
                    $windows = Get-SubKeys $registry $key $context64
                    $subKeys = @($windows.Names -split '\|' | Where-Object { $_ })
                    # BaseObject: New-Object wraps what it makes for PowerShell, and reflection wants the object itself.
                    $missed = @(foreach ($subKey in $subKeys) { if (-not $hasSubKey.Invoke($null, @($registry.PSObject.BaseObject, [string]$key, [string]$subKey))) { $subKey } })
                    [pscustomobject]@{ Device = $name; Key = $key; WindowsFound = $windows.Found; SubKeys = $subKeys.Count; Missed = @($missed); Error = $null }
                }
            }
            catch { [pscustomobject]@{ Device = $name; Key = $null; WindowsFound = $false; SubKeys = 0; Missed = @(); Error = (Get-Message $_.Exception) } }
        })

        # Registry values as Layer8 reads them, against what the 64-bit provider gives for the same value:
        #   Provisioning mode: ClientActions.ProvisioningModeValue, behind the dashboard's Client column and Turn Off
        #     Provisioning Mode. Every lab machine has it, "false"; a read from another view comes back with none.
        #   Registry values: SystemInfoReader.RegistryValue, behind Windows Security's Defender for Endpoint line and
        #     the Windows version on System Information and the dashboard. Defender for Endpoint's key isn't in the
        #     32-bit view at all, but its value is missing in both on the lab's devices (not onboarded), so it can't
        #     tell them apart; ProgramFilesDir can: "C:\Program Files" in the 64-bit view, "C:\Program Files (x86)"
        #     in the 32-bit one.
        function Get-StringValue64($Scope, [string]$Key, [string]$Name) {
            $registry = New-Object Management.ManagementClass($Scope, (New-Object Management.ManagementPath('StdRegProv')), $null)
            $in = $registry.GetMethodParameters('GetStringValue')
            $in['hDefKey'] = [uint32]2147483650
            $in['sSubKeyName'] = $Key
            $in['sValueName'] = $Name
            $options = New-Object Management.InvokeMethodOptions
            $options.Context = $context64
            $registry.InvokeMethod('GetStringValue', $in, $options)['sValue']
        }
        $reader = $device.GetType('Layer8.Device.SystemInfoReader', $true)
        $valueReads = @(
            [pscustomobject]@{
                Check = 'Provisioning mode as Layer8 reads it'; Method = 'ClientActions.ProvisioningModeValue'
                Info = $actions.GetMethod('ProvisioningModeValue', $flags); Key = 'SOFTWARE\Microsoft\CCM\CcmExec'; Name = 'ProvisioningMode'
                Arguments = { param($scope) , @($scope) }
                Means = "the dashboard's Client column and Turn Off Provisioning Mode can't see a client in provisioning mode"
            }
            [pscustomobject]@{
                Check = 'Registry values as Layer8 reads them'; Method = 'SystemInfoReader.RegistryValue'
                Info = $reader.GetMethod('RegistryValue', $flags, $null, [Type[]]@([Management.ManagementScope], [string], [string], [string]), $null)
                Key = 'SOFTWARE\Microsoft\Windows\CurrentVersion'; Name = 'ProgramFilesDir'
                Arguments = { param($scope) , @($scope, 'GetStringValue', 'SOFTWARE\Microsoft\Windows\CurrentVersion', 'ProgramFilesDir') }
                Means = "Windows Security says Not onboarded on a device onboarded to Defender for Endpoint, whose key isn't in that view"
            }
        )
        $answer.Values = @(foreach ($read in $valueReads) {
            if (-not $read.Info) { [pscustomobject]@{ Check = $read.Check; Method = $read.Method; Means = $read.Means; Device = $null; Layer8 = $null; Windows = $null; Error = 'missing' }; continue }
            foreach ($name in $reached) {
                try {
                    # BaseObject, as for HasSubKey: reflection wants the scope itself, not PowerShell's wrapper round it.
                    $scope = ($getScope.Invoke($null, @([string]$name, 'default', $null, $null))).PSObject.BaseObject
                    $layer8Value = $read.Info.Invoke($null, [object[]](& $read.Arguments $scope))
                    [pscustomobject]@{ Check = $read.Check; Method = $read.Method; Means = $read.Means; Device = $name; Layer8 = $layer8Value; Windows = (Get-StringValue64 $scope $read.Key $read.Name); Error = $null }
                }
                catch { [pscustomobject]@{ Check = $read.Check; Method = $read.Method; Means = $read.Means; Device = $name; Layer8 = $null; Windows = $null; Error = (Get-Message $_.Exception) } }
            }
        })
    }
    catch {
        $answer.Error = "script error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, (Get-Message $_.Exception)
    }
    $answer.Seconds = $watch.Elapsed.TotalSeconds
    $answer
}

# List Missing Updates' answer, worked out again from the client's own classes as ClientActions.MissingUpdates works
# it out: what's deployed and missing (CCM_SoftwareUpdate), and what the last scan says it needs that no deployment
# covers (CCM_UpdateStatus), each by KB, five shown and the rest counted.
function Get-MissingUpdatesText([string]$Name) {
    $session = New-CimSession -ComputerName $Name -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 60
    try {
        $keyOf = { param($article, $title) if ($article) { "KB$article" } elseif ($title) { $title } else { '(unnamed update)' } }
        $guidOf = {
            param($id)
            $found = [regex]::Matches([string]$id, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
            if ($found.Count) { $found[$found.Count - 1].Value.ToLowerInvariant() }
        }
        $deployed = New-Object System.Collections.Generic.List[string]
        $deployedIds = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($update in Get-CimInstance -CimSession $session -Namespace 'root\ccm\ClientSDK' -Query 'SELECT * FROM CCM_SoftwareUpdate WHERE ComplianceState = 0') {
            $updateKey = & $keyOf $update.ArticleID $update.Name
            if (-not $deployed.Contains($updateKey)) { $deployed.Add($updateKey) }
            if ($update.CimInstanceProperties['UpdateID']) { $id = & $guidOf $update.UpdateID; if ($id) { [void]$deployedIds.Add($id) } }
        }
        $needed = New-Object System.Collections.Generic.List[string]
        $note = $null
        try {
            $scans = @(Get-CimInstance -CimSession $session -Namespace 'root\ccm\ScanAgent' -Query 'SELECT LastCompletionTime FROM CCM_ScanUpdateSourceHistory' |
                Where-Object { $_.LastCompletionTime -is [datetime] -and $_.LastCompletionTime.Year -gt 2000 })
            if (-not $scans) { $note = 'No update scan yet' }
            else {
                foreach ($update in Get-CimInstance -CimSession $session -Namespace 'root\ccm\SoftwareUpdates\UpdatesStore' -Query "SELECT Article, Title, UniqueId FROM CCM_UpdateStatus WHERE Status = 'Missing'") {
                    $updateKey = & $keyOf $update.Article $update.Title
                    $id = & $guidOf $update.UniqueId
                    if ($deployed.Contains($updateKey) -or ($id -and $deployedIds.Contains($id)) -or $needed.Contains($updateKey)) { continue }
                    $needed.Add($updateKey)
                }
            }
        }
        catch {
            if (Test-CimError $_ 'InvalidNamespace', 'InvalidClass') { $note = 'No update scan results' } else { throw }
        }
        if ($deployed.Count -eq 0 -and $needed.Count -eq 0 -and -not $note) { return 'No missing updates' }
        $summarize = { param($items) if ($items.Count -le 5) { $items -join ', ' } else { (@($items)[0..4] -join ', ') + " (+$($items.Count - 5) more)" } }
        $deployedPart = 'Deployed: none missing'
        if ($deployed.Count) { $deployedPart = 'Deployed, missing: ' + (& $summarize $deployed) }
        $neededPart = $note
        if (-not $neededPart) { $neededPart = 'Nothing else needed'; if ($needed.Count) { $neededPart = 'Needed, not deployed: ' + (& $summarize $needed) } }
        "$deployedPart. $neededPart"
    }
    finally {
        Remove-CimSession -CimSession $session -ErrorAction SilentlyContinue
    }
}

# A tool's answer against the text it should be. $Normalize takes out what changes from one read to the next.
function Test-Answer([string]$Check, [string]$Expected, $Call, [scriptblock]$Normalize) {
    $got = [string]$Call.Text
    $want = $Expected
    $have = $got
    if ($Normalize) { $want = & $Normalize $want; $have = & $Normalize $have }
    if ($want -ceq $have) { Add-Result PASS $Check $got } else { Add-Result FAIL $Check "expected '$Expected', got '$got'" }
}

# One tool on one device the Devices section read, against what that reading says the tool should answer.
function Test-ToolAnswer([string]$Tool, $Row, $Call, [string]$Check, $Reads) {
    $raw = $Row.Raw
    # The progress window's words for a WMI namespace or class that isn't there (DeviceProgressDialog.DescribeFailure).
    $noClient = 'No ConfigMgr client found'
    switch ($Tool) {
        'OnlineStatus' {
            # Two pings don't take the same time.
            Test-Answer $Check $Row.Online $Call { param($text) $text -replace 'ping \d+ ms', 'ping # ms' }
        }
        'QuickStatus' {
            if ($Call.State -ne 'Completed') { Add-Result FAIL $Check "expected 'Online' and its columns, got '$($Call.Text)'"; return }
            $cell = @{}
            foreach ($entry in $Call.Cells) { $cell[$entry.Column] = $entry }
            $wrong = New-Object System.Collections.Generic.List[string]
            if ($Call.Text -cne 'Online') { $wrong.Add("Status '$($Call.Text)', not 'Online'") }
            foreach ($column in 'Signed in', 'Up', 'C: free', 'C: size', 'IP address', 'Client') { if (-not $cell.ContainsKey($column)) { $wrong.Add("no $column column") } }
            if ($wrong.Count -eq 0) {
                # The same accounts. Quick Status says Nobody where the dashboard says No one.
                $want = @($raw.SignedIn | Sort-Object) -join ', '
                $got = @($cell['Signed in'].Text -split ', ' | Where-Object { $_ -and $_ -cne 'Nobody' } | Sort-Object) -join ', '
                if ($want -ne $got) { $wrong.Add("Signed in '$($cell['Signed in'].Text)', the script reads '$($Row.'Signed in')'") }
                # Quick Status reads the uptime counter first, the dashboard the boot time. On the second client the two are
                # about 10 minutes apart, because its clock was set after it booted (STATUS.md, the step timing row); 30
                # minutes allows for that and catches a wrong unit or a wrong device.
                if ($raw.Uptime -is [TimeSpan] -and $cell['Up'].Key -is [double]) {
                    $minutes = [Math]::Abs($cell['Up'].Key - $raw.Uptime.TotalSeconds) / 60
                    if ($minutes -gt 30) { $wrong.Add(("Up '{0}', the script reads '{1}' ({2:0} minutes apart)" -f $cell['Up'].Text, $Row.Up, $minutes)) }
                }
                else { $wrong.Add("Up '$($cell['Up'].Text)', the script reads '$($Row.Up)'") }
                # Free space moves while it's read, so within 1 GB; the drive's size doesn't move.
                if ($null -ne $raw.FreeBytes -and $cell['C: free'].Key -is [double]) {
                    if ([Math]::Abs($cell['C: free'].Key - $raw.FreeBytes) -ge 1GB) { $wrong.Add("C: free '$($cell['C: free'].Text)', the script reads '$(Format-Bytes $raw.FreeBytes '0.#')'") }
                }
                else { $wrong.Add("C: free '$($cell['C: free'].Text)', the script reads '$($Row.'C: free')'") }
                if ($null -eq $raw.SizeBytes -or $cell['C: size'].Text -cne (Format-Bytes $raw.SizeBytes '0')) { $wrong.Add("C: size '$($cell['C: size'].Text)', the script reads '$($Row.'C: free')'") }
                # The device's own addresses, the one DNS gives among them.
                $dnsAddress = [regex]::Match([string]$Row.Online, '\(([^)]+)\)$').Groups[1].Value
                if ($dnsAddress -and @($cell['IP address'].Text -split ', ') -notcontains $dnsAddress) { $wrong.Add("IP address '$($cell['IP address'].Text)' doesn't include $dnsAddress, its address in DNS") }
                if ($raw.ClientSdk -and $Row.Client -match '^\d' -and $cell['Client'].Text -cne $Row.Client) { $wrong.Add("Client '$($cell['Client'].Text)', the script reads '$($Row.Client)'") }
                if (-not $raw.ClientSdk -and $Row.Client -eq 'Not installed' -and $cell['Client'].Text -cne 'None') { $wrong.Add("Client '$($cell['Client'].Text)', expected None (the script: $($Row.Client))") }
            }
            if ($wrong.Count) { Add-Result FAIL $Check ($wrong -join '; ') } else { Add-Result PASS $Check $Call.Summary }
        }
        'ClientInfo' {
            if (-not $raw.ClientSdk) { Test-Answer $Check $noClient $Call; return }
            $parts = [regex]::Match([string]$Call.Text, '^site (\S+) via (\S+); client (\S+); (\S+)$')
            if (-not $parts.Success) { Add-Result FAIL $Check "expected 'site <code> via <management point>; client <version>; <GUID>', got '$($Call.Text)'"; return }
            $wrong = New-Object System.Collections.Generic.List[string]
            if ($parts.Groups[1].Value -ne $Reads.SiteCode) { $wrong.Add("site $($parts.Groups[1].Value), not $($Reads.SiteCode)") }
            if ($Reads.ManagementPoints.Count -and $Reads.ManagementPoints -notcontains $parts.Groups[2].Value) { $wrong.Add("$($parts.Groups[2].Value) isn't one of the site's management points ($($Reads.ManagementPoints -join ', '))") }
            if ($Row.Client -match '^\d' -and $parts.Groups[3].Value -cne $Row.Client) { $wrong.Add("client $($parts.Groups[3].Value), the script reads $($Row.Client)") }
            if ($parts.Groups[4].Value -ne $raw.SMSID) { $wrong.Add("GUID $($parts.Groups[4].Value), the site has $($raw.SMSID)") }
            if ($wrong.Count) { Add-Result FAIL $Check (($wrong -join '; ') + " (it said '$($Call.Text)')") } else { Add-Result PASS $Check $Call.Text }
        }
        'CheckPendingRestart' {
            if (-not $raw.ClientSdk) { Test-Answer $Check $noClient $Call; return }
            $want = 'No restart pending'
            if ($Row.Restart -ne 'No') { $want = "Restart pending: $($Row.Restart)" }
            Test-Answer $Check $want $Call
        }
        'BitLockerStatus' {
            $state = $raw.BitLocker
            if ($null -eq $state) { Add-Result INFO $Check "answered '$($Call.Text)'; this script couldn't read BitLocker ($($Row.BitLocker))"; return }
            if ($state -eq 'NoNamespace') {
                # No BitLocker WMI namespace (a server without the BitLocker feature): the dashboard says Not available.
                Test-Answer $Check 'Not available (no BitLocker WMI namespace)' $Call
                return
            }
            if ($state -eq 'NoVolume') { Test-Answer $Check "C: isn't an encryptable volume" $Call; return }
            $protection = switch ([int]$state.Protection) { 1 { 'protected' } 0 { 'not protected' } default { 'protection unknown' } }
            $want = "C: $protection"
            if ($state.Conversion) { $want += ", $($state.Conversion)" }
            Test-Answer $Check $want $Call
        }
        'DefenderStatus' {
            $status = $raw.Defender
            if ($null -eq $status) { Add-Result INFO $Check "answered '$($Call.Text)'; this script couldn't read Defender ($($Row.Defender))"; return }
            if ($status -eq 'NoNamespace') { Test-Answer $Check 'No Microsoft Defender on this device' $Call; return }
            if ($status -eq 'NoStatus') { Test-Answer $Check 'Defender reports no status (another antivirus may be in charge)' $Call; return }
            $parts = @('AV OFF', 'real-time OFF')
            if ($status.AntivirusEnabled -eq $true) { $parts[0] = 'AV on' }
            if ($status.RealTimeProtectionEnabled -eq $true) { $parts[1] = 'real-time on' }
            $updated = ''
            if ($status.AntivirusSignatureLastUpdated -is [datetime] -and $status.AntivirusSignatureLastUpdated.Year -gt 2000) { $updated = ' (' + $status.AntivirusSignatureLastUpdated.ToString('g') + ')' }
            $parts += "signatures $($status.AntivirusSignatureAge) day(s) old$updated"
            if ($null -eq $status.QuickScanAge -or [int64]$status.QuickScanAge -ge [uint32]::MaxValue) { $parts += 'no quick scan yet' } else { $parts += "last quick scan $($status.QuickScanAge) day(s) ago" }
            Test-Answer $Check ($parts -join ', ') $Call
        }
        'MissingUpdates' {
            if (-not $raw.ClientSdk) { Test-Answer $Check $noClient $Call; return }
            $read = $Reads.Updates[$Row.Device]
            if (-not $read -or $read.Error) { Add-Result INFO $Check "answered '$($Call.Text)'; this script couldn't read the client's updates: $($read.Error)"; return }
            Test-Answer $Check $read.Text $Call
        }
        default { Add-Result WARN $Check "this script doesn't know what $Tool should answer: '$($Call.Text)'" }
    }
}

function Test-Tools {
    if (-not $consoleBin) { Add-Result SKIP 'Tools' 'no ConfigMgr console on this computer (SMS_ADMIN_UI_PATH)'; return }
    foreach ($dll in 'Layer8.dll', 'Layer8.Device.dll') { if (-not (Test-Path -LiteralPath (Join-Path $buildDir $dll))) { Add-Result FAIL 'Tools' "$dll isn't in Build\Release: run tools\Build-Layer8Msi.ps1"; return } }
    if (-not $script:expected) { Add-Result SKIP 'Tools' 'the Devices section didn''t run, and it says what each tool should answer'; return }
    $tools = @($clientTools.Keys)
    Start-ToolJob
    $running = $script:toolJob
    if (-not $running) { Add-Result FAIL 'The tool run' 'the 32-bit job didn''t start'; return }

    # The reads the Devices section doesn't make: the site's management points, for Client Info, and each client's
    # missing updates, for List Missing Updates.
    $reads = [pscustomobject]@{ SiteCode = $script:expected.SiteCode; ManagementPoints = @(); Updates = @{} }
    try {
        # The provider refuses NetworkOSPath in a select list ("Generic failure"); ServerRemoteName is the server's FQDN.
        $reads.ManagementPoints = @(Get-WmiObject -ComputerName $script:expected.Server -Namespace "root\sms\site_$($script:expected.SiteCode)" -Query "SELECT ServerRemoteName FROM SMS_SystemResourceList WHERE RoleName = 'SMS Management Point'" |
            ForEach-Object ServerRemoteName)
    }
    catch { Add-Result WARN 'Management points' "couldn't read them from the site, so Client Info's isn't checked: $($_.Exception.Message)" }
    foreach ($row in $script:expected.Rows | Where-Object { $_.State -eq 'Checked' -and $_.Raw.ClientSdk }) {
        try { $reads.Updates[$row.Device] = [pscustomobject]@{ Text = (Get-MissingUpdatesText $row.Device); Error = $null } }
        catch { $reads.Updates[$row.Device] = [pscustomobject]@{ Text = $null; Error = $_.Exception.Message.Trim() } }
    }

    $waited = [Diagnostics.Stopwatch]::StartNew()
    $answer = @(Receive-Job -Job $running.Job -Wait -AutoRemoveJob) | Where-Object { $_.PSObject.Properties['Calls'] } | Select-Object -Last 1
    $script:toolJob = $null
    if (-not $answer) { Add-Result FAIL 'The tool run' 'the 32-bit job gave nothing back'; return }
    if ($answer.Error) { Add-Result FAIL 'The tool run stopped' $answer.Error; return }
    $calls = @($answer.Calls)
    $script:toolCalls = $calls
    $devices = @($calls | Group-Object Device)
    Add-Result INFO 'Run' ("{0} tools on {1} records in 32-bit PowerShell, {2}: {3:0.0} s, {4:0.0} s of it starting PowerShell and loading Layer8 and the collection; started with the run, so this section waited {5:0.0} s for it" -f
        $tools.Count, $devices.Count, $answer.Reachable, $answer.Seconds, $answer.ReadySeconds, $waited.Elapsed.TotalSeconds)

    $byKey = @{}
    foreach ($call in $calls) { $byKey["$($call.Device)|$($call.Tool)"] = $call }

    # Placeholder records: every tool skips them by name, as the progress window does.
    $placeholders = @($script:expected.Rows | Where-Object State -eq 'Skipped')
    if ($placeholders) {
        $ran = @(foreach ($row in $placeholders) {
            foreach ($tool in $tools) {
                $call = $byKey["$($row.Device)|$tool"]
                if (-not $call) { "$($clientTools[$tool]) never saw $($row.Device)" }
                elseif ($call.State -ne 'Skipped') { "$($clientTools[$tool]) on $($row.Device): '$($call.Text)'" }
            }
        })
        if ($ran) { Add-Result FAIL 'Placeholder records skipped by name' (($ran | Select-Object -First 3) -join '; ') }
        else { Add-Result PASS 'Placeholder records skipped by name' "$($placeholders.Count) records, by all $($tools.Count) tools" }
    }

    foreach ($row in $script:expected.Rows | Where-Object State -ne 'Skipped') {
        foreach ($tool in $tools) {
            $check = "$($clientTools[$tool]) on $($row.Device)"
            $call = $byKey["$($row.Device)|$tool"]
            if (-not $call) { Add-Result FAIL $check 'the collection record the console gets has no such device'; continue }
            if ($call.State -eq 'Unfinished') { Add-Result FAIL $check "still running after $toolTimeoutSeconds s"; continue }
            if ($call.State -eq 'Skipped') { Add-Result FAIL $check 'skipped as not a computer name'; continue }
            if ($row.State -eq 'Offline') {
                if ($call.State -eq 'Offline') { Add-Result PASS $check $call.Text } else { Add-Result FAIL $check "expected it offline, got '$($call.Text)'" }
                continue
            }
            if ($row.State -eq 'Failed') { Add-Result INFO $check "answered '$($call.Text)'; this script couldn't connect: $($row.Status)"; continue }
            Test-ToolAnswer $tool $row $call $check $reads
        }
    }

    # The keys Check Pending Restart looks under for Windows' own restart flags: does its HasSubKey see what Windows has?
    foreach ($group in $answer.Registry | Group-Object Device) {
        $check = "Check Pending Restart's registry view on $($group.Name)"
        $failed = @($group.Group | Where-Object Error)
        if ($failed) { Add-Result WARN $check "couldn't read it: $($failed[0].Error)"; continue }
        $missing = @($group.Group | Where-Object { -not $_.WindowsFound } | ForEach-Object { Split-Path $_.Key -Leaf })
        $different = @(foreach ($view in $group.Group | Where-Object { @($_.Missed).Count }) {
            "{0}: {1} of the {2} subkeys Windows has, such as {3}" -f (Split-Path $view.Key -Leaf), @($view.Missed).Count, $view.SubKeys, @($view.Missed)[0]
        })
        if ($different) { Add-Result FAIL $check ("ClientActions.HasSubKey doesn't see {0}. It's reading another registry view than Windows' own (the 32-bit one, from the 32-bit console), so a restart that Windows servicing or Windows Update is waiting for doesn't show, there or on the dashboard" -f ($different -join '; ')) }
        else {
            $seen = (@($group.Group | Where-Object WindowsFound | ForEach-Object { '{0} ({1})' -f (Split-Path $_.Key -Leaf), $_.SubKeys }) -join ' and ')
            $detail = "HasSubKey sees every subkey Windows has under $seen"
            if ($missing) { $detail += "; Windows has no $($missing -join ' or ') key" }
            Add-Result PASS $check $detail
        }
    }

    # Registry values: does Layer8 read the value Windows has?
    foreach ($read in $answer.Values) {
        if ($read.Error -eq 'missing') { Add-Result FAIL $read.Check "$($read.Method) isn't there (renamed?)"; continue }
        $check = "$($read.Check) on $($read.Device)"
        if ($read.Error) { Add-Result WARN $check "couldn't read it: $($read.Error)"; continue }
        $layer8Text = 'no value'
        if ($null -ne $read.Layer8) { $layer8Text = "'$($read.Layer8)'" }
        $windowsText = 'none'
        if ($null -ne $read.Windows) { $windowsText = "'$($read.Windows)'" }
        if ("$($read.Layer8)" -ceq "$($read.Windows)" -and ($null -eq $read.Layer8) -eq ($null -eq $read.Windows)) { Add-Result PASS $check "$layer8Text, as Windows has it" }
        else { Add-Result FAIL $check "$($read.Method) reads $layer8Text where Windows has $windowsText. It's reading another registry view (the 32-bit one, from the 32-bit console), so $($read.Means)" }
    }

    # Time per device, and any call the dashboard would call slow.
    foreach ($call in $calls | Where-Object { $_.Seconds -ge $slowSeconds }) { Add-Result WARN "Slow: $($clientTools[$call.Tool]) on $($call.Device)" ("{0:0.0} s" -f $call.Seconds) }
    foreach ($group in $devices | Where-Object { @($_.Group | Where-Object State -ne 'Skipped').Count }) {
        $slowest = $group.Group | Sort-Object Seconds -Descending | Select-Object -First 1
        Add-Result INFO "Time on $($group.Name)" ("{0:0.0} s for the {1} tools; the longest {2}, {3:0.0} s" -f ($group.Group | Measure-Object Seconds -Sum).Sum, $group.Count, $clientTools[$slowest.Tool], $slowest.Seconds)
    }
}

# --- Log ------------------------------------------------------------------------------------------------------------------

function Test-Log {
    $entries = @(Get-LogEntries | Where-Object { $_.Time -ge $Since })
    Add-Result INFO 'Entries' ("{0} since {1:g}, in {2}" -f $entries.Count, $Since, $logFolder)
    if (-not $entries) { Add-Result FAIL 'Log' "nothing logged since $($Since.ToString('g')): was the console used?"; return }

    # An entry from before the console loaded this build belongs to the build before: worth seeing, not this one's failure.
    $loadedLine = $entries | Where-Object { $_.Text -match ('^Layer8 ' + [regex]::Escape("$version") + ' loaded in console 5\.') } | Select-Object -First 1
    $loadedAt = $null
    $earlier = ''
    if ($loadedLine) { $loadedAt = $loadedLine.Time; $earlier = " (before Layer8 $version loaded at $($loadedAt.ToString('T')))" }
    elseif (Get-LogEntries | Where-Object { $_.Text -match ('^Layer8 ' + [regex]::Escape("$version") + ' loaded in console 5\.') }) { $loadedAt = $Since }

    $errors = @($entries | Where-Object Type -eq 3)
    foreach ($entry in $errors | Select-Object -First 15) {
        $result = 'FAIL'
        $note = ''
        if ($null -eq $loadedAt -or $entry.Time -lt $loadedAt) { $result = 'WARN'; $note = $earlier }
        if ($null -eq $loadedAt) { $note = " (the console hasn't loaded Layer8 $version)" }
        Add-Result $result ("Error at {0:T}{1}" -f $entry.Time, $note) "$($entry.Component): $(Get-FirstLine $entry.Text)"
    }
    if (-not $errors) { Add-Result PASS 'No errors logged' '' }
    if ($errors.Count -gt 15) { Add-Result INFO 'More errors' "$($errors.Count - 15) more in the log" }

    $warnings = @($entries | Where-Object Type -eq 2)
    foreach ($entry in $warnings | Select-Object -First 15) {
        $note = ''
        if ($loadedAt -and $entry.Time -lt $loadedAt) { $note = $earlier }
        Add-Result WARN ("Warning at {0:T}{1}" -f $entry.Time, $note) "$($entry.Component): $(Get-FirstLine $entry.Text)"
    }
    if ($warnings.Count -gt 15) { Add-Result INFO 'More warnings' "$($warnings.Count - 15) more in the log" }

    # A step over 10 seconds is logged at Info with its time, whatever the log level.
    $slow = @($entries | Where-Object { $_.Text -match ' took [0-9.]+ seconds$' })
    if ($slow) { foreach ($entry in $slow) { Add-Result WARN ("Slow step at {0:T}" -f $entry.Time) $entry.Text } }
    else { Add-Result PASS 'No step took 10 seconds or more' '' }

    foreach ($entry in $entries | Where-Object { $_.Text -like '*settings saved*' }) { Add-Result INFO ("Settings saved at {0:T}" -f $entry.Time) $entry.Text }

    # The last Health Dashboard run on the collection: every build runs one, and the Devices section says what it should find.
    $scope = [regex]::Escape("Health Dashboard on $Collection")
    # The last full run: Check Again on a few devices logs a run on the same collection too.
    $startIndex = -1
    $fullCount = $null
    if ($script:expected) { $fullCount = $script:expected.Rows.Count }
    for ($i = $entries.Count - 1; $i -ge 0; $i--) {
        if ($entries[$i].Text -match "^$scope`: checking (\d+) device\(s\)" -and ($null -eq $fullCount -or [int]$Matches[1] -eq $fullCount)) { $startIndex = $i; break }
    }
    if ($startIndex -lt 0) { Add-Result FAIL "A Health Dashboard run on $Collection" "none since $($Since.ToString('g')): every build runs one"; return }
    $null = $entries[$startIndex].Text -match "^$scope`: checking (\d+) device\(s\)"
    $checked = [int]$Matches[1]
    $finishIndex = -1
    for ($i = $startIndex + 1; $i -lt $entries.Count; $i++) { if ($entries[$i].Text -match "^$scope finished: ") { $finishIndex = $i; break } }
    if ($finishIndex -lt 0) { Add-Result FAIL "The Health Dashboard run at $($entries[$startIndex].Time.ToString('T'))" "it didn't finish (closed or stopped before the end?)"; return }
    $finished = $entries[$finishIndex]
    $null = $finished.Text -match "^$scope finished: (\d+) device\(s\) in ([^;]+); (.*)$"
    Add-Result INFO 'Last dashboard run' ("{0:T}: {1} devices in {2}" -f $entries[$startIndex].Time, $Matches[1], $Matches[2])
    $loggedTiles = [ordered]@{}
    foreach ($pair in $Matches[3] -split ', ') { if ($pair -match '^(.*) (\d+)$') { $loggedTiles[$Matches[1]] = $Matches[2] } }

    if (-not $script:expected) { Add-Result SKIP 'Against the Devices section' 'the Devices section didn''t run'; return }

    # The Devices section reads with the current settings. A run made with others (a threshold being tested, not yet
    # set back) can't match it, so say so before the mismatches. HealthDashboardOptionsControl logs every save this way.
    $current = $script:expected.Settings
    $currentText = 'Health Dashboard settings saved: low disk under {0} GB; long uptime {1} days or more; not reporting after {2} days; blue screens in the last {3} days; not scanning after {4} days; Windows older than build {5}' -f
        $current.LowDiskGB, $current.LongUptimeDays, $current.NotReportingDays, $current.BlueScreenDays, $current.NotScanningDays, $current.WindowsBuildText
    $usedLine = Get-LogEntries | Where-Object { $_.Text -like 'Health Dashboard settings saved:*' -and $_.Time -le $entries[$startIndex].Time } | Select-Object -Last 1
    if ($usedLine -and $usedLine.Text -ne $currentText) {
        Add-Result WARN 'Settings the run used' ("other than the current ones, saved {0:T}: {1}. Run the dashboard again at the current settings" -f $usedLine.Time, $usedLine.Text.Substring('Health Dashboard settings saved: '.Length))
    }
    else { Add-Result PASS 'Settings the run used' 'the current ones' }

    Test-Equal 'Devices checked' $script:expected.Rows.Count $checked

    foreach ($name in $loggedTiles.Keys) {
        # Changed depends on the run before this one, which the dashboard has just replaced: checked further down.
        if ($name -eq 'changed') { continue }
        if (-not $script:expected.Tiles.Contains($name)) { Add-Result WARN "Tile '$name'" "logged $($loggedTiles[$name]); this script doesn't know the tile"; continue }
        $want = $script:expected.Tiles[$name]
        if ("$want" -eq '?') { Add-Result SKIP "Tile '$name'" "logged $($loggedTiles[$name]); this script doesn't read it yet" }
        else { Test-Equal "Tile '$name'" $want $loggedTiles[$name] }
    }
    foreach ($name in $script:expected.Tiles.Keys) { if (-not $loggedTiles.Contains($name)) { Add-Result FAIL "Tile '$name'" 'not in the logged run' } }

    # Each device's line: its issues as HealthReport.IssueText words them, "healthy", or the reason it's offline.
    $run = @($entries[$startIndex..$finishIndex])
    foreach ($row in $script:expected.Rows) {
        if ($row.State -eq 'Skipped') { continue }
        $pattern = '^' + [regex]::Escape("Health Dashboard on $($row.Device): ")
        $line = $run | Where-Object { $_.Text -match $pattern } | Select-Object -Last 1
        if (-not $line) { Add-Result FAIL "$($row.Device) in the run" 'no line for it'; continue }
        $logged = $line.Text.Substring($line.Text.IndexOf(': ') + 2)
        switch ($row.State) {
            'Offline' { if ($logged -like 'Offline*') { Add-Result PASS "$($row.Device) in the run" $logged } else { Add-Result FAIL "$($row.Device) in the run" "expected it offline, got '$logged'" } }
            'Failed' { Add-Result INFO "$($row.Device) in the run" "logged '$logged'; this script couldn't connect: $($row.Status)" }
            default {
                $want = Get-IssueText $row
                if (-not $want) { $want = 'healthy' }
                if ($row.IssueSet.Contains('CouldNotCheck')) {
                    # Why a check couldn't be read is worded by WMI in the console and by CIM here: the names have to match, not the reasons.
                    $cut = { param($text) [regex]::Replace($text, ' \([^)]*\)', '') }
                    if ((& $cut $want) -eq (& $cut $logged)) { Add-Result PASS "$($row.Device) in the run" $logged } else { Add-Result FAIL "$($row.Device) in the run" "expected '$want', got '$logged'" }
                }
                else { Test-Equal "$($row.Device) in the run" $want $logged }
            }
        }
    }

    $policy = $run | Where-Object { $_.Text -match "^$scope`: the site's last policy request read for (\d+) device\(s\); (\d+) not reporting" } | Select-Object -Last 1
    if ($policy) {
        $null = $policy.Text -match "read for (\d+) device\(s\); (\d+) not reporting"
        Test-Equal 'Policy requests read from the site' $script:expected.Contacted $Matches[1]
        Test-Equal 'Not reporting, from the site' $script:expected.NotReporting $Matches[2]
    }
    else { Add-Result WARN 'Policy requests read from the site' 'no line for it in the run' }

    # Since the last run: the Changed tile against the run's own list of what changed (the run before it is gone now:
    # the dashboard has kept this one in its place).
    $after = @($entries[$startIndex..($entries.Count - 1)])

    # Devices that didn't answer: once the run ends, the dashboard reads what ConfigMgr last had on them (C: from the
    # last hardware inventory, the Windows build) and shows it in grey. It logs one line for that, after the run's own.
    $offlineCount = if ($loggedTiles.Contains('offline')) { [int]$loggedTiles['offline'] } else { 0 }
    $lastKnown = $after | Where-Object { $_.Text -match "^$scope`: ConfigMgr's last inventory read for (\d+) offline device\(s\)" } | Select-Object -First 1
    if ($offlineCount -eq 0) { Add-Result INFO 'Last known values for offline devices' 'no device was offline in this run' }
    elseif ($lastKnown) {
        $null = $lastKnown.Text -match 'read for (\d+) offline'
        Add-Result PASS 'Last known values for offline devices' "ConfigMgr's last inventory read for $($Matches[1]) of the $offlineCount offline"
    }
    else { Add-Result FAIL 'Last known values for offline devices' "$offlineCount offline, but no line says ConfigMgr's last inventory was read for them" }
    if ($loggedTiles.Contains('changed')) {
        $changes = $after | Where-Object { $_.Text -match "^$scope`: (since the run at .*?, (\d+) changed|no earlier run to compare with)" } | Select-Object -First 1
        if (-not $changes) { Add-Result FAIL "Tile 'changed'" "logged $($loggedTiles['changed']), but no line says what changed" }
        else {
            $null = $changes.Text -match "^$scope`: (since the run at .*?, (\d+) changed|no earlier run to compare with)"
            $count = '0'
            if ($Matches[2]) { $count = $Matches[2] }
            Test-Equal "Tile 'changed'" $count $loggedTiles['changed']
            Add-Result INFO 'Since the last run' $changes.Text.Substring($changes.Text.IndexOf(': ') + 2)
        }
    }
    else { Add-Result WARN "Tile 'changed'" 'not in the logged run: a build from before the since-last-run tile?' }

    # The run the dashboard kept to compare the next one with: each device's issues, as the Devices section reads them.
    $keptLine = $after | Where-Object { $_.Text -like "Health Dashboard on $Collection`: kept this run*" } | Select-Object -First 1
    $keptPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Layer8\Dashboard\{0}.txt' -f $script:expected.CollectionId)
    if (-not $keptLine) { Add-Result FAIL 'Run kept to compare with' 'no "kept this run" line after the last run' }
    elseif (-not (Test-Path -LiteralPath $keptPath)) { Add-Result FAIL 'Run kept to compare with' "$keptPath isn't there" }
    else {
        $kept = @{}
        foreach ($line in Get-Content -LiteralPath $keptPath -Encoding UTF8) {
            $fields = $line -split "`t"
            if ($fields[0] -eq 'device' -and $fields.Count -ge 4) { $kept[$fields[2]] = @($fields[3] -split '\|' | Where-Object { $_ } | Sort-Object) -join '|' }
        }
        $wrong = @(foreach ($row in $script:expected.Rows | Where-Object State -ne 'Skipped') {
            $want = @(Get-IssueKeys $row | Sort-Object) -join '|'
            if (-not $kept.ContainsKey($row.Device)) { "$($row.Device) missing" }
            elseif ($kept[$row.Device] -ne $want) { "$($row.Device): kept '$($kept[$row.Device])', expected '$want'" }
        })
        if ($wrong) { Add-Result FAIL 'Run kept to compare with' (($wrong | Select-Object -First 5) -join '; ') }
        else { Add-Result PASS 'Run kept to compare with' "$($kept.Count) devices in $keptPath, each with the issues the Devices section reads" }
    }
}

# --- Changes --------------------------------------------------------------------------------------------------------------

function Test-Changes {
    $checklist = Join-Path $repo 'docs\REGRESSION.md'
    if (-not (Test-Path -LiteralPath $checklist)) { Add-Result FAIL 'Checklist' 'docs\REGRESSION.md is missing'; return }

    # docs\STATUS.md gets each run's results before its commit, so the last commit that changed it is the last tested code.
    $base = @(& git -C $repo log -1 --format=%h -- docs/STATUS.md)[0]
    if (-not $base) { Add-Result WARN 'Changes' 'no commit has changed docs/STATUS.md'; return }
    $changed = @(@(& git -C $repo -c core.safecrlf=false diff --name-only $base) + @(& git -C $repo ls-files --others --exclude-standard) | Where-Object { $_ } | Sort-Object -Unique)
    Add-Result INFO 'Changed files' "$($changed.Count) since $base, the last commit that changed docs/STATUS.md (uncommitted work included)"
    if (-not $changed) { Add-Result INFO 'Checklist sections' 'nothing changed: the every-build steps only'; return }

    # Sections are "### X. Title" headings with a "Files:" line of backquoted patterns (* matches across folders).
    $sections = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in Get-Content -LiteralPath $checklist) {
        if ($line -match '^### ([A-Z][0-9]*)\. (.+)$') {
            $current = [pscustomobject]@{ Id = $Matches[1]; Title = $Matches[2]; Patterns = @(); Files = New-Object System.Collections.Generic.List[string] }
            $sections.Add($current)
        }
        elseif ($line -match '^## ') { $current = $null }
        elseif ($current -and $line -match '^Files: ') { $current.Patterns = @([regex]::Matches($line, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value }) }
    }

    foreach ($file in $changed) {
        $hit = @($sections | Where-Object { $candidate = $_; @($candidate.Patterns | Where-Object { $file -like $_ }).Count -gt 0 })
        if (-not $hit) { Add-Result WARN $file 'no checklist section names it: add it to a Files line in docs\REGRESSION.md' }
        foreach ($entry in $hit) { $entry.Files.Add($file) }
    }
    foreach ($entry in $sections | Where-Object { $_.Files.Count -gt 0 }) {
        Add-Result INFO "Run $($entry.Id). $($entry.Title)" (($entry.Files | Select-Object -First 6) -join ', ')
    }
}

# --- The report ------------------------------------------------------------------------------------------------------------

function Write-Report {
    $folder = Join-Path $repo 'Build\Regression'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $kind = 'before'
    if ($sinceGiven) { $kind = 'after' }
    $file = Join-Path $folder ('Layer8-{0}-{1:yyyyMMdd-HHmmss}-{2}.md' -f $version, $started, $kind)
    $cell = { param($text) ([string]$text).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }

    $commit = (& git -C $repo rev-parse --short HEAD)
    $branch = (& git -C $repo rev-parse --abbrev-ref HEAD)
    $dirty = @(& git -C $repo status --porcelain).Count
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Layer8 $version regression run ($kind the console steps)")
    $lines.Add('')
    $lines.Add(("{0:yyyy-MM-dd HH:mm} on {1}. Commit {2} on {3}, {4} uncommitted change(s)." -f $started, $env:COMPUTERNAME, $commit, $branch, $dirty))
    if ($sinceGiven) { $lines.Add(("Log checked from {0:yyyy-MM-dd HH:mm:ss}." -f $Since)) }
    $lines.Add('')
    $counts = ($results | Group-Object Result | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    $lines.Add(("Results: {0}, in {1:0} s." -f $counts, ((Get-Date) - $started).TotalSeconds))
    $lines.Add('')
    $lines.Add('| Section | Result | Check | Detail |')
    $lines.Add('|---|---|---|---|')
    foreach ($result in $results) { $lines.Add(('| {0} | {1} | {2} | {3} |' -f $result.Section, $result.Result, (& $cell $result.Check), (& $cell $result.Detail))) }

    if ($script:expected) {
        $columns = 'Device', 'Status', 'Online', 'Signed in', 'Up', 'C: free', 'Blue screen', 'Windows', 'Client', 'Restart', 'BitLocker', 'Defender', 'Updates', 'Update scan', 'Policy request', 'Issues'
        $lines.Add('')
        $lines.Add("## What the Health Dashboard should show on $Collection")
        $lines.Add('')
        $lines.Add('Tiles: ' + $script:expected.TileText)
        $lines.Add('')
        $lines.Add('| ' + ($columns -join ' | ') + ' |')
        $lines.Add('|' + (($columns | ForEach-Object { '---' }) -join '|') + '|')
        foreach ($row in $script:expected.Rows) {
            $values = foreach ($column in $columns) { & $cell $row.$column }
            $lines.Add('| ' + ($values -join ' | ') + ' |')
        }
    }
    if ($script:toolCalls) {
        $lines.Add('')
        $lines.Add("## What the client tools answered on $Collection")
        $lines.Add('')
        $lines.Add('| Device | Tool | Answer | Seconds |')
        $lines.Add('|---|---|---|---|')
        foreach ($call in $script:toolCalls | Where-Object State -ne 'Skipped') {
            $text = $call.Text
            if ($call.Summary) { $text += ': ' + $call.Summary }
            $lines.Add(('| {0} | {1} | {2} | {3:0.0} |' -f (& $cell $call.Device), $clientTools[$call.Tool], (& $cell $text), $call.Seconds))
        }
    }
    [IO.File]::WriteAllLines($file, $lines, (New-Object Text.UTF8Encoding($false)))
    $file
}

# --- Run ------------------------------------------------------------------------------------------------------------------

Write-Host "Layer8 $version regression checks, $($started.ToString('g'))" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $work | Out-Null
$report = $null
try {
    # The Tools section's 32-bit job, started now so its slowest device overlaps the sections before it. A problem
    # starting it is reported in that section.
    if ($Skip -notcontains 'Tools' -and $Skip -notcontains 'Devices') { try { Start-ToolJob } catch { } }
    foreach ($name in 'Build', 'Installed', 'Customize', 'Library', 'Settings', 'Devices', 'Tools', 'Log', 'Changes') {
        $section = $name
        Write-Host ''
        Write-Host $name -ForegroundColor Cyan
        if ($name -eq 'Log' -and -not $sinceGiven) { Add-Result SKIP 'Log' 'runs with -Since, after the console steps'; continue }
        if ($Skip -contains $name) { Add-Result SKIP $name 'skipped'; continue }
        try { & "Test-$name" }
        catch { Add-Result FAIL 'The section stopped' ("script error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) }
    }
}
finally {
    # A job the Tools section didn't collect (it stopped, or was skipped after the start).
    if ($toolJob) { Remove-Job -Job $toolJob.Job -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    $section = 'Run'
    $report = Write-Report
}

$failures = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ''
Write-Host ("{0}, in {1:0} s" -f (($results | Group-Object Result | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '), ((Get-Date) - $started).TotalSeconds) -ForegroundColor Cyan
Write-Host "Report: $report"
if (-not $sinceGiven) {
    Write-Host ''
    Write-Host 'After the console steps in docs\REGRESSION.md, run:'
    Write-Host ("  .\tools\Test-Layer8Regression.ps1 -Since '{0:yyyy-MM-dd HH:mm:ss}'" -f $started)
}
exit $failures
