# Layer8

Open-source right-click actions for the ConfigMgr (MECM) console. Because it's always layer 8.

Layer8 starts from [ConfigMgr Quick Tools](https://github.com/Flowman/ConfigMgr-Quick-Tools) by Peter Szalatnay and builds on it. It's an independent project and isn't affiliated with Microsoft or Recast Software.

## What's in it

- Client actions on devices and collections
- Client tools on devices and collections: quick status (signed-in users, uptime, free space on C:, client version), find installed software by name, list and install missing updates, check for a pending restart, full machine policy refresh, repair the client, turn off provisioning mode
- Online status for devices and whole collections: ping, or port 445 when ping is blocked
- Remote tools on devices: ping, Remote Desktop, Remote Assistance, PowerShell session, browse C$, client logs, Computer Management, Event Viewer, Services
- Copy device names, or every member of a collection, to the clipboard
- Extended device properties: update compliance, client health, collection membership
- Collection management: add resources from a list
- Driver Manager: grabber, importer, Dell and HP driver packs
- LAPS password and BitLocker recovery key lookup (from Active Directory; type the Key ID from the recovery screen to find the right key)
- Software update clean-up and phased deployments

## Building

1. Install the ConfigMgr console and Visual Studio with the .NET desktop development workload (.NET Framework 4.8).
2. Open `Layer8.sln` and build.

Console references resolve automatically (see `Directory.Build.props`). If your console is installed somewhere unusual, build with `-p:ConfigMgrConsoleBin=<path to AdminConsole\bin>`. To build the installer, run `tools\Build-Layer8Msi.ps1`. It builds everything in Release and produces `Build\Installer\Release\Layer8.msi` (the first run downloads the WiX Toolset from nuget.org).

## Installing

From an elevated prompt:

```
msiexec /i Layer8.msi /qn
```

The MSI finds the console through the registry, puts Layer8's DLLs in the console's `bin` folder and its menus in `XmlStorage\Extensions`, and shows up in Add/Remove Programs. `msiexec /x Layer8.msi /qn` removes it, and a newer Layer8.msi upgrades an installed one in place. It refuses to install on a machine without the console.

An open console locks Layer8's DLLs, so the installer asks it to close, the same as clicking its X. A silent install running as SYSTEM (an MECM deployment) can't reach a signed-in user's console; then Windows replaces the files at the next restart and msiexec returns 3010, which MECM treats as a soft reboot.

## Developer install

From an elevated PowerShell with the console closed, run `tools\Install-Layer8Dev.ps1`. It builds Layer8, copies it into the console's folders and records every file it placed; `tools\Uninstall-Layer8Dev.ps1` removes exactly those files. Don't use it on a machine where the MSI is installed: both put the same files in the same places, so uninstall one before using the other. If PowerShell blocks the script, run it as `powershell -ExecutionPolicy Bypass -File tools\Install-Layer8Dev.ps1`.

Layer8 is an old-style console extension, so the console ignores it while **Only allow console extensions that are approved for the hierarchy** is ticked (Administration > Site Configuration > Sites > Hierarchy Settings > General).

## Status

Early days. Layer8 has its own assembly names, namespaces and console IDs, so its menus and dialogs no longer clash with ConfigMgr Quick Tools. Layer8 ships only its own DLLs, so the two never share a file in the console's bin folder. The one overlap left: both replace the device properties form, so with both installed, whichever was installed last supplies those tabs. Automatic update checks have been removed.

## Logs

Layer8 writes a CMTrace-format log to `%LOCALAPPDATA%\Layer8\Logs\Layer8.log`. Each console session starts with a line showing the Layer8, console and .NET versions. To get more detail, set the log level to `Debug` (it's picked up within a few seconds, no console restart):

```
reg add HKCU\SOFTWARE\Layer8 /v LogLevel /d Debug /f
```

Use `Debug`, `Info` (the default), `Warn` or `Error`.

The log rolls over to `Layer8.lo_` at 2 MB, the same way ConfigMgr's own logs do.

## Credits and license

Based on ConfigMgr Quick Tools by Peter Szalatnay. Released under the MIT license; see [LICENSE](LICENSE).
