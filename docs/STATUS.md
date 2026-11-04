# Layer8 status: November 3, 2026

Working notes, not documentation: where Layer8 stands, what's left before a public release, and the ideas list. Update the date and tick boxes as things land.

**In one line:** there are enough features for a first release. Testing is the gap: everything has been run in one lab with a single client.

- Version in the code: 0.1.0 (nothing tagged yet)
- Repo: private
- Lab: site server CM01 (no ConfigMgr client) and DEV01 (console and client). There's no second client yet, so a "remote" run so far means DEV01 reaching itself by name, plus CM01 as the "no client" case.

## What's been tested

| Area | Tested against | Notes |
|---|---|---|
| Client actions (policy and evaluation cycles) | DEV01 | Machine Policy and App Deployment Evaluation fired |
| Client tools (all 12) | DEV01; CM01 for "No ConfigMgr client found" | Install Missing Updates has only ever seen "No missing updates" |
| Runs on a whole collection | All Systems | The 4 placeholder records are skipped by name |
| Online Status | DEV01; CM01 ("ping blocked, port 445 answers") | |
| Remote tools | Ping, Remote Desktop, browse C$, client logs, Computer Management, Event Viewer, Services | Remote Assistance and the PowerShell session start, but the UAC prompt and the session itself haven't been confirmed |
| Copy Names, Copy Member Names, Copy Results | DEV01, All Systems | |
| BitLocker Recovery Key | No keys; the lab script's fake key | Key ID filter works ("4c41") |
| Clear Client Cache | DEV01 | 109.6 MB freed |
| Install Application | DEV01 | Installed Layer8 Test App from the window; it reached Installed. Shows the fail app's 1603 (0x643) |
| Driver Manager | Catalog download | Dell and HP driver pack imports not tested |
| MSI | Install; closing open consoles | 3 consoles closed, exit 0. Upgrade, uninstall and the SYSTEM/3010 path not tested |
| Logging | DEV01 | Log level change picked up without a restart |

Inherited from ConfigMgr Quick Tools, smoke-tested Nov 2 (opened and cancelled, nothing changed):

- Device properties tabs: all six show up. Collections lists DEV01's four collections. Maintenance Windows, Software Updates, Software Updates Compliance, Client Health and Driver Compliance open without errors but showed nothing (see rough edges).
- Client Cache (location, size, used space), Driver Grabber (WMI and ADMIN$ checks pass), Incremental Collections, Remove Retired Content, Clean Software Update Groups, Driver Package Importer, Driver Legacy Package Importer, Driver Manager options, Dell and HP Driver Pack: all open and cancel cleanly.
- Not testable in this lab yet: Quick Deployment and anything with update data (the site has never synchronized software updates: All Software Updates shows 0), Task Sequence Export to XML (no task sequences), phased deployments.

## New since October 31

- **Retry Failed Devices** and **Copy Failed Devices** on Monitoring > Deployments. For an application or software update deployment, Layer8 finds the devices in Error and either re-runs that deployment's evaluation on all of them (32 at a time, one row per device, Copy Results) or copies their names.
  - Tested on Layer8 Fail App with DEV01 in Error: Copy Failed Devices copied "DEV01"; Retry Failed Devices confirmed the app, count and collection, then reported "Application evaluation started" for DEV01. AppEnforce.log on DEV01 was written the same minute (2:29 PM); its contents weren't checked.
  - Also tested: the Layer8 menu is only offered for application and software update deployments (greyed out on a program deployment), and Copy Failed Devices with nothing in error explains why.
  - The failure took over an hour to reach Monitoring: the lab site didn't summarize between 1:15 PM and about 2:25 PM, even after Run Summarization.
- **Monitoring > Deployments > Delete** pointed at the wrong class (`ClientActions` instead of `Device`), so it couldn't have worked since the rename. Fixed and tested: deleted a throwaway deployment of Layer8 Test App to All Users and left the other three alone. The confirmation now names the collection, since the same app is often deployed to several.
- **Layer8 Fail App** in `tools\New-Layer8LabTestData.ps1`: a required app whose install exits with 1603, so DEV01 lands in Error and Retry has something to retry.
- **Install Application status text** used the wrong table for the client's EvaluationState numbers, so a failed install read "waiting to download" and an available app "detecting". It now follows Microsoft's CCM_Application reference (4 reads "last install failed"). The Deployment and Status columns now fit their text and stretch with the window.
- **Fixes from a code review** (four findings from Gemini, each checked against the code first; two more found while checking):
  - Collection evaluation statistics changed the window from a background thread, crashed when a run evaluated no collections, and crashed on a collection deleted since the log was written. Now it reads in the background and fills the window on the UI thread.
  - Add Resources to Collection put pasted lines into the query as they were. Now it trims them, drops blanks and duplicates, leaves out lines that can't be computer names (an apostrophe broke the WQL), and says which names ConfigMgr doesn't have. Gemini's suggested fix would have used `Utility.WqlEscapeString`, which is for LIKE patterns and would have stopped names with underscores from matching.
  - LAPS put the device name into the LDAP filter unescaped (the Unknown Computer records' brackets broke it) and showed a blank window for computers AD doesn't have. Now: same name check and escaping as the BitLocker lookup, and "Not found in Active Directory".
  - Client actions kept the schedule ID, full-scan flag and method in static fields, so a second action started while a slow one was connecting could change what the first one triggered. Find Software's search and the progress window's title had the same problem (a client action run after Find Software showed "Find Software: ..." as its title). Each run now gets its own values.
  - Tested Nov 1: Add Resources with blank lines, padding, a lowercase name, a duplicate, an apostrophe and a made-up name (left out the apostrophe line, found CM01 and DEV01 once each, reported "Not in ConfigMgr: NOPE99"; searched only, nothing added). LAPS on DEV01 ("No LAPS data", so the escaped lookup found it). Find Software on Layer8 Dev, then Machine Policy on it: the second window's title was right and DEV01 completed. Full Hardware Inventory on DEV01 alone ran without an error. Collection Evaluation Statistics opened without crashing but showed "No data": the lab has no incremental collections, so the filled-in path is untested. The console greys out the Layer8 menu on Unknown Computer records, so LAPS can't be run on them from there.
  - The statistics window labelled both its last rows "Longest Collection Eval"; the second is now "Shortest Collection Eval".
- **Install Application deadlines** were shifted by the time zone: the client reports them as the device's clock time tagged "+000", and Layer8 converted them as UTC (a 7:21 PM deadline showed as 12:21 PM in Pacific time). Now read as clock time, like Software Center: the fail app's deadline shows 10/31/2026 7:21 PM, when it was created.

- **Nov 3 batch** (tested on DEV01 unless noted):
  - **Copy Failed Devices shows error codes**, grouped by error, most common first, and copies Device / Error code / Error for Excel. Layer8 Fail App: "1 x 1603 (0x643): Fatal error during installation". Application codes come from SMS_AppDeploymentErrorAssetDetails, update codes from SMS_SUMDeploymentAssetDetails (the update side is untested until the lab has updates).
  - **Export...** on every progress window saves the run as a UTF-8 CSV named after the action and time.
  - **Properties tabs fill on the UI thread.** Client Health now lists every health check and the last evaluation time (it stayed blank before). Software Updates Compliance threw "Unexpected WMI query failure" on a client that has never scanned; fixed by looking up the scan history without the helper that throws on no results (retest after the next build). Client Cache also copes with a cache set larger than the free space.
  - **Dell Driver Pack offers Windows 11 and 10.** Windows 11 loads Dell's current models (Dell Pro 13 and 14, Dell 14 Premium); nothing was imported.

## Ready-to-publish checklist

### Testing

- [x] Retry Failed Devices end to end: Layer8 Fail App in Error, Retry reaches DEV01; Copy Failed Devices gives the right names (AppEnforce.log on DEV01 not checked)
- [ ] Software update deployment: Retry Failed Devices and Copy Failed Devices on an update deployment with a device in Error
- [x] Install Application installs Layer8 Test App (state reaches Installed)
- [x] Monitoring > Deployments > Delete removes a throwaway deployment
- [ ] Second client VM (DEV02): every client tool, remote tool and collection run against a machine that isn't the console
- [ ] Install Missing Updates with an update actually missing (deploy one update to Layer8 Dev)
- [ ] Remote Assistance: UAC prompt and offer; PowerShell session opens over WinRM
- [x] Smoke test the inherited Quick Tools features listed above (Nov 2; see the rough edges for what it found)
- [ ] Driver Manager: import one Dell and one HP pack, or label Driver Manager as experimental
- [ ] Scale: a collection run on 50+ devices, mostly offline is fine, to check the 32-at-a-time limit and that the console stays usable (the pilot can cover this)
- [ ] MSI paths: clean machine, upgrade over the previous MSI, uninstall leaves nothing behind, MECM deployment as SYSTEM with a console open (3010)
- [ ] A week of everyday use of the read-only tools (Online Status, Quick Status, Copy Names, BitLocker lookup) before the tools that change clients

### Repo and release

- [ ] Read through the repo for anything lab-specific (names, site codes, paths)
- [ ] LICENSE keeps Peter Szalatnay's MIT notice; README credit stays
- [ ] README: screenshots, the console version tested, known limitations
- [ ] README: a short AI disclosure
- [ ] Decide on signing. An unsigned MSI shows "unknown publisher" and SmartScreen warnings. Acceptable for a first release if the README says so
- [ ] Bump to 0.2.0 in `SharedAssemblyInfo.cs`, tag `v0.2.0`, GitHub release with the MSI and release notes
- [ ] Make the repo public

## Next steps, in order

1. Build and install this commit. Run the lab script again so it adds Layer8 Fail App, run Machine Policy Retrieval on DEV01, and wait for the deadline install to fail.
2. Monitoring > Deployments: Run Summarization, then Retry Failed Devices and Copy Failed Devices on Layer8 Fail App.
3. Install Layer8 Test App through Install Application.
4. Delete a throwaway deployment from Monitoring.
5. Build DEV02 and repeat the client and remote tool runs against it.
6. Work down the testing list, then the release list.

## Rough edges to iron out

- **Retry and Available deployments.** Retry triggers evaluation, which retries required deployments. An Available app in Error won't reinstall by itself. Fix: for Available app deployments, call `CCM_Application.Install` for that app on each failed device, the same call Install Application uses.
- **Retry uses summarized status.** A device that failed minutes ago may be missing, and one that's since recovered may still be listed. Both dialogs say so; Run Summarization first. In the lab, summarization took over an hour to pick up a failure. Worth checking whether `SMS_AppDeploymentAssetDetails` or the client's own state (as Install Application reads it) is the better source.
- **Retry menu filter.** The Layer8 group on Monitoring > Deployments should only show for application and software update deployments. If the console ignores the filter on a group, the buttons show everywhere and Layer8 explains what it supports. Check which one happens.
- **Remote Assistance and PowerShell session** prompts unconfirmed (see testing).
- **Device properties form** is shared with Quick Tools: with both installed, whichever was installed last supplies the tabs.
- **Approved console extensions.** Layer8 is an old-style extension, so the console ignores it while "Only allow console extensions that are approved for the hierarchy" is ticked. Locked-down sites often tick it. The long-term fix is packaging Layer8 as an approved extension; research what that takes before promising it.
- **Other ConfigMgr times may have the same "+000" shift** as Install Application's deadlines: Check Pending Restart's restart deadline goes through the same kind of conversion. Check it against Software Center.
- **Lab gaps:** sync software updates (at least one product and classification), and create one task sequence, so update and task sequence features can be tested.
- **Collection Evaluation Statistics** took about 30 seconds to read colleval.log from CM01 in the lab, and OK didn't close the window until it finished (the X did).
- **Skipped records count as offline** in the progress totals. A separate "skipped" count would read better.
- **Large retries** keep every device record in memory until the window closes. Fine at lab scale; watch it on a 1,000-device deployment.

## Nice-to-have features

Rough size: S is an evening, M a session or two, L a project.

| Idea | Size | Why |
|---|---|---|
| Retry for Available app deployments (see rough edges) | S | Makes Retry work for every app deployment |
| Retry for configuration baselines and task sequences | M | Baselines are a trigger away; task sequences need their rerun rules respected |
| Redistribute failed content: on Distribution Status, redistribute to every DP in error | M | Another daily chore that's click-by-click in the console |
| Windows LAPS lookup (`msLAPS-Password`, encrypted passwords; Entra-backed needs Graph) | M–L | Legacy LAPS is on its way out |
| Settings: devices at a time, timeouts, log level | S | Today these are constants or a registry value |
| Recent sign-ins in Quick Status | S | Who was on it, not just who is on it |
| Open one client log in CMTrace over C$ | S | Saves browsing to the log folder |
| Lenovo warranty and driver packs | M | Dell and HP have driver packs; Lenovo is the gap |
| Retry everything failed for a collection, across deployments | M | One click after an outage |
| Approved-extension packaging (see rough edges) | L | Needed for sites that only allow approved extensions |
