# Layer8 status

Updated November 13, 2026. Version 0.8.1.

Layer8 is early. Everything below was tested in one lab on ConfigMgr 2509 (console 5.2509.1038.1000): a site server, the client the console runs on, and a second Windows 11 client reached over the network. The site server stands in for "no ConfigMgr client". Treat anything that changes clients as something to try on a few devices first.

## What's been tested

| Area | Tested against | Notes |
|---|---|---|
| Client actions (policy and evaluation cycles) | Both clients | Machine Policy and App Deployment Evaluation fired |
| Client tools (quick status, find software, missing updates, pending restart, policy and GP refresh, client health, clear cache, repair, client info, BitLocker and Defender status, Defender quick scan) | Both clients; site server for "No ConfigMgr client found" | Repair: ccmrepair.log on the second client ends "Repair complete". Install Missing Updates has only ever seen "No missing updates" |
| Restart, Cancel Restart, Restart SMS Agent Host Service | Second client | Restart then Cancel Restart: no reboot, uptime kept counting |
| Runs on a whole collection | All Systems; a two-client collection | The four built-in placeholder records (Unknown Computer, Provisioning Device) are skipped by name |
| Online Status, and the offline check before a run | Both clients; site server ("ping blocked, port 445 answers"); the second client switched off, and on with its WMI firewall rules off | Off: "no ping reply, port 445 closed", and Quick Status says "ports 135 and 445 don't answer". WMI blocked: "Online, but WMI is blocked (RPC server unavailable)" |
| Remote tools | Ping, browse C$ and the client cache, client logs (folder and zip), Client Log in CMTrace, Computer Management, Event Viewer, Services: both clients. Remote Desktop: the console's own client | The second client needed the firewall groups in [INSTALL.md](INSTALL.md#the-devices-you-manage) for Computer Management, Event Viewer and Services. Before 0.1.4, Event Viewer opened the console's own logs. Remote Assistance and the PowerShell session start, but the UAC prompt and the session itself haven't been confirmed |
| Copy Names, Copy Member Names, Copy Device Info, Copy Results, Export | Lab client, All Systems | |
| BitLocker Recovery Key | No keys; a fake key from the lab script | Key ID filter works |
| Clear Client Cache | Both clients | On the second client it freed the installed test app's content (110 MB) |
| Install Application | Both clients | Installs an app and reaches Installed; shows a failing app's 1603 (0x643) |
| Retry Failed Devices, Copy Failed Devices | Required and Available app deployments with the client in Error | Copy groups by error code. Update deployments not tested (the lab has no updates yet) |
| Monitoring > Deployments > Delete | A throwaway app deployment | |
| Redistribute Failed Content (Distribution Status) | A package whose source share doesn't exist | |
| Collections: add and remove resources from a pasted list | Pasted names with blanks, duplicates, padding and made-up names | |
| Layer8 Settings | Lab client | A change is picked up by the next run without restarting the console |
| Driver Manager | Catalog download, Dell catalog (Windows 11 and 10) | Dell and HP driver pack imports not tested |
| Lenovo driver packs (0.7.0) | Lenovo's catalog (`catalogv2.xml`): 509 models; ThinkCentre M70A Gen 6 for Windows 11 25H2 | Releases listed newest first, Windows 11 IoT after Windows 11; IoT 25H2 lists its 8 packs. The M70A Gen 6 pack (2 GB) downloaded, matched the catalog's SHA-256 and Lenovo's signature, extracted (about 5 GB) and landed in the source folder; reopened, it shows Downloaded. The catalog's own signature is checked (Lenovo's code signing certificate, chained to a root Windows trusts): Lenovo's catalog passes, freshly downloaded and reused within the hour. A copy with one download link changed is refused, both swapped into the temp folder and downloaded from a link set in Options, and is deleted so the next click downloads a good one. Checked outside the console with the same code: a changed hash, extra white space, no signature, a forged "Lenovo" certificate and another company's signature are refused. Options shows the Lenovo catalog link and folder prefix. The lab has no Lenovo devices, so ticking the site's models hasn't been seen |
| LAPS Password (0.7.0) | The second client, with Windows LAPS backing up to AD | Encrypted: decrypts and shows the account, password, when it changed and when it expires. Expire Now set the expiry in AD, the dialog said so, and Layer8's Group Policy Update on the client had it set a new password straight away. With the policy switched to unencrypted, the client wrote a new plain password and Layer8 showed "Windows LAPS, not encrypted"; switched back, "encrypted" with the newer password |
| Driver Package Importer with a Lenovo pack (0.8.0) | The extracted ThinkCentre M70A Gen 6 pack (224 INF files, 215 drivers), from a normal share where the site server's account has Full Control | All 215 drivers imported, enabled, in a new driver package and category, with their content in the package folder. It took about 30 minutes, most of it the Intel graphics drivers. On the way: a site database at SQL Server 2022 compatibility level 160 refused every driver ("The digest is not valid"; ConfigMgr 2509 supports 150, see [INSTALL.md](INSTALL.md#the-driver-package-importer-says-the-digest-is-not-valid)); and ConfigMgr's own CreateFromINF writes ARM64 platforms as `arm64`, which the database refuses, so Layer8 writes `ARM64` as the console's import does (the pack's one ARM64 driver, deleted and imported again by Layer8). A run with refused drivers leaves the package offered for the next run; a complete one shows No change |
| Send Message (0.8.0) | The second client with nobody signed in; the console machine | "Nobody signed in; not sent", and "Sent to LAB\Administrator" with the pop-up on screen. A 258-character message is refused with the 255 limit and the prompt keeps the text |
| Windows Security (0.8.0) | The second client | Defender: 14 items, including signatures, last scans, threats in the last 30 days and Defender for Endpoint (not onboarded); Quick Scan started a scan that the tab then showed. Firewall: the three profiles from the effective policy, the domain profile in use. BitLocker: an unencrypted C:, where Suspend and Resume now say there's nothing to do. A test build had called both a success, and Windows recorded a suspend and added a recovery password protector to the unencrypted drive |
| Layer8.msi | Install, closing open consoles, uninstall; the published 0.8.0 MSI upgraded by 0.8.1 | Uninstall keeps the console's `XmlStorage\Extensions\Actions` and `Forms` folders. Upgrade: one Programs entry before and after, Layer8.dll 0.8.0.3 to 0.8.1.0, the same 46 menu files. The SYSTEM/3010 path not tested |
| System Information (0.6.0) | Both clients (Windows 11 26H2 and the console machine) | All seven tabs loaded: Overview (model, BIOS, 26H2 build 26300.9974, uptime, memory, C:, IP, "None" for battery on a VM, client version), 7 programs, 6 updates, 252 services, 87 drivers (about 10 seconds), 1 profile, 3 administrators (two local accounts, LAB\Domain Admins). Filter, sorting both ways with an arrow, and Export (filtered rows, sorted order) work. The 64-bit and 32-bit program classes each returned the full list, so programs are listed once without a 64-bit/32-bit label. Not tried: a device with a battery, an offline device, a query that times out (60 seconds) |
| Quick Status in columns (0.5.0) | All Systems: both clients, the site server (no client) and the four placeholder records | Signed in, Up, C: free, C: size, IP address and Client columns. Sorted by C: free (26.8 GB, 61 GB, 100 GB, not as text) and by Up both ways, blanks last; ties go by name; one sort arrow at a time. Export writes every column in the sorted order |
| Error Lookup, and error meanings in Copy Failed Devices and Install Application (0.4.0) | ConfigMgr, Windows Update, Windows and installer codes, as hex, decimal, signed and bare hex, and junk text; the lab's failing app on the second client | Text comes from the console's own catalog (`AdminConsole\bin\resources`), e.g. 0x80240022 "Operation failed for all the updates", 0x87D00668 "Software update still detected as actionable after apply". Copy Failed Devices: "2 x 1603 (0x643): Fatal error during installation". Install Application shows the error in the list and under it when the app is selected. Copy (to the clipboard) not checked |
| Customize-Layer8Cab.ps1 on a released cab | The one-file release copy, run from a downloads folder with a space in its name: 0.3.0 without LAPS, BitLockerKeys and RestartComputer, signed in the same step | 4 menu files dropped, 3 changed. Imported signed; on the console no LAPS Password, BitLocker Recovery Key, Restart Computer or Cancel Restart, and everything else there |
| Leaving features out of a build (`-Exclude`) | A signed 0.2.2 cab built without LAPS, RemoteControl and DriverManager | Its description says so in Console Extensions. On the console: no LAPS Password, Driver Grabber, Remote Desktop, Remote Assistance or PowerShell Session; Remote Tools' separators tidy; the rest works. The MSI build with `-Exclude` built but wasn't installed |
| Approved console extension | Signed with a lab certificate: 0.1.2 to 0.1.4 by the build script, 0.2.0 by `Sign-Layer8Cab.ps1` (now `Customize-Layer8Cab.ps1`) from the unsigned release cab | Import, approve, enable notifications, install on the console, upgrade over the previous version. Menus, icons, Layer8 Settings, Client Cache, Driver Grabber, Add Resources and the device properties tabs all work |

Inherited from ConfigMgr Quick Tools and smoke-tested (opened and cancelled, nothing changed): the device properties tabs, Client Cache, Driver Grabber, Incremental Collections, Remove Retired Content, Clean Software Update Groups, the driver package importers, and the Dell and HP driver packs. Driver Manager options was opened again for 0.7.0's Lenovo fields.

## Not tested yet

- Remote Desktop against a client other than the console's own
- Update deployments: Retry and Copy Failed Devices, Install Missing Updates with an update actually missing, Quick Deployment
- Task Sequence Export to XML and phased deployments
- Application uninstall and repair from Install Application (the lab's apps don't offer them), setting the cache size on many devices at once
- Configuration baselines: re-evaluating non-compliant devices and Retry Failed Devices (see rough edges)
- Remote Assistance's offer and the PowerShell session itself
- Dell and HP driver pack imports, including the check (since 0.2.1) that each HP SoftPaq is signed by HP before it runs
- Lenovo: ticking the site's models (the lab has no Lenovo devices)
- Windows Security: Suspend and Resume on an encrypted drive (the lab's clients aren't encrypted)
- Send Message to a collection, and to a device with several people signed in
- LAPS: legacy Microsoft LAPS (the lab forest doesn't have its schema), and passwords Windows LAPS backs up to Microsoft Entra ID (Layer8 reads Active Directory only)
- A collection run on 50 or more devices
- Layer8.msi: an MECM deployment running as SYSTEM with a console open (3010)
- Approved console extension: importing the cab unsigned (needs "Hierarchy approved console extensions can be unsigned"), Require Extension, Revoke Approval and Delete, another console picking it up from a notification

## Known rough edges

- **Retry uses summarized status.** A device that failed minutes ago may be missing, and one that has recovered since may still be listed. Both dialogs say so; run Summarization first.
- **Baseline rows in Monitoring > Deployments** don't offer Layer8's baseline actions yet: the console uses a different node for them than the one Layer8's menu is registered on.
- **Device properties form is shared with Quick Tools.** With both installed, whichever was installed last supplies the tabs.
- **Approved console extension quirks** (none documented by Microsoft; details in [INSTALL.md](INSTALL.md)): the console only loads approved extensions while `XmlStorage\Extensions\Actions` exists, a signed cab also needs signed DLLs, and the console logs a harmless `Error: …\Forms\..\bin\Layer8` line when a Layer8 window opens.
- **Times from ConfigMgr** can come with a "+000" that isn't UTC. Install Application's deadlines are fixed; Check Pending Restart's restart deadline goes through the same kind of conversion and hasn't been checked against Software Center.
- **Collection Evaluation Statistics** can take a while to read colleval.log from the site server, and OK doesn't close the window until it finishes (the X does).
- **Skipped records** share the offline color in the progress bar; the counts under it list them as Skipped.
- **Large retries** keep every device record in memory until the window closes. Fine at lab scale; not yet tried on a 1,000-device deployment.

## Next up

In order:

1. **AD cleanup**: computer accounts in Active Directory that ConfigMgr hasn't heard from, and the reverse.
2. **Uninstall from System Information**: remove an MSI-installed program from the Programs tab (System Information only reads for now).

## Ideas

| Idea | Why |
|---|---|
| Entra-backed LAPS passwords (Microsoft Graph) | Windows LAPS can back up to Entra ID instead of AD |
| Columns for Online Status, BitLocker Status, Defender Status and Client Info | Quick Status's sortable columns (0.5.0) work for any tool |
| Recent sign-ins in Quick Status | Who was on it, not just who is on it |
| Export and import Layer8 Settings | Same settings on every admin's console |
