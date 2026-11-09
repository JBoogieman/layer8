# Layer8 status

Updated November 8, 2026. Version 0.2.1.

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
| Layer8.msi | Install, closing open consoles, uninstall | Uninstall keeps the console's `XmlStorage\Extensions\Actions` and `Forms` folders. Upgrade and the SYSTEM/3010 path not tested |
| Approved console extension | Signed with a lab certificate: 0.1.2 to 0.1.4 by the build script, 0.2.0 by `Sign-Layer8Cab.ps1` from the unsigned release cab | Import, approve, enable notifications, install on the console, upgrade over the previous version. Menus, icons, Layer8 Settings, Client Cache, Driver Grabber, Add Resources and the device properties tabs all work |

Inherited from ConfigMgr Quick Tools and smoke-tested (opened and cancelled, nothing changed): the device properties tabs, Client Cache, Driver Grabber, Incremental Collections, Remove Retired Content, Clean Software Update Groups, the driver package importers, Driver Manager options, and the Dell and HP driver packs.

## Not tested yet

- Remote Desktop against a client other than the console's own
- Update deployments: Retry and Copy Failed Devices, Install Missing Updates with an update actually missing, Quick Deployment
- Task Sequence Export to XML and phased deployments
- Application uninstall and repair from Install Application (the lab's apps don't offer them), setting the cache size on many devices at once
- Configuration baselines: re-evaluating non-compliant devices and Retry Failed Devices (see rough edges)
- Remote Assistance's offer and the PowerShell session itself
- Dell and HP driver pack imports, including the check (since 0.2.1) that each HP SoftPaq is signed by HP before it runs
- A collection run on 50 or more devices
- Layer8.msi: upgrade over an older version, and an MECM deployment running as SYSTEM with a console open (3010)
- Approved console extension: importing the cab unsigned (needs "Hierarchy approved console extensions can be unsigned"), Require Extension, Revoke Approval and Delete, another console picking it up from a notification

## Known rough edges

- **Retry uses summarized status.** A device that failed minutes ago may be missing, and one that has recovered since may still be listed. Both dialogs say so; run Summarization first.
- **Baseline rows in Monitoring > Deployments** don't offer Layer8's baseline actions yet: the console uses a different node for them than the one Layer8's menu is registered on.
- **Device properties form is shared with Quick Tools.** With both installed, whichever was installed last supplies the tabs.
- **Approved console extension quirks** (none documented by Microsoft; details in [INSTALL.md](INSTALL.md)): the console only loads approved extensions while `XmlStorage\Extensions\Actions` exists, a signed cab also needs signed DLLs, and the console logs a harmless `Error: …\Forms\..\bin\Layer8` line when a Layer8 window opens.
- **Times from ConfigMgr** can come with a "+000" that isn't UTC. Install Application's deadlines are fixed; Check Pending Restart's restart deadline goes through the same kind of conversion and hasn't been checked against Software Center.
- **Collection Evaluation Statistics** can take a while to read colleval.log from the site server, and OK doesn't close the window until it finishes (the X does).
- **Skipped records count as offline** in the progress totals.
- **Large retries** keep every device record in memory until the window closes. Fine at lab scale; not yet tried on a 1,000-device deployment.

## Ideas

| Idea | Why |
|---|---|
| Windows LAPS lookup (`msLAPS-Password`, encrypted passwords; Entra-backed needs Graph) | Legacy LAPS is on its way out |
| Recent sign-ins in Quick Status | Who was on it, not just who is on it |
| Lenovo driver packs | Dell and HP have them |
| Export and import Layer8 Settings | Same settings on every admin's console |
