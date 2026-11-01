# Layer8 status: October 31, 2026

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
| Install Application | DEV01 | The window lists apps and says why Install is off. A real install hasn't been done yet (the test app was recreated with registry detection for this) |
| Driver Manager | Catalog download | Dell and HP driver pack imports not tested |
| MSI | Install; closing open consoles | 3 consoles closed, exit 0. Upgrade, uninstall and the SYSTEM/3010 path not tested |
| Logging | DEV01 | Log level change picked up without a restart |

Inherited from ConfigMgr Quick Tools and not retested since the fork: extended device properties tabs, add resources to a collection, LAPS (legacy) lookup, software update clean-up, phased deployments.

## New today, not tested yet

- **Retry Failed Devices** and **Copy Failed Devices** on Monitoring > Deployments. For an application or software update deployment, Layer8 finds the devices in Error and either re-runs that deployment's evaluation on all of them (32 at a time, one row per device, Copy Results) or copies their names.
- **Monitoring > Deployments > Delete** pointed at the wrong class (`ClientActions` instead of `Device`), so it couldn't have worked since the rename. Fixed.
- **Layer8 Fail App** in `tools\New-Layer8LabTestData.ps1`: a required app whose install exits with 1603, so DEV01 lands in Error and Retry has something to retry.

## Ready-to-publish checklist

### Testing

- [ ] Retry Failed Devices end to end: Layer8 Fail App in Error, Run Summarization, Retry, AppEnforce.log on DEV01 shows a new attempt; Copy Failed Devices gives the right names
- [ ] Install Application installs Layer8 Test App (state reaches Installed, `HKLM\SOFTWARE\Layer8Test` exists)
- [ ] Monitoring > Deployments > Delete removes a throwaway deployment
- [ ] Second client VM (DEV02): every client tool, remote tool and collection run against a machine that isn't the console
- [ ] Install Missing Updates with an update actually missing (deploy one update to Layer8 Dev)
- [ ] Remote Assistance: UAC prompt and offer; PowerShell session opens over WinRM
- [ ] Smoke test the inherited Quick Tools features listed above, or mark them as untested in the README
- [ ] Driver Manager: import one Dell and one HP pack, or label Driver Manager as experimental
- [ ] Scale: a collection run on 50+ devices, mostly offline is fine, to check the 32-at-a-time limit and that the console stays usable (the pilot can cover this)
- [ ] MSI paths: clean machine, upgrade over the previous MSI, uninstall leaves nothing behind, MECM deployment as SYSTEM with a console open (3010)
- [ ] A week of everyday use of the read-only tools (Online Status, Quick Status, Copy Names, BitLocker lookup) before the tools that change clients

### Repo and release

- [ ] Read through the repo for anything lab-specific (names, site codes, paths)
- [ ] LICENSE keeps Peter Szalatnay's MIT notice; README credit stays
- [ ] README: screenshots, the console version tested, known limitations
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
- **Retry uses summarized status.** A device that failed minutes ago may be missing, and one that's since recovered may still be listed. Both dialogs say so; Run Summarization first.
- **Retry menu filter.** The Layer8 group on Monitoring > Deployments should only show for application and software update deployments. If the console ignores the filter on a group, the buttons show everywhere and Layer8 explains what it supports. Check which one happens.
- **Remote Assistance and PowerShell session** prompts unconfirmed (see testing).
- **Device properties form** is shared with Quick Tools: with both installed, whichever was installed last supplies the tabs.
- **Approved console extensions.** Layer8 is an old-style extension, so the console ignores it while "Only allow console extensions that are approved for the hierarchy" is ticked. Locked-down sites often tick it. The long-term fix is packaging Layer8 as an approved extension; research what that takes before promising it.
- **Skipped records count as offline** in the progress totals. A separate "skipped" count would read better.
- **Large retries** keep every device record in memory until the window closes. Fine at lab scale; watch it on a 1,000-device deployment.

## Nice-to-have features

Rough size: S is an evening, M a session or two, L a project.

| Idea | Size | Why |
|---|---|---|
| Failed devices with error codes: copy or export each device's error code and description, grouped ("12 devices: 0x87D00607") | S–M | Tells you why they failed before you retry |
| Retry for Available app deployments (see rough edges) | S | Makes Retry work for every app deployment |
| Retry for configuration baselines and task sequences | M | Baselines are a trigger away; task sequences need their rerun rules respected |
| Redistribute failed content: on Distribution Status, redistribute to every DP in error | M | Another daily chore that's click-by-click in the console |
| Windows LAPS lookup (`msLAPS-Password`, encrypted passwords; Entra-backed needs Graph) | M–L | Legacy LAPS is on its way out |
| Export results to CSV | S | Clipboard works, but a file is easier to attach to a ticket |
| Settings: devices at a time, timeouts, log level | S | Today these are constants or a registry value |
| Recent sign-ins in Quick Status | S | Who was on it, not just who is on it |
| Open one client log in CMTrace over C$ | S | Saves browsing to the log folder |
| Lenovo warranty and driver packs | M | Dell and HP have driver packs; Lenovo is the gap |
| Retry everything failed for a collection, across deployments | M | One click after an outage |
| Approved-extension packaging (see rough edges) | L | Needed for sites that only allow approved extensions |
