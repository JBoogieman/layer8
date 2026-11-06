# Extensions.Dev

Developer-only console items. The MSI harvests `Extensions\`, not this folder, so nothing here ships.

`.\tools\Dev-Loop.ps1 -WithDevTools` copies `Layer8Probe.xml` into the console's
`XmlStorage\Extensions\Actions\<GUID>\` for every GUID in `nodes.txt`; a plain `.\tools\Dev-Loop.ps1`
removes those copies again. Right-click anything on those nodes > Layer8 Probe Selection logs the selected
objects' class and every property to `Layer8.log`.
