# Layer8 status

Updated November 6, 2026. Version 0.1.2, nothing released yet.

Layer8 is early. Everything below was tested in one lab: one site server and one client, on ConfigMgr 2509 (console 5.2509.1038.1000). A "remote" run so far means the client reaching itself by name, plus the site server as the "no ConfigMgr client" case. Treat anything that changes clients as something to try on a few devices first.

## What's been tested

| Area | Tested against | Notes |
|---|---|---|
| Client actions (policy and evaluation cycles) | Lab client | Machine Policy and App Deployment Evaluation fired |
| Client tools (quick status, find software, missing updates, pending restart, policy and GP refresh, client health, cache, repair, provisioning mode, BitLocker status, Defender quick scan) | Lab client; site server for "No ConfigMgr client found" | Install Missing Updates has only ever seen "No missing updates" |
| Runs on a whole collection | All Systems | The four built-in placeholder records (Unknown Computer, Provisioning Device) are skipped by name |
| Online Status | Lab client; site server ("ping blocked, port 445 answers") | |
| Remote tools | Ping, Remote Desktop, browse C$ and the client cache, client logs (folder and zip), Client Log in CMTrace, Computer Management, Event Viewer, Services | Remote Assistance and the PowerShell session start, but the UAC prompt and the session itself haven't been confirmed |
| Copy Names, Copy Member Names, Copy Device Info, Copy Results, Export | Lab client, All Systems | |
| BitLocker Recovery Key | No keys; a fake key from the lab script | Key ID filter works |
| Clear Client Cache | Lab client | |
| Install Application | Lab client | Installs an app and reaches Installed; shows a failing app's 1603 (0x643) |
| Retry Failed Devices, Copy Failed Devices | Required and Available app deployments with the client in Error | Copy groups by error code. Update deployments not tested (the lab has no updates yet) |
| Monitoring > Deployments > Delete | A throwaway app deployment | |
| Redistribute Failed Content (Distribution Status) | A package whose source share doesn't exist | |
| Collections: add and remove resources from a pasted list | Pasted names with blanks, duplicates, padding and made-up names | |
| Layer8 Settings | Lab client | A change is picked up by the next run without restarting the console |
| Driver Manager | Catalog download, Dell catalog (Windows 11 and 10) | Dell and HP driver pack imports not tested |
| Layer8.msi | Install, closing open consoles, uninstall | Uninstall keeps the console's `XmlStorage\Extensions\Actions` and `Forms` folders. Upgrade and the SYSTEM/3010 path not tested |
| Approved console extension | 0.1.2, signed with a lab certificate | Import, approve, enable notifications, install on the console. Menus, icons, Layer8 Settings, Client Cache, Driver Grabber, Add Resources and the device properties tabs all work |

Inherited from ConfigMgr Quick Tools and smoke-tested (opened and cancelled, nothing changed): the device properties tabs, Client Cache, Driver Grabber, Incremental Collections, Remove Retired Content, Clean Software Update Groups, the driver package importers, Driver Manager options, and the Dell and HP driver packs.

## Not tested yet

- Anything against a second client that isn't the console's own computer, and the offline check with a device that's really switched off
- Update deployments: Retry and Copy Failed Devices, Install Missing Updates with an update actually missing, Quick Deployment
- Task Sequence Export to XML and phased deployments
- Restart and Cancel Restart, application uninstall and repair from Install Application, setting the cache size on many devices at once
- Configuration baselines: re-evaluating non-compliant devices and Retry Failed Devices (see rough edges)
- Remote Assistance's offer and the PowerShell session itself
- Dell and HP driver pack imports
- A collection run on 50 or more devices
- Layer8.msi: upgrade over an older version, and an MECM deployment running as SYSTEM with a console open (3010)
- Approved console extension: Require Extension, Revoke Approval and Delete; another console picking it up from a notification

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
