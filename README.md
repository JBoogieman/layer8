# Layer8

Open-source right-click actions for the ConfigMgr (MECM) console. Because it's always layer 8.

Layer8 starts from [ConfigMgr Quick Tools](https://github.com/Flowman/ConfigMgr-Quick-Tools) by Peter Szalatnay and builds on it. It's an independent project and isn't affiliated with Microsoft or Recast Software.

## What's in it

- Client actions on devices and collections
- Extended device properties: update compliance, client health, collection membership, Dell warranty
- Collection management: add resources from a list
- Driver Manager: grabber, importer, Dell and HP driver packs
- LAPS password lookup
- Software update clean-up and phased deployments

## Building

1. Install the ConfigMgr console and Visual Studio with the .NET desktop development workload (.NET Framework 4.8).
2. Open `ConfigMgr.QuickTools.sln` and build.

Console references resolve automatically (see `Directory.Build.props`). If your console is installed somewhere unusual, build with `-p:ConfigMgrConsoleBin=<path to AdminConsole\bin>`. The WiX installer project needs WiX Toolset v3 and is being reworked.

## Status

Early days. The rebrand is happening in stages, so the code still uses the Quick Tools project and namespace names for now. Automatic update checks have been removed.

## Credits and license

Based on ConfigMgr Quick Tools by Peter Szalatnay. Released under the MIT license; see [LICENSE](LICENSE).
