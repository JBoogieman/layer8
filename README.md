# Layer8

Open-source right-click actions for the ConfigMgr (MECM) console. Because it's always layer 8.

Layer8 starts from [ConfigMgr Quick Tools](https://github.com/Flowman/ConfigMgr-Quick-Tools) by Peter Szalatnay and builds on it. It's an independent project and isn't affiliated with Microsoft or Recast Software.

## What's in it

Organized by where you right-click in the console.

**A device, or a device collection** (a collection runs on every member):
- **Client Actions:** the eleven standard client cycles, plus Quick Status (who's signed in, uptime, free space on C:, IP address, client version), Find Software (by name, or older than a version: `Chrome < 131`), Check Pending Restart, List and Install Missing Updates, Full Machine Policy Refresh, Group Policy Update, Client Health Check, Client Info, Reset Policy, Clear Client Cache, Repair Client, Turn Off Provisioning Mode, Restart SMS Agent Host Service, Restart Computer and Cancel Restart, Defender Status and Quick Scan, BitLocker Status
- **On a collection, also:** Online Status, Collect Client Logs, and Retry All Failed Deployments (every app, update and baseline deployment to the collection, on the devices in error)
- Every run shows one answer per device: done, offline, blocked, or why it failed. It works through 32 devices at a time (change it in Layer8 Settings), and Run Again on Failed, Copy Results (for Excel) and Export are on every result window

**A device:**
- **Remote Tools:** Online Status (ping, or port 445 when ping is blocked), Ping, Remote Desktop, Remote Assistance, PowerShell Session, Browse C$, Client Logs, Browse ccmcache, Client Log in CMTrace, Collect Client Logs (zipped to your desktop), Computer Management, Event Viewer, Services
- **Install Application:** the apps deployed to the device; install one now, like Software Center
- **LAPS Password** and **BitLocker Recovery Key** (from Active Directory; type the Key ID from the recovery screen to find the right key)
- Copy Names, Copy Device Info, Add to existing collections, Show Messages, Client Cache, Driver Grabber, and extra device properties tabs (update compliance, client health, collection membership)

**A collection:** Add Resources List and Remove Resources List (paste names), Copy Member Names

**Monitoring:**
- **Deployments:** Retry Failed Devices (re-runs the deployment on every device in error; for an Available app, installs it), Copy Failed Devices (with error codes), Delete
- **Distribution Status:** Redistribute Failed Content, Copy Failed Distribution Points

**Elsewhere:** Quick Deployment (deploy to several collections at once), Task Sequence Export to XML, Clean Software Update Groups, Remove Retired Content, Incremental Collections and Collection Evaluation Statistics, Driver Manager (grabber, importers, Dell and HP driver packs)

**Error Lookup** (on the Layer8 menus): paste any error code (`0x87D00324`, `-2016410844`, `1603`) and see what it means, from ConfigMgr's own error catalog and Windows'. Copy Failed Devices and Install Application show the meaning next to every code too.

**Layer8 Settings** (on the Layer8 menus): how many devices to contact at once, a quick offline check before connecting, and the log level.

Don't want some of it? `Customize-Layer8Cab.ps1 -Exclude LAPS, BitLockerKeys, RemoteControl` takes those menu items out of the released cab entirely, no building needed ([how](docs/INSTALL.md#leaving-features-out)).

The devices you manage need nothing installed except the ConfigMgr client, just some firewall rules: see [what the devices need](docs/INSTALL.md#the-devices-you-manage).

## Building

1. Install the ConfigMgr console and Visual Studio with the .NET desktop development workload (.NET Framework 4.8).
2. Open `Layer8.sln` and build.

Console references resolve automatically (see `Directory.Build.props`). If your console is installed somewhere unusual, build with `-p:ConfigMgrConsoleBin=<path to AdminConsole\bin>`.

To build the packages, run `tools\Build-Layer8Msi.ps1` (everything in Release, then `Build\Installer\Release\Layer8.msi`; the first run downloads the WiX Toolset from nuget.org), then `tools\Build-Layer8Extension.ps1` for `Build\Extension\Layer8-<version>.cab` (add `-CertificateThumbprint` to sign it).

## Installing

Download `Layer8.msi` or `Layer8-<version>.cab` from [Releases](https://github.com/JBoogieman/layer8/releases). There are two ways to put Layer8 in a console. They give you the same menus. [docs/INSTALL.md](docs/INSTALL.md) explains how each one works, what it needs, step by step with screenshots, and what to do when the menus don't show up.

| | Approved console extension (`Layer8-<version>.cab`) | MSI (`Layer8.msi`) |
|---|---|---|
| How | Import it into the site once and approve it; each console installs it from the Console Extensions node | Run it on each computer with the console, or deploy it with MECM |
| Sites with **Only allow console extensions that are approved for the hierarchy** on | Works | Ignored by the console |
| Signing | Signed with a certificate the site trusts, or unsigned if the hierarchy allows it | Not needed |

If that hierarchy setting is on, use the cab. Never put both on the same console: every menu would show up twice.

## Developer install

From an elevated PowerShell with the console closed, run `tools\Install-Layer8Dev.ps1`. It builds Layer8, copies it into the console's folders and records every file it placed; `tools\Uninstall-Layer8Dev.ps1` removes exactly those files. In a lab, `tools\New-Layer8LabTestData.ps1` creates a test app, an app that always fails (for Retry Failed Devices) and a fake BitLocker recovery key to test against (`-Remove` deletes them). Don't use it on a machine where the MSI is installed: both put the same files in the same places, so uninstall one before using the other. If PowerShell blocks the script, run it as `powershell -ExecutionPolicy Bypass -File tools\Install-Layer8Dev.ps1`.

The developer install puts Layer8 in the same places as the MSI, so the console ignores it too while **Only allow console extensions that are approved for the hierarchy** is ticked (Administration > Site Configuration > Sites > Hierarchy Settings > General). Uninstalling it leaves the console's own `XmlStorage\Extensions\Actions` and `Forms` folders in place: the console needs `Actions` to load approved extensions.

## Status

Early days: see [docs/STATUS.md](docs/STATUS.md) for what's been tested, what hasn't, the known rough edges and the ideas list. Layer8 has its own assembly names, namespaces and console IDs, so its menus and dialogs no longer clash with ConfigMgr Quick Tools. Layer8 ships only its own DLLs, so the two never share a file in the console's bin folder. The one overlap left: both replace the device properties form, so with both installed, whichever was installed last supplies those tabs. Automatic update checks have been removed.

Found a security problem? Please report it privately: see [SECURITY.md](SECURITY.md).

## Logs

Layer8 writes a CMTrace-format log to `%LOCALAPPDATA%\Layer8\Logs\Layer8.log`. Each console session starts with a line showing the Layer8, console and .NET versions. To get more detail, set the log level to `Debug` in Layer8 Settings, or from a prompt (it's picked up within a few seconds, no console restart):

```
reg add HKCU\SOFTWARE\Layer8 /v LogLevel /d Debug /f
```

Use `Debug`, `Info` (the default), `Warn` or `Error`.

The log rolls over to `Layer8.lo_` at 2 MB, the same way ConfigMgr's own logs do.

## Credits and license

Based on ConfigMgr Quick Tools by Peter Szalatnay. Released under the MIT license; see [LICENSE](LICENSE).

AI was used to help bring this vision to life.
