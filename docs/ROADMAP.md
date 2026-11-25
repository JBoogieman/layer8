# Layer8 roadmap

Where Layer8 might go next. These are ideas, not promises: Layer8 is built in spare time, for fun, and things move when they're ready and tested. Want something on here sooner, or something that isn't? [Open an issue](https://github.com/JBoogieman/layer8/issues) and say what it would save you.

## What stays true

- **Free and open source** (MIT). No paid tier, no license key, no sign-up.
- **Nothing phones home.** Layer8 talks to your site, your devices and your Active Directory, and nothing else unless you ask it to (driver pack catalogs, say).
- **Reading is safe.** Anything that changes a device, the site or AD says so and asks first.
- **Nothing ships untested.** Every build re-runs the whole regression suite against a real ConfigMgr lab ([REGRESSION.md](REGRESSION.md), [STATUS.md](STATUS.md)).
- **Take only what you want.** Features can be left out of the cab without building anything.

## Next up

| Idea | What it would do |
|---|---|
| **Fix it from the Health Dashboard** | Act on a tile straight from the dashboard: a scan cycle for Not scanning, repair for Client, a restart (with a confirmation) for Restart pending, the right tool for BitLocker off and Defender, Online Status then a policy request for Not reporting |

## Soon-ish

| Idea | What it would do |
|---|---|
| Warranty lookup | Warranty end date per device, vendor by vendor (Lenovo first) |
| AD cleanup | Computers in AD that ConfigMgr hasn't heard from, and the reverse. A read-only report first; disable, move and delete later, behind confirmations |
| LAPS from Microsoft Entra ID | Read Windows LAPS passwords backed up to Entra ID, not just AD |
| Columns for every status tool | Online Status, BitLocker Status, Defender Status and Client Info get sortable columns like Quick Status |
| Recent sign-ins | Who was on a device, not just who is on it now |
| Share your settings | Export and import Layer8 Settings, so every admin's console matches |

## Bigger ideas

| Idea | What it would do |
|---|---|
| Fleet reports in the console | Boot times, blue screens and hardware trends by model ([layer8-reports](https://github.com/JBoogieman/layer8-reports) brought into Layer8) |
| Hardware lifecycle | Failure rates and age by model generation, to plan replacements on data |
| Driver packs on a schedule | Keep the source share current without opening a wizard, with every download still checked |
| Co-management view | Which workloads Intune owns on each device |
| A PowerShell module | Layer8's checks and actions from a script, on the same code the console uses |

## Trust and quality

| Idea | What it would do |
|---|---|
| Signed releases | A cab and MSI signed with a public code-signing certificate, so no "allow unsigned" setting is needed |
| Builds on every pull request | Automatic build and code scanning for every change |
| A nightly lab run | The regression suite on a schedule, not just before each build |
| An outside security review | A threat model, and a second pair of eyes on the code |
| Package managers | Install the MSI with winget or Chocolatey |

## Moonshots

Far-fetched on purpose. Some of these may never happen; all of them are fun to think about.

- **Ask your fleet:** type "which laptops blue-screened twice this month?" and get the answer, without writing WQL or CMPivot.
- **See trouble coming:** spot failing disks, tired batteries and crashing models before people feel it.
- **Self-healing, with you in charge:** rules like "a client that stops reporting gets repaired", run only after you approve them.
- **A fleet map:** your sites and floors, with each device's health on it, live.
- **Layer8 for Intune:** the same right-click tools for devices managed in the cloud.
- **Layer8 on the web:** the dashboard and the fixes without installing the console.

## How to help

- Try it and [open an issue](https://github.com/JBoogieman/layer8/issues) with what broke, what's missing, or what you'd love.
- Found a security problem? See [SECURITY.md](../SECURITY.md) and report it privately.
- Want to build something? Open an issue first so we can talk it through.
