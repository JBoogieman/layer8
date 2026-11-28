<#
.SYNOPSIS
    Runs docs\REGRESSION.md's console steps through UI Automation: it presses what a person would click in the
    ConfigMgr console and Layer8's windows, and reads what they show as text. No screenshots.

.DESCRIPTION
    Run it after Test-Layer8Regression.ps1, whose report says what each window should show, and before that
    script's -Since run, which checks what Layer8 logged while this ran.

    It drives the console that's open on this computer, so it needs:
      - the console open and connected, on an unlocked desktop (over RDP, the window open, not minimized)
      - hands off the keyboard and mouse while it runs: it presses a few keys (Space on a tile, and Shift+F10
        when the ribbon has no Layer8 drop-down), and keys go to the window in front
      - TlbImp.exe from the Windows SDK (Visual Studio installs it), to generate the UI Automation interop
        assembly into Build\Uia the first time

    It presses only what its allowlist names: tools\Layer8Uia.cs refuses any other action, and any keystroke
    while a window outside the console is in front. It never opens LAPS Password or BitLocker Recovery Key, and
    never reads a window titled LAPS, BitLocker, Recovery or Password. Nothing it does changes a device, the site
    or Active Directory.

    The sections:

      Dashboard  The Health Dashboard on the collection: opened from the ribbon (or the right-click menu), timed to
                 its "Checked n devices" line, every row and tile compared with the regression script's Devices
                 section, sorting by C: free, Policy request and Issues, the filter box alone and with a tile, the
                 Devices tile and an empty filter showing them all again, then closed.
      ADCleanup  The AD Cleanup Report (REGRESSION.md section R): opened three ways, every row and tile compared with
                 the regression script's ADCleanup section at Layer8 Settings' days and at 1 day (set through Layer8
                 Settings, and set back in a finally block), the status line, a tile with the filter box, Workstations
                 only, sorting, Copy, which tiles show the Fix button, Fix with nothing selected, and the objects no fix
                 touches. With -WithFixes and the test objects in OU=Layer8 Test, the fix steps: the runner selects
                 the rows (each checked in AD to be an L8-TEST- object there), opens the Fix menu, reads each question
                 and answers No where the step says so; every Yes and count typed is yours, and so is Move to OU's
                 list (what it offers, the OU, its OK: it can't be read while open), each asked for in one line, and
                 the runner reads AD by SID before and after. It never changes AD itself.

    Each check prints PASS, FAIL, WARN, SKIP or INFO. The report goes to Build\Regression, and the exit code is
    the number of FAILs.

.PARAMETER Skip
    Sections to leave out: Dashboard, ADCleanup.

.PARAMETER WithFixes
    Runs section R's fix steps, which change Active Directory through your own clicks: run it in a window you
    watch, since it stops at each one and says what to click or type. Needs the test objects in OU=Layer8 Test.

.PARAMETER FixTile
    With -WithFixes, the tiles whose fix steps run: Disabled (Move to OU twice, Enable, Delete), Stale (Disable and
    Delete two, from the day after the test objects are made), or both (the default). Each step also needs its own
    test objects, so the Disabled steps, which delete the second L8-TEST-DUP, run once per set of test objects.

.PARAMETER Report
    The regression script's report to compare with. Default: the newest *-before.md in Build\Regression that has
    the collection's dashboard table.

.PARAMETER Collection
    The device collection. Default: All Systems.

.EXAMPLE
    .\tools\Test-Layer8Console.ps1
#>
[CmdletBinding()]
param(
    # Not -Section: PowerShell names ignore case, so it would be the script's own $section (the report's column),
    # and its ValidateSet would refuse the script's values.
    [ValidateSet('Dashboard', 'ADCleanup')]
    [string[]]$Skip,
    [string]$Report,
    [string]$Collection = 'All Systems',
    [switch]$WithFixes,
    [ValidateSet('Disabled', 'Stale')]
    [string[]]$FixTile = @('Disabled', 'Stale')
)

# The console is 32-bit, and this was tested in 32-bit PowerShell against it (November 26, 2026), so run as it.
if ([IntPtr]::Size -eq 8) {
    # PowerShell's own escaping, which also doubles the curly quotes: PowerShell reads those as single quotes too.
    $quote = { param($text) "'" + [Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent([string]$text) + "'" }
    $command = '& ' + (& $quote $PSCommandPath)
    foreach ($name in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$name]
        # A switch as -Name:$true, or its quoted value would bind as a stray argument.
        if ($value -is [switch]) { $command += " -$name`:$" + $value.IsPresent.ToString().ToLowerInvariant(); continue }
        $command += " -$name " + ((@($value) | ForEach-Object { & $quote $_ }) -join ', ')
    }
    Write-Host 'Started in 64-bit PowerShell: running in 32-bit PowerShell instead.' -ForegroundColor DarkGray
    # A failure before the script runs (a parameter it refuses) sets no exit code, so it's caught and counted as one.
    & (Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -Command ('try { ' + $command + '; exit $LASTEXITCODE } catch { $_; exit 1 }')
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$started = Get-Date
$repo = Split-Path $PSScriptRoot -Parent
$logFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Layer8\Logs'
$up = [char]0x25B2
$down = [char]0x25BC

# --- Results (the regression script's style) -------------------------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]
$resultColors = @{ PASS = 'Green'; FAIL = 'Red'; WARN = 'Yellow'; SKIP = 'DarkGray'; INFO = 'Gray' }
$section = 'Start'

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

# --- Setup ---------------------------------------------------------------------------------------------------------

# The interop assembly for Windows' UI Automation COM API, generated from UIAutomationCore.dll by the SDK's TlbImp.
# Generated here, not kept in git: it's Windows' own type library, and a newer Windows can add to it.
function Get-Interop {
    $folder = Join-Path $repo 'Build\Uia'
    $dll = Join-Path $folder 'Interop.UIAutomationClient.dll'
    $core = Join-Path $env:SystemRoot 'System32\UIAutomationCore.dll'
    if ((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTime -ge (Get-Item -LiteralPath $core).LastWriteTime) { return $dll }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows'
    $tlbimp = @(Get-ChildItem -LiteralPath $sdk -Recurse -Filter TlbImp.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -notmatch '\\x64$' } | Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending) | Select-Object -First 1
    if (-not $tlbimp) { throw "TlbImp.exe isn't under $sdk (the Windows SDK, which Visual Studio installs): it makes the UI Automation interop assembly" }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $output = & $tlbimp.FullName $core "/out:$dll" /namespace:Interop.UIAutomationClient /silent 2>&1
    if ($LASTEXITCODE -or -not (Test-Path -LiteralPath $dll)) { throw "TlbImp couldn't make $dll`: $($output -join ' ')" }
    Add-Result INFO 'UI Automation interop' "generated with $($tlbimp.FullName)"
    $dll
}

# Reads a file the console may be writing to.
function Read-SharedText([string]$Path) {
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)).ReadToEnd() } finally { $stream.Dispose() }
}

# The Layer8 version the console last loaded, from Layer8.log.
function Get-LoadedVersion {
    $log = Join-Path $logFolder 'Layer8.log'
    if (-not (Test-Path -LiteralPath $log)) { return $null }
    $found = [regex]::Matches((Read-SharedText $log), 'Layer8 ([0-9.]+) loaded in console 5\.')
    if ($found.Count) { $found[$found.Count - 1].Groups[1].Value } else { $null }
}

# A cell of a report table: the regression script writes | as \| and line breaks as spaces.
function Split-Row([string]$Line) {
    $inner = $Line.Trim()
    if ($inner.StartsWith('|')) { $inner = $inner.Substring(1) }
    if ($inner.EndsWith('|') -and -not $inner.EndsWith('\|')) { $inner = $inner.Substring(0, $inner.Length - 1) }
    @([regex]::Split($inner, '(?<!\\)\|') | ForEach-Object { $_.Replace('\|', '|').Trim() })
}

# What the regression script's Devices section says the dashboard should show: its tile counts and each row.
function Read-Expected([string]$Path) {
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    $version = $null
    if ($lines[0] -match '^# Layer8 (\S+) regression run') { $version = $Matches[1] }
    $heading = [array]::IndexOf($lines, "## What the Health Dashboard should show on $Collection")
    if ($heading -lt 0) { return $null }
    $tiles = [ordered]@{}
    $rows = [ordered]@{}
    $columns = $null
    for ($i = $heading + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -like '## *') { break }
        if ($line -match '^Tiles: (.*)$') {
            foreach ($pair in $Matches[1] -split ', ') { if ($pair -match '^(.*) (\d+)$') { $tiles[$Matches[1]] = [int]$Matches[2] } }
            continue
        }
        if ($line -notlike '|*') { continue }
        $cells = Split-Row $line
        if (-not $columns) { $columns = $cells; continue }
        if ($cells[0] -match '^-+$') { continue }
        $row = [ordered]@{}
        for ($c = 0; $c -lt $columns.Count; $c++) { $row[$columns[$c]] = $(if ($c -lt $cells.Count) { $cells[$c] } else { '' }) }
        $rows[$row['Device']] = $row
    }
    [pscustomobject]@{ Path = $Path; Version = $version; Written = (Get-Item -LiteralPath $Path).LastWriteTime; Tiles = $tiles; Rows = $rows }
}

# The newest before-report holding a line the section needs (the dashboard's table, or the ADCleanup section's).
function Find-Report([string]$Needle) {
    if ($Report) { return (Resolve-Path -LiteralPath $Report).ProviderPath }
    $folder = Join-Path $repo 'Build\Regression'
    foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter '*-before.md' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)) {
        if (Select-String -LiteralPath $file.FullName -SimpleMatch $Needle -Quiet) { return $file.FullName }
    }
    $null
}

function Test-ReportAge([string]$Path, [string]$Version) {
    $age = (Get-Date) - (Get-Item -LiteralPath $Path).LastWriteTime
    $ageText = '{0:0} minutes old' -f $age.TotalMinutes
    if ($age.TotalMinutes -gt 60) { Add-Result WARN 'The regression report' "$(Split-Path $Path -Leaf), $ageText`: what it read moves, so run the script again first" }
    else { Add-Result INFO 'The regression report' "$(Split-Path $Path -Leaf), $ageText" }
    if ($Version -and $Version -ne $script:loaded) { Add-Result WARN 'Report and console versions' "the report is from $Version, the console runs $($script:loaded)" }
}

# --- The console -------------------------------------------------------------------------------------------------

$U = $null
$main = $null

function Wait-Until([int]$TimeoutMs, [scriptblock]$Test) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $value = & $Test
        if ($value) { return $value }
        if ($watch.ElapsedMilliseconds -ge $TimeoutMs) { return $null }
        Start-Sleep -Milliseconds 200
    }
}

# Device Collections with the collection selected. The console names every result row the same (read live, the
# selected row's text; cached, "Result Item Details"), so the row is found by its Name cell (column 1; 0 is the icon).
function Select-Collection([string]$Name = $Collection) {
    $workspace = $main.Find('Button', 'Assets and Compliance', 5000)
    if (-not $workspace) { throw 'the console has no Assets and Compliance workspace button' }
    $workspace.Invoke()
    $node = $main.Find('TreeItem', 'Device Collections', 5000)
    if (-not $node) { throw 'the navigation tree has no Device Collections' }
    $node.Select()
    $grid = $main.Find('DataGrid', 'Result Details', 10000)
    if (-not $grid) { throw 'Device Collections shows no result grid' }
    $row = Wait-Until 10000 {
        $rows = $grid.Rows()
        for ($i = 0; $i -lt $rows.Count; $i++) {
            if ($rows[$i].Count -gt 1 -and $rows[$i][1] -eq $Name) { return $grid.FindAll('DataItem', '')[$i] }
        }
    }
    if (-not $row) { throw "Device Collections has no collection named $Name" }
    $row.Select()
    # The details pane names what's selected: nothing opens on another collection.
    $selected = Wait-Until 3000 { $box = $main.Find('Edit', 'Name:', 0); if ($box -and $box.Value -eq $Name) { $box } }
    if (-not $selected) { throw "selecting $Name didn't show it in the details pane" }
    $row
}

# Opens a Layer8 item for the selected collection. The ribbon first: at the window widths seen, the collapsed
# Collection group's popup holds the collection's Layer8 drop-down, so no key is pressed (phase 0, November 26).
# Then the right-click menu, which only a key opens (Shift+F10 on the focused row).
function Open-Layer8Item([string]$Item, $Row, [ValidateSet('Any', 'Ribbon', 'Menu')][string]$Route = 'Any') {
    $group = $null
    if ($Route -ne 'Menu') { $group = $main.Find('Group', 'Collection', 3000) }
    if ($Route -eq 'Ribbon' -and -not $group) { throw 'the ribbon has no Collection group' }
    if ($group) {
        # Twice: right after a report window closes, the ribbon's popup can close again as it opens (November 26, 21:03).
        foreach ($attempt in 1, 2) {
            # The popup can also close between finding the drop-down and expanding it: Expand then throws
            # UIA_E_ELEMENTNOTAVAILABLE (0x80040200; the third opening, right after a report closed, November 27,
            # 08:12). That COM error is caught here so the second attempt finds the drop-down afresh.
            try {
                $group = $main.Find('Group', 'Collection', 3000)
                if ($group.Has('ExpandCollapse')) { $group.Expand(); Start-Sleep -Milliseconds 700 }
                $dropDown = $group.Find('MenuBar', 'Layer8', 0)
                if (-not $dropDown) {
                    foreach ($w in $U::Windows()) { if ($w.Handle -ne $U::MainHandle) { $dropDown = $w.Find('MenuBar', 'Layer8', 0); if ($dropDown) { break } } }
                }
                if ($dropDown) {
                    $dropDown.Expand()
                    # The drop-down's items show twice: under it, where they offer Invoke, and in a popup copy that doesn't
                    # (probe dumps, November 26). So the one to press is the one with Invoke.
                    $menuItem = Wait-Until 5000 {
                        @($dropDown.FindAll('MenuItem', $Item)) + @($U::Windows() | Where-Object { $_.Handle -ne $U::MainHandle } | ForEach-Object { $_.FindAll('MenuItem', $Item) }) |
                            Where-Object { $_.Has('Invoke') } | Select-Object -First 1
                    }
                    if ($menuItem) { $menuItem.Invoke(); return 'the ribbon' }
                }
            }
            catch {
                if ($_.Exception.InnerException -isnot [Runtime.InteropServices.COMException]) { throw }
                Add-Result INFO "The ribbon, attempt $attempt" $_.Exception.InnerException.Message
            }
            Close-Menus
            try { if ($group.Has('ExpandCollapse')) { $group.Collapse() } } catch { }
            Start-Sleep -Milliseconds 1000
        }
        if ($Route -eq 'Ribbon') { throw "the ribbon's Layer8 drop-down has no $Item to press" }
    }
    if (-not $U::ConsoleInFront() -and -not $U::BringToFront()) { throw "the console isn't in front, so no key was pressed" }
    # Brought to the front, the console can still leave the keyboard focus on the desktop ("Desktop 1"), and then
    # the row refuses focus ("not valid due to the current state", November 26): bring it forward again and retry.
    try { $Row.Focus() }
    catch {
        $null = $U::BringToFront()
        Start-Sleep -Milliseconds 500
        $Row.Focus()
    }
    Start-Sleep -Milliseconds 300
    $U::Key('Shift+F10', $Row)
    $layer8 = $U::WaitMenuItem('Layer8', 3000)
    if (-not $layer8) {
        # The first key right after the console comes to the front can be lost (November 26, 21:00): once more.
        Close-Menus
        $null = $U::BringToFront()
        $Row.Focus()
        Start-Sleep -Milliseconds 500
        $U::Key('Shift+F10', $Row)
        $layer8 = $U::WaitMenuItem('Layer8', 3000)
    }
    if (-not $layer8) { throw "Shift+F10 opened no menu with Layer8 in it" }
    $layer8.Expand()
    $menuItem = $U::WaitMenuItem($Item, 3000)
    if (-not $menuItem) { throw "Layer8's menu has no $Item" }
    $menuItem.Invoke()
    'the right-click menu'
}

function Close-Menus {
    foreach ($i in 1..3) {
        # Menus and ribbon popups have no title; an empty offscreen popup can linger, so only those holding a menu item count.
        $open = @($U::Windows() | Where-Object { $_.Handle -ne $U::MainHandle -and -not $_.Name -and $_.Find('MenuItem', '', 0) })
        if (-not $open -or -not $U::ConsoleInFront()) { break }
        $U::Key('Escape', $null)
        Start-Sleep -Milliseconds 300
    }
}

# --- Dashboard -----------------------------------------------------------------------------------------------------

# A column's header without the sort arrow the dashboard adds.
function Get-ColumnName([string]$Header) { $Header.Replace([string]$up, '').Replace([string]$down, '').Trim() }

function Read-Grid($List) {
    $headers = @($List.Columns() | ForEach-Object { Get-ColumnName $_ })
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($cells in $List.Rows()) {
        $row = [ordered]@{}
        for ($c = 0; $c -lt $headers.Count; $c++) { $row[$headers[$c]] = $(if ($c -lt $cells.Count) { $cells[$c] } else { '' }) }
        $rows.Add($row)
    }
    $rows
}

function Read-Tiles($Window) {
    $tiles = [ordered]@{}
    foreach ($button in $Window.FindAll('Button', '')) {
        if ($button.Name -match '^(.+): (\d+)(?:, was \d+)?$') { $tiles[$Matches[1]] = [pscustomobject]@{ Count = [int]$Matches[2]; Element = $button } }
    }
    $tiles
}

function Get-Shown($Window) {
    $label = $Window.FindLike('Text', 'Showing .*', 2000)
    if ($label) { $label.Name } else { '' }
}

# Sizes as the dashboard writes them ("79.6 GB of 149 GB"), in GB.
function ConvertTo-GB([string]$Text) {
    if ($Text -match '^([0-9.,]+) (KB|MB|GB|TB) of') { [double]$Matches[1] * @{ KB = 1 / 1MB; MB = 1 / 1KB; GB = 1; TB = 1KB }[$Matches[2]] } else { $null }
}

function Test-Order([double[]]$Values) {
    $rising = $true; $falling = $true
    for ($i = 1; $i -lt $Values.Count; $i++) {
        if ($Values[$i] -lt $Values[$i - 1]) { $rising = $false }
        if ($Values[$i] -gt $Values[$i - 1]) { $falling = $false }
    }
    if ($rising -and $falling) { 'flat' } elseif ($rising) { 'up' } elseif ($falling) { 'down' } else { 'unsorted' }
}

function Invoke-Header($List, [string]$Column) {
    $headers = @($List.FindAll('HeaderItem', ''))
    $index = -1
    for ($i = 0; $i -lt $headers.Count; $i++) { if ((Get-ColumnName $headers[$i].Name) -eq $Column) { $index = $i; break } }
    if ($index -lt 0) { throw "the grid has no $Column column" }
    $header = $headers[$index]
    # UI Automation clicks a header only while the grid isn't scrolled sideways. Scrolled (by the scroll bar's own
    # message, or LVM_SCROLL) to show the dashboard's last columns, Policy request's header refused Invoke and its
    # MSAA default action, was hit-tested as Blue screen's, and a guarded mouse click on it sorted nothing
    # (November 26). The columns these checks sort are in view without scrolling.
    if ($header.IsOffscreen) { throw "the $Column header is out of view, and UI Automation can't click a header in a grid scrolled sideways" }
    $header.Invoke()
    Start-Sleep -Milliseconds 500
}

# Clicks a column twice: each click must sort its values one way, the second the other way.
function Test-SortBothWays($List, [string]$Column, [scriptblock]$Key) {
    $orders = @()
    foreach ($click in 1, 2) {
        try { Invoke-Header $List $Column }
        catch {
            if ($_.Exception.Message -like '*out of view*') { Add-Result SKIP "Sorting by $Column" "by hand: $($_.Exception.Message)"; return }
            throw
        }
        $values = @(Read-Grid $List | ForEach-Object { & $Key $_[$Column] } | Where-Object { $null -ne $_ })
        $orders += Test-Order $values
    }
    $distinct = $orders -notcontains 'flat'
    if ($orders -contains 'unsorted') { Add-Result FAIL "Sorting by $Column" "orders after two clicks: $($orders -join ', ')" }
    elseif ($distinct -and $orders[0] -ne $orders[1]) { Add-Result PASS "Sorting by $Column" "both ways ($($orders -join ', then '))" }
    elseif (-not $distinct) { Add-Result WARN "Sorting by $Column" 'too few different values to tell' }
    else { Add-Result FAIL "Sorting by $Column" "the same way twice ($($orders -join ', '))" }
}

# Space on a tile: UI Automation's Invoke doesn't click one (no default action; phase 0, November 26).
function Push-Tile($Window, [string]$Caption) {
    $tile = (Read-Tiles $Window)[$Caption]
    if (-not $tile) { throw "the dashboard has no $Caption tile" }
    $tile.Element.Focus()
    Start-Sleep -Milliseconds 300
    $U::Key('Space', $tile.Element)
    Start-Sleep -Milliseconds 700
}

# A WinForms button by the keyboard, as a tile is: focus, then Space. Invoke runs the click inside the call, so a
# MessageBox the click opens holds it, and until that closes UI Automation can't read the console at all (the Fix
# button with nothing selected, November 27, 08:14 and 08:25). A key is sent and done: the box can be read and answered.
function Push-Button($Button) {
    $Button.Focus()
    Start-Sleep -Milliseconds 300
    $U::Key('Space', $Button)
    Start-Sleep -Milliseconds 500
}

function Set-Filter($Window, [string]$Text) {
    $Window.Find('Edit', 'Filter:', 2000).SetValue($Text)
    Start-Sleep -Milliseconds 700
}

function Test-Dashboard {
    $reportPath = Find-Report "## What the Health Dashboard should show on $Collection"
    if (-not $reportPath) { Add-Result FAIL 'The regression report' "none in Build\Regression with a dashboard table for $Collection`: run tools\Test-Layer8Regression.ps1 first"; return }
    $expected = Read-Expected $reportPath
    if (-not $expected) { Add-Result FAIL 'The regression report' "$reportPath has no dashboard table for $Collection"; return }
    Test-ReportAge $reportPath $expected.Version

    $U::Allow('Microsoft Configuration Manager.*', 'Button', 'Assets and Compliance', 'Invoke')
    $U::Allow('Microsoft Configuration Manager.*', 'TreeItem', 'Device Collections', 'Select')
    $U::Allow('Microsoft Configuration Manager.*', 'DataItem', '.*', 'Select,Focus')
    $U::Allow('Microsoft Configuration Manager.*', 'Group', 'Collection', 'Expand,Collapse')
    $U::Allow('.*', 'MenuBar', 'Layer8', 'Expand,Collapse')
    $U::Allow('.*', 'MenuItem', 'Layer8', 'Expand')
    $U::Allow('.*', 'MenuItem', 'Health Dashboard', 'Invoke')
    # The dashboard's own question ("Run Health Dashboard on <collection>? It only reads, ..."), above Layer8
    # Settings' Confirm above; its text is checked before Yes is pressed.
    $U::Allow('Health Dashboard', 'Button', 'Yes', 'Invoke')
    $windowTitle = [regex]::Escape("Health Dashboard: $Collection")
    $U::Allow($windowTitle, 'HeaderItem', '.*', 'Invoke')
    $U::Allow($windowTitle, 'Edit', 'Filter:', 'SetValue')
    $U::Allow($windowTitle, 'Button', "[^:]+: \d+(, was \d+)?", 'Focus')
    $U::Allow($windowTitle, 'Button', 'Close', 'Invoke')
    $U::Allow('', 'Key', 'Shift\+F10|Escape|Space', 'Key')

    # A menu a stopped run left open.
    Close-Menus
    $row = Select-Collection
    $clicked = Get-Date
    $route = Open-Layer8Item 'Health Dashboard' $row
    Add-Result PASS 'Opened from' $route
    # The dashboard, or first its question when the collection has more devices than Layer8 Settings' Confirm above.
    $window = $U::WaitWindow('Window', "$windowTitle|Health Dashboard", 15000)
    if ($window -and $window.Name -eq 'Health Dashboard') {
        $text = @($window.FindAll('Text', '') | ForEach-Object Name) -join ' '
        if ($text -notmatch "^Run Health Dashboard on $([regex]::Escape($Collection))\? It only reads") { throw "an unexpected question, left open: $text" }
        Add-Result INFO 'The question' $text
        $window.Find('Button', 'Yes', 1000).Invoke()
        $window = $U::WaitWindow('Window', $windowTitle, 15000)
    }
    if (-not $window) { throw "no Health Dashboard: $Collection window within 15 seconds" }
    try {
        $status = $window.FindLike('Text', 'Checked \d+ device.*', 120000)
        if (-not $status) { Add-Result FAIL 'The run' "no 'Checked n devices' status line within 2 minutes"; return }
        $seconds = ((Get-Date) - $clicked).TotalSeconds
        if ($seconds -le 30) { Add-Result PASS 'Finished in seconds' ("{0:0.0} s from the click: {1}" -f $seconds, $status.Name) }
        else { Add-Result WARN 'Finished in seconds' ("{0:0.0} s from the click: {1}" -f $seconds, $status.Name) }

        $list = $window.Find('List', '', 2000)
        $shown = @(Read-Grid $list)
        $script:dashboardRows = $shown
        Test-Equal 'Devices in the grid' $expected.Rows.Count $shown.Count

        # Placeholders: skipped by name, nothing read.
        $skipped = @($shown | Where-Object { $_['Status'] -eq 'Skipped: not a computer name' } | ForEach-Object { $_['Device'] })
        $expectedSkipped = @($expected.Rows.Values | Where-Object { $_['Status'] -eq 'Skipped: not a computer name' } | ForEach-Object { $_['Device'] })
        if ((@($skipped | Sort-Object) -join '|') -ceq (@($expectedSkipped | Sort-Object) -join '|')) { Add-Result PASS 'Placeholders skipped by name' ($skipped -join ', ') }
        else { Add-Result FAIL 'Placeholders skipped by name' "shown: $($skipped -join ', '); the script: $($expectedSkipped -join ', ')" }

        # The columns that must read what the script read, and those that move with the clock.
        $exact = [ordered]@{ Status = 'Status'; 'Signed in' = 'Signed in'; 'C: free' = 'C: free'; Restart = 'Restart'; BitLocker = 'BitLocker'
            Defender = 'Defender'; 'Blue screen' = 'Blue screen'; Windows = 'Windows'; 'Updates missing' = 'Updates'; Client = 'Client'; Issues = 'Issues' }
        $clock = 'Up', 'Update scan', 'Policy request'
        foreach ($row in $shown) {
            $name = $row['Device']
            if ($skipped -contains $name) { continue }
            $want = $expected.Rows[$name]
            if (-not $want) { Add-Result FAIL "$name" "on the dashboard, not in the script's reading"; continue }
            $differ = @()
            $moved = @()
            foreach ($column in $exact.Keys) {
                $got = $row[$column]; $wanted = $want[$exact[$column]]
                if ("$got" -ceq "$wanted") { continue }
                $gotGB = ConvertTo-GB $got; $wantedGB = ConvertTo-GB $wanted
                if ($column -eq 'C: free' -and $null -ne $gotGB -and $null -ne $wantedGB -and [math]::Abs($gotGB - $wantedGB) -le 1) { $moved += "C: free '$got' (the script read '$wanted')"; continue }
                $differ += "$column '$got' (the script read '$wanted')"
            }
            foreach ($column in $clock) {
                if ([bool]$row[$column] -ne [bool]$want[$column]) { $differ += "$column '$($row[$column])' (the script read '$($want[$column])')" }
            }
            if ($differ) { Add-Result FAIL "$name" ($differ -join '; ') }
            elseif ($moved) { Add-Result WARN "$name" ("as the script read, except " + ($moved -join '; ')) }
            else { Add-Result PASS "$name" ("as the script read: {0}; Issues '{1}'" -f $row['Status'], $row['Issues']) }
        }

        # Tiles: the script's counts by name; Devices and Changed are the dashboard's own.
        $tiles = Read-Tiles $window
        $differ = @()
        foreach ($caption in $tiles.Keys) {
            $key = $caption.ToLowerInvariant()
            if (-not $expected.Tiles.Contains($key)) { continue }
            if ($tiles[$caption].Count -ne $expected.Tiles[$key]) { $differ += "$caption $($tiles[$caption].Count) (the script: $($expected.Tiles[$key]))" }
        }
        $missing = @($expected.Tiles.Keys | Where-Object { $key = $_; -not ($tiles.Keys | Where-Object { $_.ToLowerInvariant() -eq $key }) })
        if ($missing) { $differ += "no tile for: $($missing -join ', ')" }
        $tileText = ($tiles.Keys | ForEach-Object { "$_ $($tiles[$_].Count)" }) -join ', '
        if ($differ) { Add-Result FAIL 'Tiles' ($differ -join '; ') } else { Add-Result PASS 'Tiles' $tileText }

        # Sorting (A): C: free by size, both ways; Issues puts the most first. Policy request, the last column, is
        # only in view with the grid scrolled sideways, where UI Automation can't click a header (see Invoke-Header).
        Test-SortBothWays $list 'C: free' { param($v) ConvertTo-GB $v }
        Add-Result SKIP 'Sorting by Policy request' 'by hand: UI Automation can''t click a header in a grid scrolled sideways'
        $issuesFirst = $false
        foreach ($click in 1, 2) {
            Invoke-Header $list 'Issues'
            $counts = @(Read-Grid $list | ForEach-Object { if ($_['Status'] -match '^(\d+) issues?$') { [int]$Matches[1] } elseif ($_['Status'] -match '^Skipped') { -1 } else { 0 } })
            if ((Test-Order $counts) -in 'down', 'flat') { $issuesFirst = $true; break }
        }
        if ($issuesFirst) { Add-Result PASS 'Sorting by Issues' "most first, placeholders after (issue counts: $($counts -join ', '))" }
        else { Add-Result FAIL 'Sorting by Issues' "neither click put the most issues first (last: $($counts -join ', '))" }
        foreach ($click in 1, 2) {
            if (@($list.Columns())[0] -like "*$up") { break }
            Invoke-Header $list 'Device'
        }

        # The filter box alone, then with a tile; Devices and an empty filter show them all again.
        $named = @($shown | Where-Object { $skipped -notcontains $_['Device'] -and $_['BitLocker'] -and $_['Issues'] -like '*BitLocker off*' } | Select-Object -Last 1)
        if (-not $named) { Add-Result SKIP 'Filter and tile' 'no device on BitLocker off to filter for'; return }
        $device = $named[0]['Device']
        Set-Filter $window $device
        $one = @(Read-Grid $list | ForEach-Object { $_['Device'] })
        if ($one.Count -eq 1 -and $one[0] -eq $device) { Add-Result PASS "Filter '$device'" (Get-Shown $window) }
        else { Add-Result FAIL "Filter '$device'" "rows: $($one -join ', '); $(Get-Shown $window)" }
        Push-Tile $window 'BitLocker off'
        $one = @(Read-Grid $list | ForEach-Object { $_['Device'] })
        if ($one.Count -eq 1 -and $one[0] -eq $device) { Add-Result PASS "BitLocker off with '$device' in the filter" (Get-Shown $window) }
        else { Add-Result FAIL "BitLocker off with '$device' in the filter" "rows: $($one -join ', '); $(Get-Shown $window)" }
        Push-Tile $window 'Devices'
        Set-Filter $window ''
        $all = @(Read-Grid $list)
        $text = Get-Shown $window
        if ($all.Count -eq $shown.Count -and $text -match '^Showing all') { Add-Result PASS 'Devices and an empty filter show them all again' $text }
        else { Add-Result FAIL 'Devices and an empty filter show them all again' "$($all.Count) rows; $text" }
    }
    finally {
        $close = @($window.FindAll('Button', 'Close')) | Select-Object -Last 1
        if ($close) { $close.Invoke() }
        if (Wait-Until 3000 { -not $U::WaitWindow('Window', $windowTitle, 0) }) { Add-Result PASS 'Dashboard closed' '' }
        else { Add-Result FAIL 'Dashboard closed' "it's still open" }
    }
}

# --- AD Cleanup Report (section R) ---------------------------------------------------------------------------------

# The runner never changes Active Directory: every Yes, typed count and Move to OU OK in a fix is the tester's. It reads
# AD only, by distinguished name and SID, before and after each of those clicks.
$findingCaptions = @{ OK = 'OK'; NotInAD = 'Not in AD'; Disabled = 'Disabled'; Stale = 'Stale'; ADUnused = 'AD unused'; ClientSilent = 'Client silent'; NoClient = 'No client'; NotChecked = 'Not checked' }
$reportTitle = 'AD Cleanup Report'
$fixTitles = 'Disable|Enable|Move to OU|Delete'
$testOU = 'OU=Layer8 Test'

# The regression script's ADCleanup section: each computer's tile at the setting's days and at 1 day, and the counts.
function Read-ADExpected([string]$Path) {
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    $version = $null
    if ($lines[0] -match '^# Layer8 (\S+) regression run') { $version = $Matches[1] }
    $at = @{}
    $objects = $null; $records = $null; $domain = $null
    foreach ($line in $lines) {
        if ($line -notlike '| ADCleanup |*') { continue }
        $cells = Split-Row $line
        if ($cells.Count -lt 4) { continue }
        $check = $cells[2]; $detail = $cells[3]
        if ($check -match '^At (\d+) days?: (.+)$') {
            $days = [int]$Matches[1]; $name = $Matches[2]
            if ($detail -match '^(\w+)(?: \((.*)\))?$' -and $findingCaptions.ContainsKey($Matches[1])) {
                $entry = [pscustomobject]@{ Tile = $findingCaptions[$Matches[1]]; Note = $Matches[2] }
                if (-not $at.ContainsKey($days)) { $at[$days] = [ordered]@{} }
                # A list per name: two objects can share one in different OUs (the two L8-TEST-DUPs, November 27).
                if (-not $at[$days].Contains($name)) { $at[$days][$name] = New-Object System.Collections.Generic.List[object] }
                $at[$days][$name].Add($entry)
            }
        }
        elseif ($check -eq 'Computer objects, as AD has them' -and $detail -match '^([\d,]+) in (\S+?):') { $objects = [int]($Matches[1] -replace ','); $domain = $Matches[2] }
        elseif ($check -eq 'Device records, as the site has them' -and $detail -match '^([\d,]+)') { $records = [int]($Matches[1] -replace ',') }
    }
    [pscustomobject]@{ Version = $version; At = $at; Objects = $objects; Records = $records; Domain = $domain }
}

function Get-ADDaysSetting {
    $value = (Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Layer8' -Name ADNotSeenDays -ErrorAction SilentlyContinue).ADNotSeenDays
    if ($null -eq $value) { 90 } else { [int]$value }
}

# Layer8 Settings > AD Cleanup Report > days, through the page as a person sets it, checked in the registry after.
function Set-ADDays([int]$Days) {
    $row = Select-Collection
    $null = Open-Layer8Item 'Layer8 Settings' $row
    $sheet = $U::WaitWindow('Window', 'Layer8 Settings', 10000)
    if (-not $sheet) { throw "Layer8 Settings didn't open" }
    try {
        $sheet.Find('TabItem', 'AD Cleanup Report', 3000).Select()
        $box = $sheet.Find('Edit', 'Not seen by AD or by the client in (days):', 3000)
        if (-not $box) { throw 'the AD Cleanup Report page has no days box' }
        $box.SetValue([string]$Days)
        Start-Sleep -Milliseconds 300
        $sheet.Find('Button', 'OK', 2000).Invoke()
    }
    catch {
        $cancel = $sheet.Find('Button', 'Cancel', 0)
        if ($cancel) { $cancel.Invoke() }
        throw
    }
    if (-not (Wait-Until 5000 { -not $U::WaitWindow('Window', 'Layer8 Settings', 0) })) { throw "Layer8 Settings didn't close after OK" }
    $saved = Get-ADDaysSetting
    if ($saved -ne $Days) { throw "Layer8 Settings saved $saved days, not $Days" }
}

# --- Reading AD: read-only, by distinguished name and by SID ---

function Get-DomainPath { [string]([ADSI]'LDAP://RootDSE').Properties['defaultNamingContext'].Value }

# An ADSI path for a distinguished name: / is ADSI's own separator, so it's escaped.
function Get-AdsiPath([string]$DistinguishedName) { 'LDAP://' + $DistinguishedName.Replace('/', '\/') }

function ConvertTo-ADComputer($Entry) {
    if (-not $Entry -or -not $Entry.Properties['distinguishedName'].Value) { return $null }
    $sidBytes = [byte[]]$Entry.Properties['objectSid'].Value
    [pscustomobject]@{
        Name     = [string]$Entry.Properties['name'].Value
        Account  = [string]$Entry.Properties['sAMAccountName'].Value
        DN       = [string]$Entry.Properties['distinguishedName'].Value
        Sid      = (New-Object Security.Principal.SecurityIdentifier($sidBytes, 0)).Value
        Disabled = (([int]$Entry.Properties['userAccountControl'].Value) -band 2) -ne 0
        Created  = [datetime]::SpecifyKind([datetime]$Entry.Properties['whenCreated'].Value, 'Utc').ToLocalTime()
    }
}

function Read-ADByDN([string]$DistinguishedName) {
    try { ConvertTo-ADComputer (New-Object DirectoryServices.DirectoryEntry (Get-AdsiPath $DistinguishedName)) } catch { $null }
}

# Null once the object is gone (deleted): a SID binding only finds live objects.
function Read-ADBySid([string]$Sid) {
    try { ConvertTo-ADComputer (New-Object DirectoryServices.DirectoryEntry "LDAP://<SID=$Sid>") } catch { $null }
}

# The test objects REGRESSION.md section R lists, found by a read-only search under OU=Layer8 Test.
function Find-TestObjects([string]$DomainPath) {
    $ou = "$testOU,$DomainPath"
    if (-not [DirectoryServices.DirectoryEntry]::Exists((Get-AdsiPath $ou))) { return $null }
    $searcher = New-Object DirectoryServices.DirectorySearcher((New-Object DirectoryServices.DirectoryEntry (Get-AdsiPath $ou)), '(&(objectCategory=computer)(sAMAccountName=L8-TEST-*))')
    $searcher.PageSize = 100
    $found = @{}
    foreach ($result in $searcher.FindAll()) {
        $computer = ConvertTo-ADComputer $result.GetDirectoryEntry()
        if ($computer) { $found[$computer.Account.TrimEnd('$')] = $computer }
    }
    $found
}

# A row the runner may select for a fix: an L8-TEST- object under OU=Layer8 Test, as AD itself says it is now.
function Assert-TestRow($Row, [string]$DomainPath) {
    $computer = Read-ADByDN $Row['Location in AD']
    if (-not $computer) { throw "$($Row['Computer']): AD has no object at '$($Row['Location in AD'])', so no fix was started" }
    if ($computer.Name -notlike 'L8-TEST-*' -or $computer.DN -notlike "*,$testOU,$DomainPath") {
        throw "$($computer.Name) ($($computer.DN)) isn't an L8-TEST- object in $testOU, so no fix was started"
    }
    $computer
}

# --- The report window ---

function Open-Report([string]$Name = $Collection, [string]$Route = 'Any') {
    $row = Select-Collection $Name
    $clicked = Get-Date
    $how = Open-Layer8Item $reportTitle $row $Route
    $window = $U::WaitWindow('Window', [regex]::Escape($reportTitle), 15000)
    if (-not $window) { throw "no $reportTitle window within 15 seconds" }
    $status = $window.FindLike('Text', '[\d,]+ computer objects in .*', 60000)
    if (-not $status) { Close-Report $window; throw "no 'n computer objects' status line within a minute" }
    [pscustomobject]@{ Window = $window; List = $window.Find('List', '', 2000); How = $how; Seconds = ((Get-Date) - $clicked).TotalSeconds; Status = $status.Name }
}

function Close-Report($Window) {
    $close = @($Window.FindAll('Button', 'Close')) | Select-Object -Last 1
    if ($close) { $close.Invoke() }
    $null = Wait-Until 3000 { -not $U::WaitWindow('Window', [regex]::Escape($reportTitle), 0) }
    # Focus goes back to the console's main window; let it settle before the next menu opens.
    Start-Sleep -Milliseconds 800
}

# After a fix the report reads again: its status line is the counts once more.
function Wait-ReportRead($Report) {
    $status = Wait-Until 120000 { $s = $Report.Window.FindLike('Text', '[\d,]+ computer objects in .*', 0); if ($s) { $s } }
    if (-not $status) { throw "the report didn't read again within 2 minutes" }
}

function Get-FixButton($Window) { $Window.FindLike('Button', 'Fix \([\d,]+\)\.\.\.', 0) }

# Shows a tile's rows, pressing the tile only when it isn't the one showing: pressing the tile that's showing
# shows them all again, as the dashboard's tiles do (Tile.cs). A tile's selected state isn't exposed yet, so
# "showing" is read from the grid: just that tile's rows (or, for Computers, all of them).
function Show-Tile($Report, [string]$Caption) {
    $tiles = Read-Tiles $Report.Window
    $rows = @(Read-Grid $Report.List)
    if (-not $tiles.Contains($Caption)) { throw "the report has no $Caption tile" }
    $showing = $(if ($Caption -eq 'Computers') { $rows.Count -eq $tiles['Computers'].Count }
                 else { $rows.Count -eq $tiles[$Caption].Count -and $rows.Count -lt $tiles['Computers'].Count -and -not @($rows | Where-Object { $_['Finding'] -ne $Caption }) })
    if (-not $showing) { Push-Tile $Report.Window $Caption }
}

# Each computer on the tile the script lists at those days, with its note where the script gives one.
function Get-DaysText([int]$Days) { if ($Days -eq 1) { '1 day' } else { "$Days days" } }

function Test-ReportRows($Rows, $Tiles, $Expected, [int]$Days) {
    $want = $Expected.At[$Days]
    $at = Get-DaysText $Days
    if (-not $want) { Add-Result SKIP "Tiles at $at" "the regression report has no 'At $at' lines"; return }
    $shownNames = @($Rows | ForEach-Object { $_['Computer'] })
    foreach ($name in $want.Keys) {
        $named = @($Rows | Where-Object { $_['Computer'] -eq $name })
        if (-not $named) { Add-Result FAIL "At $at`: $name" "on the script's list, not in the report"; continue }
        # Each object the script lists under this name gets a row of its own: its tile, and its notes where the
        # script gives them.
        $left = New-Object System.Collections.Generic.List[object]
        foreach ($row in $named) { $left.Add($row) }
        $differ = @(); $matched = @()
        foreach ($entry in $want[$name]) {
            $match = $null
            foreach ($row in $left) { if ($row['Finding'] -ceq $entry.Tile -and (-not $entry.Note -or $row['Notes'] -ceq $entry.Note)) { $match = $row; break } }
            if ($match) { [void]$left.Remove($match); $matched += $match['Finding'] + $(if ($match['Notes']) { " ($($match['Notes']))" }) }
            else { $differ += "no row on '$($entry.Tile)'$(if ($entry.Note) { " with notes '$($entry.Note)'" }) (its rows: $(($named | ForEach-Object { "$($_['Finding']) ($($_['Notes']))" }) -join ', '))" }
        }
        if ($left.Count) { $differ += "$($left.Count) more than the script lists: $(($left | ForEach-Object { $_['Finding'] }) -join ', ')" }
        if ($differ) { Add-Result FAIL "At $at`: $name" ($differ -join '; ') }
        else { Add-Result PASS "At $at`: $name" ($matched -join '; ') }
    }
    foreach ($name in $shownNames | Where-Object { -not $want.Contains($_) }) { Add-Result FAIL "At $at`: $name" "in the report, not on the script's list" }
    $counts = @{}
    foreach ($row in $Rows) { $counts[$row['Finding']] = 1 + [int]$counts[$row['Finding']] }
    $differ = @()
    foreach ($caption in $Tiles.Keys) {
        $expected = $(if ($caption -eq 'Computers') { $Rows.Count } else { [int]$counts[$caption] })
        if ($Tiles[$caption].Count -ne $expected) { $differ += "$caption $($Tiles[$caption].Count) (its rows: $expected)" }
    }
    $tileText = ($Tiles.Keys | ForEach-Object { "$_ $($Tiles[$_].Count)" }) -join ', '
    if ($differ) { Add-Result FAIL "Tiles at $at" ($differ -join '; ') } else { Add-Result PASS "Tiles at $at" $tileText }
}

# The Fix button on the tiles that have rows: there on Stale and Disabled only. A tile with no rows doesn't take a
# press (at 90 days in the lab, Stale and Disabled are empty: the button never showed, November 26), so those are
# checked when they have rows. Then Fix with nothing selected, on a fix tile that has rows: it says to select some.
function Test-FixButtons($Open, [string]$At) {
    $tiles = Read-Tiles $Open.Window
    $with = @(); $without = @(); $wrong = @()
    foreach ($caption in @($tiles.Keys)) {
        if ($caption -ne 'Computers' -and $tiles[$caption].Count -eq 0) { continue }
        Show-Tile $Open $caption
        $button = Wait-Until 1000 { Get-FixButton $Open.Window }
        if ($button) { $with += $caption } else { $without += $caption }
        if ([bool]$button -ne ($caption -in 'Stale', 'Disabled')) { $wrong += $caption }
    }
    $detail = "on: $(if ($with) { $with -join ', ' } else { 'none' }); not on: $($without -join ', ')"
    if ($wrong) { Add-Result FAIL "The Fix button, at $At" "$detail (wrong on: $($wrong -join ', '))" } else { Add-Result PASS "The Fix button, at $At" $detail }
    $empty = @('Stale', 'Disabled' | Where-Object { $tiles.Contains($_) -and $tiles[$_].Count -eq 0 })
    if ($empty) { Add-Result SKIP "The Fix button on $($empty -join ' and '), at $At" 'no rows there to show it on' }
    $fixTile = @('Stale', 'Disabled' | Where-Object { $with -contains $_ }) | Select-Object -First 1
    if ($fixTile) {
        Show-Tile $Open $fixTile
        $button = Wait-Until 1000 { Get-FixButton $Open.Window }
        Test-Equal "Fix on $fixTile with nothing selected reads" 'Fix (0)...' $button.Name
        Push-Button $button
        $message = $U::WaitWindow('Window', 'Fix', 5000)
        if ($message) {
            $text = Get-WindowText $message
            $message.Find('Button', 'OK', 2000).Invoke()
            Test-Contains "Fix on $fixTile with nothing selected says" $text @('Select the computer objects to fix')
        }
        else { Add-Result FAIL "Fix on $fixTile with nothing selected says" 'no Fix message' }
    }
    Show-Tile $Open 'Computers'
}

function Test-ReportStatus([string]$Status, $Expected, [int]$Days) {
    if ($Status -notmatch '^([\d,]+) computer objects in (\S+) and ([\d,]+) device records on the site, read in [^.]+\. Not seen in (\d+) days? counts') {
        Add-Result FAIL 'The status line' "not as expected: $Status"; return
    }
    $differ = @()
    if ($null -ne $Expected.Objects -and [int]($Matches[1] -replace ',') -ne $Expected.Objects) { $differ += "$($Matches[1]) computer objects (the script: $($Expected.Objects))" }
    if ($Expected.Domain -and $Matches[2] -ne $Expected.Domain) { $differ += "domain $($Matches[2]) (the script: $($Expected.Domain))" }
    if ($null -ne $Expected.Records -and [int]($Matches[3] -replace ',') -ne $Expected.Records) { $differ += "$($Matches[3]) device records (the script: $($Expected.Records))" }
    if ([int]$Matches[4] -ne $Days) { $differ += "$($Matches[4]) days (the setting: $Days)" }
    if ($differ) { Add-Result FAIL 'The status line' ($differ -join '; ') } else { Add-Result PASS 'The status line' $Status }
}

# Selects rows by computer name (and, for two objects with one name, by where they are in AD), one at a time.
function Select-ReportRows($Report, [object[]]$Wanted) {
    $rows = @(Read-Grid $Report.List)
    $items = @($Report.List.FindAll('ListItem', ''))
    $picked = @()
    foreach ($spec in $Wanted) {
        $index = -1
        for ($i = 0; $i -lt $rows.Count; $i++) {
            if ($rows[$i]['Computer'] -eq $spec.Name -and (-not $spec.In -or $rows[$i]['Location in AD'] -like "*$($spec.In)*") -and $picked -notcontains $i) { $index = $i; break }
        }
        if ($index -lt 0) { throw "$($spec.Name)$(if ($spec.In) { " in $($spec.In)" }) isn't on this tile" }
        if ($picked.Count -eq 0) { $items[$index].Select() } else { $items[$index].AddToSelection() }
        $picked += $index
    }
    Start-Sleep -Milliseconds 300
    @($picked | ForEach-Object { $rows[$_] })
}

# One window of a fix, by its title: the question (Yes and No), the count box (a text box), or a message (OK alone).
function Wait-FixWindow([string]$Fix, [string]$Kind, [int]$TimeoutMs) {
    Wait-Until $TimeoutMs {
        # While Move to OU's list is open, UI Automation times out reading the console (see Push-Button): not yet.
        try {
            foreach ($w in $U::Windows()) {
                if ($w.Handle -eq $U::MainHandle -or $w.Name -ne $Fix) { continue }
                $hasEdit = [bool]$w.Find('Edit', '', 0)
                $hasYes = [bool]$w.Find('Button', 'Yes', 0)
                $isKind = $(switch ($Kind) { 'Question' { $hasYes } 'Count' { $hasEdit } 'Message' { -not $hasEdit -and -not $hasYes } })
                if ($isKind) { return $w }
            }
        }
        catch {
            # UIA_E_TIMEOUT, as a TimeoutException or a COMException (0x80131505, November 27, 08:34), under
            # PowerShell's MethodInvocationException: keep waiting. Anything else stops the step.
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            if ($inner -is [TimeoutException] -or $inner -is [Runtime.InteropServices.COMException]) { $null } else { throw }
        }
    }
}

function Get-WindowText($Window) { (@($Window.FindAll('Text', '') | ForEach-Object Name) -join ' ').Trim() }

function Test-Open($Window) { try { $null = $Window.Name; $true } catch { $false } }

# One line saying what the tester clicks or types; the runner waits for that window to close.
function Wait-Answer($Window, [string]$Ask, [int]$Minutes = 10) {
    # Closed is told by the window's handle: a closed MessageBox's element still answers, nameless (see Uia.IsOpen).
    $handle = $Window.Handle
    Write-Host ''
    Write-Host "  >>> YOU: $Ask" -ForegroundColor Magenta
    Add-Result INFO 'Asked you' $Ask
    $closed = Wait-Until ($Minutes * 60000) { if ($handle -ne [IntPtr]::Zero) { -not $U::IsOpen($handle) } else { -not (Test-Open $Window) } }
    if ($closed) { return }
    # Not answered: leave AD as it is. No on a question, Cancel on a count box or the OU list.
    $back = $Window.Find('Button', 'No', 0)
    if (-not $back) { $back = $Window.Find('Button', 'Cancel', 0) }
    if ($back) {
        $back.Invoke()
        throw "no answer in $Minutes minutes to: $Ask (pressed $($back.Name) instead, so nothing changed)"
    }
    throw "no answer in $Minutes minutes to: $Ask (found no No or Cancel to press: it's still open, and nothing was pressed)"
}

function Read-FixMessage([string]$Fix) {
    $message = Wait-FixWindow $Fix 'Message' 180000
    if (-not $message) { throw "no '$Fix' message within 3 minutes" }
    $text = Get-WindowText $message
    $message.Find('Button', 'OK', 2000).Invoke()
    $text
}

# Tile, rows, Fix button and menu item, after AD confirms each row is a test object. Returns the rows' AD reads.
function Start-Fix($Report, [string]$Tile, [object[]]$Targets, [string]$Fix, [string]$DomainPath) {
    Show-Tile $Report $Tile
    $rows = Select-ReportRows $Report $Targets
    $computers = @($rows | ForEach-Object { Assert-TestRow $_ $DomainPath })
    $button = Get-FixButton $Report.Window
    if (-not $button) { throw "no Fix button on $Tile" }
    Push-Button $button
    $item = $U::WaitMenuItem("$Fix...", 3000)
    if (-not $item) { throw "the Fix menu has no $Fix..." }
    # Move to OU's list opens inside its menu item's click, and the menu takes no Space: the click goes ahead without
    # waiting, and the list is the tester's (see InvokeNoWait). The other fixes' clicks return at once: their question
    # opens after the second read, outside the click.
    if ($Fix -eq 'Move to OU') { $item.InvokeNoWait(2000) } else { $item.Invoke() }
    $computers
}

function Wait-Question([string]$Fix, [int]$Minutes = 1) {
    $question = Wait-FixWindow $Fix 'Question' ($Minutes * 60000)
    if (-not $question) { throw "no '$Fix' question within $Minutes minute$(if ($Minutes -ne 1) { 's' })" }
    [pscustomobject]@{ Window = $question; Text = (Get-WindowText $question) }
}

# One line for the tester where the runner can't watch the window they answer (Move to OU's list): what comes after it
# is what the runner waits for.
function Write-Ask([string]$Ask) {
    Write-Host ''
    Write-Host "  >>> YOU: $Ask" -ForegroundColor Magenta
    Add-Result INFO 'Asked you' $Ask
}

function Test-Contains([string]$Check, [string]$Text, [string[]]$Parts) {
    $missing = @($Parts | Where-Object { $Text -notlike "*$_*" })
    if ($missing) { Add-Result FAIL $Check "doesn't say: $($missing -join '; '). It says: $Text" } else { Add-Result PASS $Check $Text }
}

function Test-ADCleanup {
    $reportPath = Find-Report '| ADCleanup | PASS | At '
    if (-not $reportPath) { Add-Result FAIL 'The regression report' "none in Build\Regression with ADCleanup results: run tools\Test-Layer8Regression.ps1 first"; return }
    $expected = Read-ADExpected $reportPath
    Test-ReportAge $reportPath $expected.Version
    $days = Get-ADDaysSetting
    if ($days -ne 90) { Add-Result WARN 'Layer8 Settings' "AD Cleanup Report days is $days, not the default 90: the checks use $days" }

    $console = 'Microsoft Configuration Manager.*'
    $U::Allow($console, 'Button', 'Assets and Compliance', 'Invoke')
    $U::Allow($console, 'TreeItem', 'Device Collections', 'Select')
    $U::Allow($console, 'DataItem', '.*', 'Select,Focus')
    $U::Allow($console, 'Group', 'Collection', 'Expand,Collapse')
    $U::Allow('.*', 'MenuBar', 'Layer8', 'Expand,Collapse')
    $U::Allow('.*', 'MenuItem', 'Layer8', 'Expand')
    $U::Allow('.*', 'MenuItem', 'AD Cleanup Report|Layer8 Settings', 'Invoke')
    $U::Allow('Layer8 Settings', 'TabItem', 'AD Cleanup Report', 'Select')
    $U::Allow('Layer8 Settings', 'Edit', 'Not seen by AD or by the client in \(days\):', 'SetValue')
    $U::Allow('Layer8 Settings', 'Button', 'OK|Cancel', 'Invoke')
    $title = [regex]::Escape($reportTitle)
    $U::Allow($title, 'Button', "[^:]+: [\d,]+(, was [\d,]+)?", 'Focus')
    $U::Allow($title, 'Edit', 'Filter:', 'SetValue')
    $U::Allow($title, 'CheckBox', 'Workstations only', 'Toggle')
    $U::Allow($title, 'HeaderItem', '.*', 'Invoke')
    $U::Allow($title, 'ListItem', '.*', 'Select')
    $U::Allow($title, 'Button', 'Copy|Close|Fix \([\d,]+\)\.\.\.', 'Invoke')
    # Fix is pressed by the keyboard (Push-Button): focus, then Space.
    $U::Allow($title, 'Button', 'Fix \([\d,]+\)\.\.\.', 'Focus')
    # The Fix menu's items open a question (or Move to OU's list); nothing changes before its Yes, which is the tester's.
    $U::Allow($title, 'MenuItem', 'Disable\.\.\.|Enable\.\.\.|Move to OU\.\.\.|Delete\.\.\.', 'Invoke')
    $U::Allow('Fix', 'Button', 'OK', 'Invoke')
    $U::Allow('Move to OU', 'Button', 'Cancel', 'Invoke')
    $U::Allow($fixTitles, 'Button', 'No|Cancel|OK', 'Invoke')
    $U::Allow('', 'Key', 'Shift\+F10|Escape|Space', 'Key')
    # Whatever the allowlist says: the Yes that changes AD, and OK where there's a text box (the count to type, the
    # OU list), are the tester's.
    $U::Deny($fixTitles, 'Button', 'Yes', '', 'a fix''s Yes changes Active Directory: that click is yours')
    $U::Deny("$fixTitles|Move to OU", 'Button', 'OK', 'Edit', 'OK on a count to type or on the OU list goes ahead with a fix: that click is yours')

    Close-Menus
    # What a stopped run left open, closed without changing anything: its Fix message (OK), then the report (Close).
    # A fix's question or count box left open is left alone, and the report's Close then fails: that's the tester's.
    $leftover = $U::WaitWindow('Window', 'Fix', 0)
    if ($leftover) { $leftover.Find('Button', 'OK', 2000).Invoke(); Add-Result INFO 'Left open by a stopped run' 'a Fix message: OK pressed' }
    $leftReport = $U::WaitWindow('Window', $title, 0)
    if ($leftReport) { Close-Report $leftReport; Add-Result INFO 'Left open by a stopped run' "the $reportTitle`: closed" }
    $open = $null
    $atOne = $false
    try {
        # Opening it (R): three ways, the same report each time, since it covers the whole domain and site.
        $reads = @()
        foreach ($way in @(@{ Name = $Collection; Route = 'Menu' }, @{ Name = 'Layer8 Dev'; Route = 'Menu' }, @{ Name = $Collection; Route = 'Ribbon' })) {
            try { $opened = Open-Report $way.Name $way.Route }
            catch {
                # The right-click menu opens only with a key on a focused row, and the console's grid can refuse the
                # focus (November 26, 20:46: focus left on the desktop). The ribbon needs no key, so the rest goes on.
                if ($way.Route -ne 'Menu') { throw }
                Add-Result WARN "Opened from $($way.Name) by the right-click menu" "couldn't: $($_.Exception.Message)"
                Close-Menus
                continue
            }
            $rows = @(Read-Grid $opened.List)
            $reads += [pscustomobject]@{ Way = "$($way.Name), $($opened.How)"; Names = (($rows | ForEach-Object { "$($_['Computer']):$($_['Finding'])" }) -join ', ') }
            Add-Result PASS "Opened from $($way.Name)" ("{0}, read in {1:0.0} s: {2} rows" -f $opened.How, $opened.Seconds, $rows.Count)
            if ($way.Name -eq $Collection -and $way.Route -eq 'Ribbon') { $open = $opened } else { Close-Report $opened.Window }
        }
        $different = @($reads | Where-Object { $_.Names -ne $reads[0].Names })
        if ($different) { Add-Result FAIL 'The same report each time' (($reads | ForEach-Object { "$($_.Way): $($_.Names)" }) -join ' | ') }
        else { Add-Result PASS 'The same report each time' $reads[0].Names }

        # Tiles, rows and the status line at the setting's days.
        $rows = @(Read-Grid $open.List)
        $script:adRows = $rows
        Test-ReportRows $rows (Read-Tiles $open.Window) $expected $days
        Test-ReportStatus $open.Status $expected $days

        # A tile filters the grid, with the filter box too; Computers shows them all again.
        $tiles = Read-Tiles $open.Window
        $busy = @($tiles.Keys | Where-Object { $_ -ne 'Computers' -and $tiles[$_].Count -gt 0 }) | Select-Object -First 1
        if ($busy) {
            Show-Tile $open $busy
            $onTile = @(Read-Grid $open.List)
            if ($onTile.Count -eq $tiles[$busy].Count -and -not @($onTile | Where-Object { $_['Finding'] -ne $busy })) { Add-Result PASS "Tile $busy" "$($onTile.Count) rows, all on $busy" }
            else { Add-Result FAIL "Tile $busy" "rows: $(($onTile | ForEach-Object { "$($_['Computer']):$($_['Finding'])" }) -join ', ')" }
            $one = $onTile[0]['Computer']
            Set-Filter $open.Window $one
            $filtered = @(Read-Grid $open.List | ForEach-Object { $_['Computer'] })
            if ($filtered.Count -eq 1 -and $filtered[0] -eq $one) { Add-Result PASS "Tile $busy with '$one' in the filter" 'one row' }
            else { Add-Result FAIL "Tile $busy with '$one' in the filter" "rows: $($filtered -join ', ')" }
            Set-Filter $open.Window ''
            Show-Tile $open 'Computers'
            $all = @(Read-Grid $open.List)
            if ($all.Count -eq $rows.Count) { Add-Result PASS 'Computers shows them all again' "$($all.Count) rows" } else { Add-Result FAIL 'Computers shows them all again' "$($all.Count) of $($rows.Count) rows" }
        }
        Add-Result SKIP "The tiles' rules on hover" 'by hand: a tile''s hover text isn''t exposed to UI Automation yet (the tile accessibility change)'

        # Workstations only: the servers drop out of the grid and the counts, and come back.
        $servers = @($rows | Where-Object { $_['Operating system'] -like '*Server*' } | ForEach-Object { $_['Computer'] })
        $box = $open.Window.Find('CheckBox', 'Workstations only', 2000)
        $box.Toggle()
        Start-Sleep -Milliseconds 700
        $left = @(Read-Grid $open.List | ForEach-Object { $_['Computer'] })
        $computersTile = (Read-Tiles $open.Window)['Computers'].Count
        if (-not @($left | Where-Object { $servers -contains $_ }) -and $left.Count -eq $rows.Count - $servers.Count -and $computersTile -eq $left.Count) {
            Add-Result PASS 'Workstations only' "$($servers -join ' and ') left out; Computers $computersTile"
        }
        else { Add-Result FAIL 'Workstations only' "shown: $($left -join ', '); Computers $computersTile; servers: $($servers -join ', ')" }
        $box.Toggle()
        Start-Sleep -Milliseconds 700
        $back = @(Read-Grid $open.List)
        if ($back.Count -eq $rows.Count) { Add-Result PASS 'Workstations only, unticked' "$($back.Count) rows again" } else { Add-Result FAIL 'Workstations only, unticked' "$($back.Count) of $($rows.Count) rows" }

        # Sorting: by date both ways, Resource ID by number. A column only in view scrolled sideways is a hand check.
        $date = { param($v) $when = [datetime]::MinValue; if ($v -match '^(.+?) \(' -and [datetime]::TryParse($Matches[1], [ref]$when)) { $when.Ticks } else { $null } }
        foreach ($column in 'Last seen in AD', 'Last logon', 'Client last heard from') { Test-SortBothWays $open.List $column $date }
        Test-SortBothWays $open.List 'Resource ID' { param($v) $n = 0; if ([int]::TryParse(($v -replace ','), [ref]$n)) { $n } else { $null } }
        foreach ($click in 1, 2) { if (@($open.List.Columns())[0] -like "*$up") { break }; Invoke-Header $open.List 'Computer' }
        Add-Result SKIP 'A row''s Notes on hover' 'by hand: row hover text isn''t exposed to UI Automation; the Notes column was checked above'

        # Copy: the shown rows, in their sort order. It replaces what's on the clipboard.
        $open.Window.Find('Button', 'Copy', 2000).Invoke()
        Start-Sleep -Milliseconds 500
        $copied = @(Get-Clipboard)
        $order = @(Read-Grid $open.List | ForEach-Object { $_['Computer'] })
        $copiedOrder = @($copied | ForEach-Object { ($_ -split "`t")[0] } | Where-Object { $order -contains $_ })
        if (($copiedOrder -join '|') -ceq ($order -join '|')) { Add-Result PASS 'Copy' "$($copied.Count) lines, the rows in the grid's order" }
        else { Add-Result FAIL 'Copy' "clipboard order: $($copiedOrder -join ', '); grid: $($order -join ', ')" }
        Add-Result SKIP 'Export' 'by hand for now: the runner doesn''t drive the save dialog yet'

        # The Fix button: on Stale and Disabled only; with nothing selected it says to select some.
        Test-FixButtons $open (Get-DaysText $days)

        # The fix steps: with the test objects, and only when the tester is watching to make each AD change by hand.
        $domainPath = Get-DomainPath
        $test = Find-TestObjects $domainPath
        # Shared with each step's script block, which runs in a scope of its own.
        $fixState = @{ Skip = $null; Failed = @{}; Ran = $false }
        if (-not $test) { $fixState.Skip = "no $testOU in AD (read-only search at $(Get-Date -Format 'HH:mm')): the test objects are yours to make (REGRESSION.md section R)" }
        elseif (-not $WithFixes) { $fixState.Skip = 'run with -WithFixes in a window you watch: each Yes, count and Move to OU list is yours' }
        # Each step runs when its tile is in -FixTile and its own test objects are there: after the Disabled-tile steps
        # (the second DUP deleted, OFF moved and enabled) the Stale ones still run the next day (November 27). A
        # failed step stops the later steps on its own tile only.
        $step = {
            param([string]$Name, [string]$Tile, [string[]]$Needs, [scriptblock]$Body)
            if ($fixState.Skip) { Add-Result SKIP $Name $fixState.Skip; return }
            if ($FixTile -notcontains $Tile) { Add-Result SKIP $Name "a $Tile-tile step, and -FixTile is $($FixTile -join ', ')"; return }
            $gone = @($Needs | Where-Object { -not $test.ContainsKey($_) })
            if ($gone) { Add-Result SKIP $Name "needs $($gone -join ', '), not in $testOU now: the test objects are yours to make again (REGRESSION.md section R)"; return }
            if ($fixState.Failed[$Tile]) { Add-Result SKIP $Name "after a failed $Tile-tile step, whose objects it may need"; return }
            try { & $Body; $fixState.Ran = $true }
            catch { Add-Result FAIL $Name $_.Exception.Message; $fixState.Failed[$Tile] = $true; Close-Menus }
        }

        if (-not $fixState.Skip) {
            Write-Host ''
            Write-Host '  >>> YOU: the fix steps start. Do only what each >>> line asks; the runner presses No and each summary''s OK.' -ForegroundColor Magenta
        }
        & $step 'Move to OU, three ways at once' 'Disabled' @('L8-TEST-OFF', 'L8-TEST-PROT', 'L8-TEST-DUP2') {
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-OFF' }, @{ Name = 'L8-TEST-PROT' }, @{ Name = 'L8-TEST-DUP'; In = 'OU=Disabled' }) 'Move to OU' $domainPath
            # The list can't be read while it's open (see Start-Fix), so what it offers is the tester's to check.
            Write-Ask "in Move to OU, check the list offers Computers, Layer8 Test and Layer8 Test / Disabled, and no Domain Controllers. Pick Layer8 Test and click OK"
            $question = Wait-Question 'Move to OU' 10
            Test-Contains 'Move to OU, three ways: the question' $question.Text @('already in that OU', 'protected from accidental deletion', 'L8-TEST-DUP', 'DC01')
            Wait-Answer $question.Window "click Yes in the 'Move to OU' question (it moves L8-TEST-DUP from OU=Disabled to Layer8 Test)"
            Test-Contains 'Move to OU, three ways: the summary' (Read-FixMessage 'Move to OU') @('already has an object with its name')
            $dup = Read-ADBySid $test['L8-TEST-DUP2'].Sid
            if ($dup -and $dup.DN -like '*,OU=Disabled,*') { Add-Result PASS 'Move to OU, three ways: in AD' "L8-TEST-DUP2 stays in $($dup.DN)" } else { Add-Result FAIL 'Move to OU, three ways: in AD' "L8-TEST-DUP2: $($dup.DN)" }
            Wait-ReportRead $open
        }
        & $step 'Move to OU' 'Disabled' @('L8-TEST-OFF', 'L8-TEST-PROT') {
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-OFF' }, @{ Name = 'L8-TEST-PROT' }) 'Move to OU' $domainPath
            Write-Ask "in Move to OU, pick Layer8 Test / Disabled and click OK"
            $question = Wait-Question 'Move to OU' 10
            Test-Contains 'Move to OU: the question' $question.Text @('L8-TEST-OFF', 'protected from accidental deletion')
            Wait-Answer $question.Window "click Yes in the 'Move to OU' question (it moves L8-TEST-OFF to Layer8 Test / Disabled)"
            Add-Result INFO 'Move to OU: the summary' (Read-FixMessage 'Move to OU')
            $off = Read-ADBySid $test['L8-TEST-OFF'].Sid
            if ($off -and $off.DN -like "CN=L8-TEST-OFF,OU=Disabled,$testOU,*") { Add-Result PASS 'Move to OU: in AD' $off.DN } else { Add-Result FAIL 'Move to OU: in AD' "L8-TEST-OFF: $($off.DN)" }
            $offered = (Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Layer8' -Name ADMoveTo -ErrorAction SilentlyContinue).ADMoveTo
            Test-Equal 'Move to OU: offered first next time' "OU=Disabled,$testOU,$domainPath" $offered
            Wait-ReportRead $open
        }
        & $step 'Enable' 'Disabled' @('L8-TEST-OFF') {
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-OFF' }) 'Enable' $domainPath
            $question = Wait-Question 'Enable'
            Add-Result INFO 'Enable: the question' $question.Text
            Wait-Answer $question.Window "click Yes in the 'Enable' question (it enables L8-TEST-OFF)"
            Add-Result INFO 'Enable: the summary' (Read-FixMessage 'Enable')
            $off = Read-ADBySid $test['L8-TEST-OFF'].Sid
            if ($off -and -not $off.Disabled) { Add-Result PASS 'Enable: in AD' 'L8-TEST-OFF is enabled' } else { Add-Result FAIL 'Enable: in AD' "L8-TEST-OFF disabled: $($off.Disabled)" }
            Wait-ReportRead $open
            # The report reads again on the Disabled tile, which OFF has just left (read as '', November 27, 09:13).
            Show-Tile $open 'Computers'
            $now = @(Read-Grid $open.List | Where-Object { $_['Computer'] -eq 'L8-TEST-OFF' } | ForEach-Object { $_['Finding'] })
            Test-Equal 'Enable: L8-TEST-OFF is now on' 'No client' ($now -join ', ')
        }
        & $step 'Delete, and its question' 'Disabled' @('L8-TEST-PROT', 'L8-TEST-DUP2') {
            $dupSid = $test['L8-TEST-DUP2'].Sid
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-PROT' }, @{ Name = 'L8-TEST-DUP'; In = 'OU=Disabled' }) 'Delete' $domainPath
            $question = Wait-Question 'Delete'
            Test-Contains 'Delete: the question' $question.Text @('protected from accidental deletion', 'Recycle Bin', '180 days')
            $question.Window.Find('Button', 'No', 2000).Invoke()
            Start-Sleep -Milliseconds 700
            if (Read-ADBySid $dupSid) { Add-Result PASS 'Delete: No changes nothing' 'L8-TEST-DUP2 is still in AD' } else { Add-Result FAIL 'Delete: No changes nothing' 'L8-TEST-DUP2 is gone' }
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-PROT' }, @{ Name = 'L8-TEST-DUP'; In = 'OU=Disabled' }) 'Delete' $domainPath
            Wait-Answer (Wait-Question 'Delete').Window "click Yes in the 'Delete' question"
            $count = Wait-FixWindow 'Delete' 'Count' 30000
            if (-not $count) { throw 'no count to type after Yes' }
            Wait-Answer $count "type 2 (the wrong count, on purpose) in the 'Delete' box and click OK"
            Test-Contains 'Delete: the wrong count' (Read-FixMessage 'Delete') @("That wasn't 1")
            if (Read-ADBySid $dupSid) { Add-Result PASS 'Delete: the wrong count changes nothing' 'L8-TEST-DUP2 is still in AD' } else { Add-Result FAIL 'Delete: the wrong count changes nothing' 'L8-TEST-DUP2 is gone' }
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-PROT' }, @{ Name = 'L8-TEST-DUP'; In = 'OU=Disabled' }) 'Delete' $domainPath
            Wait-Answer (Wait-Question 'Delete').Window "click Yes in the 'Delete' question"
            $count = Wait-FixWindow 'Delete' 'Count' 30000
            if (-not $count) { throw 'no count to type after Yes' }
            Wait-Answer $count "type 1 in the 'Delete' box and click OK (it deletes the L8-TEST-DUP in OU=Disabled)"
            Add-Result INFO 'Delete: the summary' (Read-FixMessage 'Delete')
            if (-not (Read-ADBySid $dupSid)) { Add-Result PASS 'Delete: in AD' 'L8-TEST-DUP2 is gone' } else { Add-Result FAIL 'Delete: in AD' 'L8-TEST-DUP2 is still there' }
            Wait-ReportRead $open
        }

        # At 1 day: the tiles again, the objects no fix touches, and the steps that need objects a day old.
        Close-Report $open.Window
        $open = $null
        Set-ADDays 1
        $atOne = $true
        $open = Open-Report $Collection 'Ribbon'
        $rows = @(Read-Grid $open.List)
        # The regression report was read before the fix steps changed AD (OFF enabled, the second DUP deleted), so
        # after them its 1-day tiles and counts no longer apply: its next run, and the -Since run, check AD itself.
        if ($fixState.Ran) {
            Add-Result SKIP 'Tiles at 1 day' 'the fix steps changed AD after the regression report was read: its next run checks these'
            Add-Result SKIP 'The status line at 1 day' 'its counts are from before the fix steps'
        }
        else {
            Test-ReportRows $rows (Read-Tiles $open.Window) $expected 1
            Test-ReportStatus $open.Status $expected 1
        }
        Add-Result SKIP 'The tiles'' rules say "1 day"' 'by hand: a tile''s hover text isn''t exposed to UI Automation yet (the tile accessibility change)'
        Test-FixButtons $open '1 day'
        $untouchable = @($rows | Where-Object { $_['Finding'] -eq 'Stale' -and $_['Computer'] -in 'DC01', 'CM01' } | ForEach-Object { @{ Name = $_['Computer'] } })
        if ($untouchable.Count -eq 2) {
            Show-Tile $open 'Stale'
            $null = Select-ReportRows $open $untouchable
            Test-Equal 'Never touched: DC01 and CM01 selected, Fix reads' 'Fix (0)...' (Get-FixButton $open.Window).Name
            Push-Button (Get-FixButton $open.Window)
            $message = $U::WaitWindow('Window', 'Fix', 5000)
            if ($message) {
                $text = Get-WindowText $message
                $message.Find('Button', 'OK', 2000).Invoke()
                Test-Contains 'Never touched: Fix says' $text @('None of the selected computer objects can take a fix', '1 domain controller', '1 with a site system role')
            }
            else { Add-Result FAIL 'Never touched: Fix says' 'no Fix message' }
            Show-Tile $open 'Computers'
        }
        else {
            $where = ($rows | Where-Object { $_['Computer'] -in 'DC01', 'CM01' } | ForEach-Object { "$($_['Computer']) on $($_['Finding'])" }) -join ', '
            Add-Result SKIP 'Never touched' "needs DC01 and CM01 on Stale at 1 day; today: $where"
        }

        $ready = $null
        if (-not $fixState.Skip -and $FixTile -contains 'Stale' -and $test -and $test.ContainsKey('L8-TEST-STALE1') -and $test.ContainsKey('L8-TEST-STALE2')) {
            $ready = @('L8-TEST-STALE1', 'L8-TEST-STALE2' | ForEach-Object { $test[$_].Created.AddHours(24) }) | Sort-Object -Descending | Select-Object -First 1
        }
        if ($ready -and $ready -gt (Get-Date)) { $fixState.Skip = "L8-TEST-STALE1 and -STALE2 are less than a day old: runnable from $($ready.ToString('g'))" }
        & $step 'Disable' 'Stale' @('L8-TEST-STALE1', 'L8-TEST-STALE2') {
            $null = Start-Fix $open 'Stale' @(@{ Name = 'L8-TEST-STALE1' }, @{ Name = 'L8-TEST-STALE2' }) 'Disable' $domainPath
            Wait-Answer (Wait-Question 'Disable').Window "click Yes in the 'Disable' question (it disables L8-TEST-STALE1 and -STALE2)"
            $count = Wait-FixWindow 'Disable' 'Count' 30000
            if (-not $count) { throw 'no count to type after Yes' }
            Wait-Answer $count "type 2 in the 'Disable' box and click OK"
            Add-Result INFO 'Disable: the summary' (Read-FixMessage 'Disable')
            $both = @('L8-TEST-STALE1', 'L8-TEST-STALE2' | ForEach-Object { Read-ADBySid $test[$_].Sid } | Where-Object { $_ -and $_.Disabled })
            Test-Equal 'Disable: in AD, disabled' 2 $both.Count
            Wait-ReportRead $open
        }
        & $step 'Delete two' 'Stale' @('L8-TEST-STALE1', 'L8-TEST-STALE2') {
            $sids = @('L8-TEST-STALE1', 'L8-TEST-STALE2' | ForEach-Object { $test[$_].Sid })
            $null = Start-Fix $open 'Disabled' @(@{ Name = 'L8-TEST-STALE1' }, @{ Name = 'L8-TEST-STALE2' }) 'Delete' $domainPath
            Wait-Answer (Wait-Question 'Delete').Window "click Yes in the 'Delete' question (it deletes L8-TEST-STALE1 and -STALE2)"
            $count = Wait-FixWindow 'Delete' 'Count' 30000
            if (-not $count) { throw 'no count to type after Yes' }
            Wait-Answer $count "type 2 in the 'Delete' box and click OK"
            Add-Result INFO 'Delete two: the summary' (Read-FixMessage 'Delete')
            $left = @($sids | ForEach-Object { Read-ADBySid $_ } | Where-Object { $_ })
            Test-Equal 'Delete two: left in AD' 0 $left.Count
            Wait-ReportRead $open
        }
        Add-Result INFO 'The log' "the -Since run checks each fix Layer8 logged against AD"
    }
    finally {
        if ($open) { try { Close-Report $open.Window } catch { } }
        Close-Menus
        if ($atOne -or (Get-ADDaysSetting) -ne $days) {
            try { Set-ADDays $days; Add-Result PASS 'Layer8 Settings back to' "$days days" }
            catch {
                # Through the registry Layer8 reads it from, so a failed step never leaves the setting at 1 day.
                Set-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Layer8' -Name ADNotSeenDays -Value $days -Type DWord
                Add-Result WARN 'Layer8 Settings back to' "$days days, written to the registry: the settings page failed ($($_.Exception.Message))"
            }
        }
    }
}

# --- Report --------------------------------------------------------------------------------------------------------

function Write-Report {
    $folder = Join-Path $repo 'Build\Regression'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $version = $script:loaded
    if (-not $version) { $version = 'unknown' }
    $file = Join-Path $folder ('Layer8-{0}-{1:yyyyMMdd-HHmmss}-console.md' -f $version, $started)
    $cell = { param($text) ([string]$text).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Layer8 $version console steps")
    $lines.Add('')
    $lines.Add(("{0:yyyy-MM-dd HH:mm} on {1}, through UI Automation. Commit {2} on {3}." -f $started, $env:COMPUTERNAME, (& git -C $repo rev-parse --short HEAD), (& git -C $repo rev-parse --abbrev-ref HEAD)))
    $lines.Add('')
    $counts = ($results | Group-Object Result | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    $lines.Add(("Results: {0}, in {1:0} s." -f $counts, ((Get-Date) - $started).TotalSeconds))
    $lines.Add('')
    $lines.Add('| Section | Result | Check | Detail |')
    $lines.Add('|---|---|---|---|')
    foreach ($result in $results) { $lines.Add(('| {0} | {1} | {2} | {3} |' -f $result.Section, $result.Result, (& $cell $result.Check), (& $cell $result.Detail))) }
    if ($script:dashboardRows) {
        $columns = @($script:dashboardRows[0].Keys)
        $lines.Add('')
        $lines.Add("## What the Health Dashboard showed on $Collection")
        $lines.Add('')
        $lines.Add('| ' + ($columns -join ' | ') + ' |')
        $lines.Add('|' + (($columns | ForEach-Object { '---' }) -join '|') + '|')
        foreach ($row in $script:dashboardRows) { $lines.Add('| ' + (($columns | ForEach-Object { & $cell $row[$_] }) -join ' | ') + ' |') }
    }
    if ($script:adRows) {
        $columns = @($script:adRows[0].Keys)
        $lines.Add('')
        $lines.Add("## What the AD Cleanup Report showed")
        $lines.Add('')
        $lines.Add('| ' + ($columns -join ' | ') + ' |')
        $lines.Add('|' + (($columns | ForEach-Object { '---' }) -join '|') + '|')
        foreach ($row in $script:adRows) { $lines.Add('| ' + (($columns | ForEach-Object { & $cell $row[$_] }) -join ' | ') + ' |') }
    }
    [IO.File]::WriteAllLines($file, $lines, (New-Object Text.UTF8Encoding($false)))
    $file
}

# --- Run -----------------------------------------------------------------------------------------------------------

Write-Host "Layer8 console steps through UI Automation, $($started.ToString('g'))" -ForegroundColor Cyan
# Not $report: PowerShell names ignore case, so that's the -Report parameter.
$reportFile = $null
$loaded = $null
try {
    Write-Host ''
    Write-Host 'Start' -ForegroundColor Cyan
    $ready = $false
    try {
        $interop = Get-Interop
        Add-Type -Path $interop
        Add-Type -Path (Join-Path $PSScriptRoot 'Layer8Uia.cs') -ReferencedAssemblies $interop, 'System.Core'
        $U = [Layer8.Testing.Uia]
        $console = @(Get-Process -Name Microsoft.ConfigurationManagement -ErrorAction SilentlyContinue)
        if ($console.Count -ne 1) { throw "expected one ConfigMgr console open, found $($console.Count)" }
        if (-not [int]$console[0].MainWindowHandle) { throw 'the console has no main window yet (still starting?)' }
        $main = $U::Attach($console[0].Id, $console[0].MainWindowHandle)
        $loaded = Get-LoadedVersion
        Add-Result INFO 'Console' "PID $($console[0].Id), Layer8 $loaded loaded (Layer8.log)"
        if (-not $U::ConsoleInFront() -and -not $U::BringToFront()) { throw "the console couldn't be brought to the front" }
        $ready = $true
    }
    catch { Add-Result FAIL 'Setup' $_.Exception.Message }
    foreach ($name in 'Dashboard', 'ADCleanup') {
        $section = $name
        Write-Host ''
        Write-Host $name -ForegroundColor Cyan
        if ($Skip -contains $name) { Add-Result SKIP $name 'skipped'; continue }
        if (-not $ready) { Add-Result SKIP $name 'setup failed'; continue }
        # Each section from a console in front and not minimized.
        if (-not $U::ConsoleInFront() -and -not $U::BringToFront()) { Add-Result FAIL $name "the console couldn't be brought to the front"; continue }
        try { & "Test-$name" }
        catch { Add-Result FAIL 'The section stopped' ("line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) }
        finally { try { Close-Menus } catch { } }
    }
}
finally {
    $section = 'Run'
    $reportFile = Write-Report
}

$failures = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ''
Write-Host ("{0}, in {1:0} s" -f (($results | Group-Object Result | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '), ((Get-Date) - $started).TotalSeconds) -ForegroundColor Cyan
Write-Host "Report: $reportFile"
exit $failures
