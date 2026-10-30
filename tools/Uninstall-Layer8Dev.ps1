<#
.SYNOPSIS
    Removes a developer install of Layer8 from the ConfigMgr console on this machine.

.DESCRIPTION
    Reads %ProgramData%\Layer8\install-manifest.txt (written by Install-Layer8Dev.ps1) and
    deletes exactly the files listed there, then any folders under XmlStorage\Extensions that
    those files left empty. It refuses to touch anything outside the console's own folder.

    Run it from an elevated PowerShell with the ConfigMgr console closed.
    Use -WhatIf to see what would be removed without removing anything.

.EXAMPLE
    .\tools\Uninstall-Layer8Dev.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

if (-not $env:SMS_ADMIN_UI_PATH) { throw 'ConfigMgr console not found on this machine (SMS_ADMIN_UI_PATH is not set).' }
$console = Split-Path (Split-Path $env:SMS_ADMIN_UI_PATH)
$extensions = Join-Path $console 'XmlStorage\Extensions'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: it removes files from Program Files.'
}
if (Get-Process Microsoft.ConfigurationManagement -ErrorAction SilentlyContinue) {
    throw 'Close the ConfigMgr console first: it locks the DLLs this script removes.'
}

$manifest = Join-Path $env:ProgramData 'Layer8\install-manifest.txt'
if (-not (Test-Path $manifest)) { throw "No manifest at $manifest, so there's nothing recorded to uninstall." }

$removed = 0
$folders = New-Object System.Collections.Generic.HashSet[string]
foreach ($file in Get-Content $manifest) {
    if (-not $file.StartsWith($console + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Skipping a path outside the console folder: $file"
        continue
    }
    if (Test-Path $file) {
        if ($PSCmdlet.ShouldProcess($file, 'Remove')) { Remove-Item $file -Force; $removed++ }
    }
    if ($file.StartsWith($extensions + '\', [StringComparison]::OrdinalIgnoreCase)) { [void]$folders.Add((Split-Path $file)) }
}

# Remove extension folders our files left empty, deepest first, never above XmlStorage\Extensions.
foreach ($folder in ($folders | Sort-Object Length -Descending)) {
    $current = $folder
    while ($current.Length -gt $extensions.Length -and (Test-Path $current) -and -not (Get-ChildItem $current -Force)) {
        if ($PSCmdlet.ShouldProcess($current, 'Remove empty folder')) { Remove-Item $current } else { break }
        $current = Split-Path $current
    }
}

if (-not $WhatIfPreference) { Remove-Item $manifest }
Write-Host "Removed $removed files."
