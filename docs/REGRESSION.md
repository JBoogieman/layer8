# Layer8 regression run

Every test build re-runs the old tests, not only the new one: everything in [STATUS.md](STATUS.md)'s tested table that the change could touch, and a Health Dashboard run on All Systems every time. This is that list. `tools\Test-Layer8Regression.ps1` checks what doesn't need the console (the build, the cab, the installed copy, Customize-Layer8Cab.ps1, Error Lookup's text, the signature checks, what each device should show on the dashboard, and what Online Status, Quick Status, Client Info, Check Pending Restart, BitLocker Status, Defender Status and List Missing Updates answer on each device, run in 32-bit PowerShell as the console runs them); the steps below are the rest.

Written for the lab STATUS.md describes: a site server, the client the console runs on, a second client, the Layer8 Dev collection, and the test data from `tools\New-Layer8LabTestData.ps1`.

**Changes something** marks a step that changes a device, the site or Active Directory. Never screenshot or copy a BitLocker recovery key or a LAPS password, here or anywhere.

## Every build

1. Build the test build and install it in the console ([INSTALL.md](INSTALL.md)): bump the fourth number in `SharedAssemblyInfo.cs`, run `tools\Build-Layer8Msi.ps1`, then `tools\Build-Layer8Extension.ps1` with your signing certificate, and import, approve and install the cab. Installing restarts the console, so step 4's run is the first one after a restart: the case the step timing row is about.
2. Run the script:

   ```powershell
   .\tools\Test-Layer8Regression.ps1
   ```

   - Every FAIL is understood before going on.
   - Its **Changes** section names the sections below that this change touches: the files changed since the last commit that changed STATUS.md, matched against each section's Files line.
   - Its **Devices** section says what the dashboard should show on All Systems.
   - Its last line is the command for step 5.
3. Run the sections the change touches, and any others you know it touches.
4. Run the Health Dashboard on All Systems, with Layer8 Settings at their defaults, as the last dashboard run:
   - [ ] It finishes in seconds and skips the placeholder records by name.
   - [ ] The tiles, and each device's Issues, are what the script's Devices section lists.
   - [ ] The Windows, Client, BitLocker, Blue screen and C: free columns read what the script lists. Up and Policy request move on with the clock.
5. Run the script again with the time the first run printed:

   ```powershell
   .\tools\Test-Layer8Regression.ps1 -Since '2026-11-20 19:25:00'
   ```

   It checks the console loaded this build, every error, warning and slow step Layer8 logged since then, the last dashboard run on All Systems against its own reading of the devices, and Layer8 Settings back at their defaults.
6. Record the results in STATUS.md before the commit. Each run's report is in `Build\Regression`.

## When the change touches it

### A. Health Dashboard

Files: `Layer8.Device/HealthDashboard.cs`, `Layer8.Device/HealthCheck.cs`, `Layer8.Device/HealthRun.cs`, `Layer8/ListViewSorter.cs`, `Extensions/Actions/*/Layer8Devices.xml`, `Extensions/Actions/*/Layer8Collections.xml`

- [ ] **Layer8 Dev:** both clients checked in a few seconds. BitLocker reads as BitLocker Status reads it ("Suspended (encrypted)" when not protected but fully encrypted, "Off" when fully decrypted).
- [ ] **All Systems:** the placeholders are skipped by name. The site server shows Client "Not installed" (its management point runs CcmExec without a client), BitLocker "Not available" and not counted, and Updates "No client".
- [ ] **Devices selected under Devices** (the Layer8 menu): the same checks for just those.
- [ ] **Tiles:**
  - Clicking a tile filters the grid, and it works with the filter box: BitLocker off and the second client's name give one row.
  - Devices shows them all again.
  - Each tile shows its rule on hover.
- [ ] **Sorting:** C: free sorts by size both ways. Policy request sorts newest or oldest first. Issues, most first, puts devices with issues ahead of the placeholders.
- [ ] **Policy request:** matches the times the console's Devices view shows, and stays blank for the site server and the placeholders.
- [ ] **An offline client** (shut down DEV02 first, and note when):
  - Status reads "Offline, last seen ...", matching when it went down, and the row is grey.
  - Signed in, Restart, Client and Windows show ConfigMgr's last values. C: free shows the last hardware inventory's value and its age. Up, BitLocker, Defender, Blue screen and the update columns stay blank.
  - Hovering the row gives the offline reason, when ConfigMgr last heard from it, and where each grey value came from. The hardware inventory time matches when the last Hardware Inventory Cycle ran on it.
  - The tiles count it under Offline only: no Low disk or Restart from the grey values.
- [ ] **Blue screen:** a dump inside the window shows its time and stop code, as the script lists it.
- [ ] **Windows:** reads version and build ("26H2, 26300.x"), and the site server "Server 2025, 26100.x".
- [ ] **Update scan:** reads how long ago each client's last software update scan finished, as the script lists it, and "No client" for the site server. It sorts oldest or newest first.
- [ ] **Since last run:** change a threshold so one device gains an issue (Not scanning set between the two clients' last scans), and run the dashboard again in a new window.
  - The Changed tile counts that device, and its Since last run cell says "New: <issue>".
  - That tile shows "+1" beside its count once the run ends, and its hover text gives the last run's count and time.
  - The status line says how many changed since the run at what time.
  - Refresh straight after shows nothing changed. Set the threshold back, and the next run says "Gone: <issue>" and "-1".
  - Check Again on one device logs what changed but isn't kept. With no earlier run of a collection, Changed's hover text says so.
- [ ] **Double-click a row:** System Information opens with the same uptime, free space and client version.
- [ ] **Check Again:** re-checks just the selected device.
- [ ] **Export:** writes the shown rows in their sort order, UTF-8 with a byte order mark.
- [ ] **Client Actions from the right-click menu:**
  - Machine Policy Retrieval and Evaluation Cycle on the console's computer completes.
  - Client Info on both clients, with the second one switched off, answers for one, reports the other offline, and offers Run Again on Failed.
  - Restart Computer asks the console menu's question ("Restart <name> in 5 minutes?"), and No runs nothing.
  - Find Software asks for its search first.
  - With only a placeholder record selected, Client Actions is greyed out.
- [ ] **Changes something:** with the second client switched off, Offline counts it and its Issues cell says Offline. The long placeholder names don't widen the Device column, and hovering the row shows the full offline reason.
- [ ] **Step timing:** with Layer8 Settings > General > Log level at Debug, every check on every device logs its time. The script's `-Since` run lists any step over 10 seconds, which is logged at any level.

### B. Layer8 Settings

Files: `Layer8/Layer8Settings.cs`, `Layer8/OptionsControl*`, `Layer8/HealthDashboardOptionsControl.cs`, `Extensions/Forms/Layer8Settings.xml`, `Extensions/Actions/*/Layer8Devices.xml`, `Extensions/Actions/*/Layer8Collections.xml`

Open it from a collection's Client Actions menu. Every change below is set back afterwards; the script's `-Since` run warns about one left changed.

- [ ] **Tabs:** General and Health Dashboard. Health Dashboard shows the defaults (10 GB, 14 days, 7 days, 14 days, 14 days, build 26100), and Apply lights up after a change.
- [ ] **Long uptime:** set below the console's computer's uptime, the next dashboard counts that computer under Long uptime and not the second client. The page shows the new number when opened again. Set back to 14, the tile goes back to 0.
- [ ] **Not reporting:** 3 saves and shows 3 when opened again. Set back to 7.
- [ ] **Blue screens:** 30 saves, and the tile's hover text says "in the last 30 days". Set back to 14.
- [ ] **Not scanning:** set to a number of days between the two clients' last scans (the script's Update scan column), and the next dashboard counts only the client that scanned longer ago. The tile's hover text shows the number. Set back to 14.
- [ ] **Windows build:** one revision above the clients' build gives Windows behind on both clients, and the site server isn't counted. Set back to 26100.
- [ ] **A build of "26300.":** the console's red mark appears beside the box. OK gives the console's own error with the help text and keeps the page open, not "The given key was not present in the dictionary".
- [ ] **The log:** each save is logged with its values. The script's `-Since` run lists the "settings saved" lines.
- [ ] **General:** a change is picked up by the next run without restarting the console.

### C. Client actions and client tools

Files: `Layer8.Device/ClientActions.cs`, `Layer8.Device/ClientActionProgressBar.cs`, `Layer8.Device/ResultClientHealthControl*`, `Layer8.Device/ResultSoftwareUpdatesControl*`, `Layer8.Device/ResultSUComplianceControl*`, `Extensions/Actions/*/Layer8Devices.xml`, `Extensions/Actions/*/Layer8Collections.xml`

- [ ] **Evaluation cycles:** Machine Policy and App Deployment Evaluation fire on both clients.
- [ ] **List Missing Updates on a client that has scanned:** lists the deployed and the needed updates, not "No update scan yet".
- [ ] **Tools on both clients:** Quick Status, Find Software, List Missing Updates, Check Pending Restart, policy and Group Policy refresh, Client Health Check, Client Info, BitLocker Status and Defender Status all complete. On the site server they say "No ConfigMgr client found".
- [ ] **Against the script:** Client Info's version, Check Pending Restart and List Missing Updates agree with the script's Client, Restart and Updates columns. An up-to-date client says "No missing updates".
- [ ] **Find Software:** finds the 64-bit Teams Meeting Add-in on the second client.
- [ ] **Changes something:** Defender Quick Scan starts a scan.
- [ ] **Changes something:** Repair Client on the second client. ccmrepair.log ends "Repair complete" after several minutes.
- [ ] **Changes something:** Restart, then Cancel Restart, on the second client. It doesn't reboot, and its uptime keeps counting.
- [ ] **Changes something:** Restart SMS Agent Host Service on the second client.
- [ ] **A second admin account, not run as administrator:** Machine Policy on the console's own computer warns that it needs Run as administrator, not "Access denied" with a stack trace. On a two-client collection, the other client completes.

### D. Collection runs, Online Status and Quick Status columns

Files: `Layer8.Device/DeviceProgressDialog*`, `Layer8.Device/DeviceColumns.cs`, `Layer8/ListViewSorter.cs`, `Layer8.Device/RemoteTools.cs`, `Layer8.Device/Device.cs`, `Extensions/Actions/*/Layer8Collections.xml`

- [ ] **A client tool on All Systems and on Layer8 Dev:** the four placeholder records are skipped by name and counted as Skipped.
- [ ] **Online Status:** on both clients and the site server it reads what the script's Online line says. The site server: "ping blocked, port 445 answers".
- [ ] **Changes something:** with the second client switched off, Online Status says "no ping reply, port 445 closed", and Quick Status says "ports 135 and 445 don't answer".
- [ ] **WMI blocked:** with its WMI firewall rules off, the second client reads "Online, but WMI is blocked (RPC server unavailable)". That's a firewall change, so it's yours to make and undo.
- [ ] **Quick Status on All Systems:**
  - The Signed in, Up, C: free, C: size, IP address and Client columns are all there.
  - C: free sorts as sizes, not as text, and Up sorts both ways with blanks last. Ties go by name, and there's one sort arrow at a time.
  - Export writes every column in the sorted order.

### E. Remote tools and Send Message

Files: `Layer8.Device/RemoteTools.cs`, `Layer8.Device/InputPrompt.cs`, `Extensions/Actions/*/Layer8Devices.xml`

- [ ] **Both clients:** Ping, Browse C$, Browse ccmcache, Client Logs (the folder, and Collect Client Logs' zip), Client Log in CMTrace, Computer Management, Event Viewer (the client's logs, not the console's) and Services.
- [ ] **Remote Desktop:** to the console's own computer.
- [ ] **Remote Assistance and PowerShell Session:** they start. Their UAC prompt and the session itself are still on STATUS.md's untested list.
- [ ] **Send Message, nobody signed in:** to the second client it says "Nobody signed in; not sent".
- [ ] **Changes something:** Send Message to the console's computer says "Sent to <account>", and the pop-up shows.
- [ ] **A 258-character message:** refused with the 255 limit, and the prompt keeps the text.

### F. Copy and export

Files: `Layer8.Device/Device.cs`, `Extensions/Actions/*/Layer8Devices.xml`, `Extensions/Actions/*/Layer8Collections.xml`

- [ ] **Copy Names, Copy Device Info, Copy Results and Export:** on a client, they give what the window shows.
- [ ] **Copy Member Names:** on All Systems, the same.

### G. BitLocker Recovery Key and LAPS Password

Files: `Layer8.Device/BitLockerKeys.cs`, `Layer8.Device/LAPSDialog.cs`, `Extensions/Actions/*/Layer8Devices.xml`

Check that the fields are there. Don't copy or screenshot the key or the password.

- [ ] **No keys:** BitLocker Recovery Key on a device with none says so.
- [ ] **The fake key:** with the lab script's fake key (adding it **changes Active Directory**), the key is found, and the Key ID filter finds it by 4C415938.
- [ ] **LAPS Password:** on the second client it decrypts and shows the account, when the password changed and when it expires.
- [ ] **Changes something** (Active Directory, and the client's password): Expire Now. The dialog says the expiry is set, and Layer8's Group Policy Update on the client has it set a new password straight away.

### H. Install Application

Files: `Layer8.Device/ApplicationInstall.cs`, `Extensions/Actions/*/Layer8Devices.xml`

- [ ] **Changes something:** Install Application on both clients installs Layer8 Test App, which reaches Installed.
- [ ] **The failing app:** it shows 1603 (0x643) in the list, and again under it when selected.
- [ ] **Changes something** (the site): with 15 apps deployed to the second client (`New-Layer8LabTestData.ps1 -LongList`), the window opens and settles.
  - Maximize, restore, and resizing bigger and smaller leave no horizontal scrollbar.
  - Selecting the failed app shows its 1603 in the status line.
  - Refresh reloads.

### I. Client cache

Files: `Layer8.Device/ClientCache.cs`, `Layer8.Device/ResultCacheControl*`, `Layer8/ListViewSorter.cs`, `Extensions/Forms/Layer8DeviceCache.xml`

- [ ] **Changes something:** Clear Client Cache on both clients. On the second client it frees the test app's content.
- [ ] **Client Cache:** opens and cancels.
- [ ] **Client Cache sorting** (issue #3): Size sorts by size both ways (3 KB below 8 MB below 670 MB, whatever the decimal separator), and Last Used by date, not by day of the month. The arrow follows the sorted column.

### J. Deployments: Retry, Copy Failed, Delete, Redistribute

Files: `Layer8.Device/DeploymentRetry.cs`, `Layer8.Device/ContentRedistribute.cs`, `Layer8.Device/ErrorCodes.cs`, `Extensions/Actions/d1621955-*/*`, `Extensions/Actions/14214306-*/*`, `Extensions/Actions/*/Layer8*Msg.xml`

The test data comes from `tools\New-Layer8LabTestData.ps1`. Retry uses summarized status, so run Summarization first.

- [ ] **Copy Failed Devices:** on Layer8 Fail App it groups devices by error code ("2 x 1603 (0x643): Fatal error during installation", one per client in Error).
- [ ] **Changes something:** Retry Failed Devices on the Required and the Available app deployment, with a client in Error.
- [ ] **Changes something** (the site): Monitoring > Deployments > Delete on a throwaway app deployment.
- [ ] **Changes something** (the site): Redistribute Failed Content on Layer8 Broken Package. It stays in Error, because its source share doesn't exist.

### K. Collection membership

Files: `Layer8/AddResourcesListDialog*`, `Layer8/RemoveResourcesListDialog*`, `Extensions/Forms/Layer8AddResourcesListDialog.xml`, `Extensions/Forms/Layer8RemoveResourcesListDialog.xml`, `Extensions/Actions/*/Layer8DevicesCollections.xml`, `Extensions/Actions/*/Layer8Collections.xml`

- [ ] **Changes something** (a test collection's members): Add Resources List with names pasted with blanks, duplicates, padding and made-up names adds each real name once. Remove Resources List does the same in reverse.

### L. System Information

Files: `Layer8.Device/SystemInfo.cs`, `Extensions/Actions/*/Layer8Devices.xml`

- [ ] **Both clients:** all seven tabs load.
- [ ] **Overview against the script:** its build, C: and client version match the script's columns. Its uptime is read from the counter first, so on the second client it can differ from the dashboard by about 10 minutes, because that client's clock was set after it booted.
- [ ] **Programs:** lists the 64-bit programs once each, the ConfigMgr client and the Teams Meeting Add-in among them.
- [ ] **Filter, sorting and Export:** filtering works, sorting goes both ways with an arrow, and Export writes the filtered rows in sorted order.
- [ ] **Uninstall, maximized,** next to the Filter box:
  - Edge "wasn't installed by Windows Installer", and nothing is run.
  - The Teams add-in "was installed for one user ... Nothing was run".
- [ ] **Changes something:** uninstalling a test copy of Layer8.msi. The question shows the exact msiexec command, then "Uninstalled Layer8 ...", and the list refreshes without it.
- [ ] **Changes something:** the test MSIs.
  - The one that asks for a restart: "needs a restart to finish".
  - The one that fails: "failed: 1603 (0x643): Fatal error during installation", and it stays in the list.
- [ ] **Without elevation:** a second admin account, not run as administrator, reads every tab.

### M. Windows Security

Files: `Layer8.Device/WindowsSecurity.cs`, `Layer8.Device/SystemInfo.cs`, `Extensions/Actions/*/Layer8Devices.xml`

- [ ] **Defender:** on the second client, its 14 items.
- [ ] **Firewall:** the three profiles, with the domain profile in use.
- [ ] **BitLocker on an unencrypted C::** Suspend and Resume say there's nothing to do, and nothing changes.
- [ ] **Changes something:** Quick Scan starts a scan that the tab then shows.
- [ ] **Maximized:** the buttons are clear of the title bar.

### N. Error Lookup

Files: `Layer8.Device/ErrorLookup.cs`, `Layer8.Device/ErrorCodes.cs`, `Extensions/Actions/*/Layer8Devices.xml`, `Extensions/Actions/*/Layer8Collections.xml`

The script checks the parsing and the text from the console's catalog (its Library section).

- [ ] **The window:** from a device and from Monitoring > Deployments, 0x87D00668, 87D00668, -2016410008 and 1603 give the text the script lists, and junk text says it isn't a code.
- [ ] **Install Application:** shows a failing app's meaning in the list and under it.

### O. Driver Manager

Files: `Layer8.DriverManager/*`, `Extensions/Actions/0ae7814c-*/*`, `Extensions/Actions/3ad39fd0-*/*`, `Extensions/Forms/Layer8*Driver*.xml`, `Extensions/Forms/Layer8Options.xml`

The script checks Lenovo's catalog signature and the signature check on downloads (its Library section).

- [ ] **Dell:** the catalog downloads, and lists Windows 11 and 10.
- [ ] **Lenovo Driver Pack:**
  - Releases are listed newest first, with Windows 11 IoT after Windows 11, and IoT 25H2 lists its 8 packs.
  - The ThinkCentre M70A Gen 6 pack for Windows 11 25H2 shows Downloaded.
  - Options shows the Lenovo catalog link and the folder prefix.
- [ ] **Open and cancel:** the Driver Package Importer, the Driver Legacy Package Importer, Driver Grabber, and the Dell and HP driver packs.

### P. Packaging: the cab, the MSI and leaving features out

Files: `tools/Build-*.ps1`, `tools/Customize-Layer8Cab.ps1`, `tools/Layer8Features.ps1`, `tools/Install-Layer8Dev.ps1`, `tools/Uninstall-Layer8Dev.ps1`, `tools/Dev-Loop.ps1`, `Layer8.Setup/*`, `Layer8.sln`, `Extensions/*`

The script checks:

- the cab's manifest and signatures
- that the installed copy is file for file the built one
- the release copy of Customize-Layer8Cab.ps1 on an unsigned copy

- [ ] **Approved extension, over the previous version** (step 1 does this): the menus, icons, Layer8 Settings, Client Cache, Driver Grabber, Add Resources and the device properties tabs all work.
- [ ] **Built with `-Exclude LAPS, RemoteControl, DriverManager`, signed:**
  - Console Extensions shows a description that says what was left out.
  - The console has no LAPS Password, Driver Grabber, Remote Desktop, Remote Assistance or PowerShell Session.
  - Remote Tools' separators are tidy, and the rest works.
  - Then build and install the full cab again: the build overwrites the same cab.
- [ ] **Customize-Layer8Cab.ps1 `-Exclude LAPS, BitLockerKeys, RestartComputer`,** signed in the same step and imported: no LAPS Password, BitLocker Recovery Key, Restart Computer or Cancel Restart, and everything else is there. Then the full cab again.
- [ ] **Changes something:** Layer8.msi, on a console without the cab (never both).
  - It installs, closing open consoles.
  - The uninstall keeps the console's `XmlStorage\Extensions\Actions` and `Forms` folders.

### Q. Inherited tools: open and cancel

Files: `Layer8/IncrementalCollectionsPage*`, `Layer8/RemoveRetiredContentPage*`, `Layer8/CleanSoftwareUpdatesPage*`, `Layer8/QuickDeployment*`, `Layer8/CollevalStatisticsDialog*`, `Layer8/TaskSequenceImportExport.cs`, `Layer8/DataGridViewCalendarColumn.cs`, `Layer8.Device/ResultCollectionsControl*`, `Layer8.Device/ResultMaintenanceWindowsControl*`, `Layer8.Device/TrackBarWithoutFocus*`, `Extensions/Forms/CollectionResource.xml`, `Extensions/Forms/Layer8IncrementalCollectionsWizard.xml`, `Extensions/Forms/Layer8RemoveRetiredContentWizard.xml`, `Extensions/Forms/Layer8CleanSoftwareUpdatesWizard.xml`, `Extensions/Forms/Layer8QuickDeploymentWizard.xml`

- [ ] **Open and cancel, with nothing changed:** the device properties tabs, Client Cache, Driver Grabber, Incremental Collections, Remove Retired Content, Clean Software Update Groups, the driver package importers, and the Dell and HP driver packs.
- [ ] **Collection Evaluation statistics sorting:** Evaluation Time sorts by the seconds both ways (9.5 below 12), and the arrow follows the sorted column.

### Z. Shared code: run every section

Files: `Layer8/Utility.cs`, `Layer8/Layer8Log.cs`, `Layer8/SafeAction.cs`, `Layer8/Properties/*`, `Layer8/Resources/*`, `Layer8/Layer8.csproj`, `Layer8.Device/Properties/*`, `Layer8.Device/Layer8.Device.csproj`, `Layer8.Device/Device.cs`, `Layer8.Device/ErrorCodes.cs`, `Layer8.DriverManager/Properties/*`, `IniFileParser/*`, `Directory.Build.*`

Everything else is built on these, so a change here can touch any section.

### W. Version, docs and the regression tools

Files: `SharedAssemblyInfo.cs`, `docs/*`, `README.md`, `SECURITY.md`, `LICENSE`, `.github/*`, `*.gitignore`, `*.gitattributes`, `Extensions.Dev/*`, `tools/Test-Layer8Regression.ps1`, `tools/New-Layer8LabTestData.ps1`, `tools/New-Layer8ScaleRecords.ps1`, `tools/Prepare-Layer8LabClient.ps1`

Nothing beyond the every-build steps.

## Before tagging a release

The release is batched: every section above once, on the release build, plus these.

- [ ] **The unsigned release cab** (`Build\Release\<version>`, from `Build-Layer8Extension.ps1` without a certificate):
  1. Turn on "Hierarchy approved console extensions can be unsigned". It's a hierarchy setting, so it's yours.
  2. Delete the signed test copy from the site.
  3. Import with "Allow extension to be unsigned" ticked. It should show Signed: No and Unsigned Binaries Allowed: Yes.
  4. Approve it and enable notifications.
  5. On the console, Uninstall, then Install.
  6. Check: the menus are back, Quick Status works, and the cab's SHA-256 matches what the build printed.
- [ ] **The release copy of Customize-Layer8Cab.ps1:** from the release folder, run from a downloads folder with a space in its name, on the release cab, signing in the same step. Imported, its left-out items are gone and everything else is there.
- [ ] **Changes something:** Layer8.msi upgrading the last release's MSI. There's one Programs entry before and after, Layer8.dll goes from the old version to the new, and the number of menu files is the same.

## Not repeated

One-time events, and setups that take hours or change a lot. Their rows in STATUS.md stay the record. Each one has a Files line, so the script's Changes section names it when its own code changes, and that's when to repeat it as its row describes.

### X1. Upgrading ConfigMgr or the client

Files: `Layer8.Setup/Package.wxs`

Once per ConfigMgr release (2509 to 2609, and the 2609 client). After the next upgrade, run this whole checklist.

### X2. The Lenovo pack download (2 GB) and extract (5 GB)

Files: `Layer8.DriverManager/LenovoDriverPack*`, `Layer8.DriverManager/Authenticode.cs`

The script checks the catalog's signature and the download signature check every run.

### X3. The Driver Package Importer with the Lenovo pack (215 drivers, about 30 minutes)

Files: `Layer8.DriverManager/DriverPackage*`, `Layer8.DriverManager/Driver.cs`, `Layer8.DriverManager/Package.cs`, `Layer8.DriverManager/LegacyPackage*`

### X4. LAPS with the policy switched to unencrypted and back

Files: `Layer8.Device/LAPSDialog.cs`

A Group Policy change, so it's yours to make.

### X5. Missing updates with a real update

Files: `Layer8.Device/ClientActions.cs`

Remove a cumulative update, list it, deploy it, and install it. Repeat when List Missing Updates or Install Missing Updates changes.

### X6. A blue screen (NotMyFault on the second client)

Files: `Layer8.Device/HealthCheck.cs`

It crashes a client. The last dump counts while it's inside the Blue screen window, and the script's Blue screen column says whether it still does.

### X7. An uninstall that runs 33 minutes

Files: `Layer8.Device/SystemInfo.cs`

### X8. Install Application's list, sized 211 ways

Files: `Layer8.Device/ApplicationInstall.cs`

It was a standalone copy of the list, for the hang fixed in 0.9.3.
