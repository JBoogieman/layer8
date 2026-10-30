# Layer8

Open-source right-click actions for the ConfigMgr (MECM) console. Because it's always layer 8.

Layer8 starts from [ConfigMgr Quick Tools](https://github.com/Flowman/ConfigMgr-Quick-Tools) by Peter Szalatnay and builds on it. It's an independent project and isn't affiliated with Microsoft or Recast Software.

## What's in it

- Client actions on devices and collections
- Extended device properties: update compliance, client health, collection membership
- Collection management: add resources from a list
- Driver Manager: grabber, importer, Dell and HP driver packs
- LAPS password lookup
- Software update clean-up and phased deployments

## Building

1. Install the ConfigMgr console and Visual Studio with the .NET desktop development workload (.NET Framework 4.8).
2. Open `Layer8.sln` and build.

Console references resolve automatically (see `Directory.Build.props`). If your console is installed somewhere unusual, build with `-p:ConfigMgrConsoleBin=<path to AdminConsole\bin>`. There is no installer yet; use the developer install below.

## Loading it into the console

From an elevated PowerShell with the console closed, run `tools\Install-Layer8Dev.ps1`. It builds Layer8, copies it into the console's folders and records every file it placed; `tools\Uninstall-Layer8Dev.ps1` removes exactly those files. If PowerShell blocks the script, run it as `powershell -ExecutionPolicy Bypass -File tools\Install-Layer8Dev.ps1`.

Layer8 is an old-style console extension, so the console ignores it while **Only allow console extensions that are approved for the hierarchy** is ticked (Administration > Site Configuration > Sites > Hierarchy Settings > General).

## Status

Early days. Layer8 has its own assembly names, namespaces and console IDs, so its menus and dialogs no longer clash with ConfigMgr Quick Tools. Running both in one console still isn't supported: they put different versions of log4net.dll in the same folder, and both replace the device properties form. Automatic update checks have been removed.

## Logs

Layer8 writes a CMTrace-format log to `%LOCALAPPDATA%\Layer8\Logs\Layer8.log`. Each console session starts with a line showing the Layer8, console and .NET versions. To get more detail, change `<level value="INFO" />` to `DEBUG` in `Layer8.log4net.config` next to Layer8's DLLs; it's picked up without restarting the console.

## Credits and license

Based on ConfigMgr Quick Tools by Peter Szalatnay. Released under the MIT license; see [LICENSE](LICENSE).
