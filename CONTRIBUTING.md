# Contributing to Layer8

Thanks for wanting to help. Layer8 is a spare-time project, so the way in is simple: talk first, keep changes small, and test them in a real console.

## A spare-time project

Layer8 is built by one person in spare time, for fun. Help is welcome, and so is "no thanks, that's not for me":

- Issues and pull requests get read, but replies come when they come. There's no promised support, response time or release date.
- What gets merged, and when, is the maintainer's call. A no is about fit, not about you.
- Layer8 is provided as is, without warranty (see [LICENSE](LICENSE)). It acts with your ConfigMgr and Active Directory rights, so try each release in a lab or on a small collection first.

## Before you write code

**Open an issue first** for anything bigger than a typo, and say what it would save an admin. That way we agree on the shape before you spend an evening on it. The [roadmap](docs/ROADMAP.md) lists what's already planned.

**Found a security problem?** Don't open an issue. Use **Report a vulnerability** on the Security tab ([SECURITY.md](SECURITY.md)).

## What a good change looks like

- **Small.** One fix or one feature per pull request, in commits that each do one thing and say why.
- **Safe by default.** Reading is fine; anything that changes a device, the site or Active Directory says so and asks first. Passwords and recovery keys never go to the log, and never stay on the clipboard.
- **Careful with what comes from outside.** Device names, WMI values, file names from a device or a vendor catalog, and anything typed are checked before they go into a path, a query or a command line. `Layer8/Utility.cs` has the helpers (`ComputerName`, `IsInside`, `SafeName`, `IsPlainFileName`, `WqlText`, `LoadXml`).
- **Plain words.** Menu items, messages and docs say what happens in everyday language, without jargon.
- **Like the code around it.** .NET Framework 4.8, C# 7, WinForms. Every console entry point goes through `SafeAction`.

## Building and trying it

You need the ConfigMgr console and Visual Studio with .NET desktop development. The [README](README.md#building) has the steps, and [INSTALL.md](docs/INSTALL.md) covers installing a test build in a lab console.

## Testing

Nothing ships untested, and every build re-runs the old tests, not only the new one:

1. Run `tools\Test-Layer8Regression.ps1` on a lab with a console and at least one client. It prints PASS or FAIL for each check, and its Changes section names the console steps your change touches.
2. Do those steps from [REGRESSION.md](docs/REGRESSION.md).
3. Say in the pull request what you tested and on what (ConfigMgr version, Windows on the devices).

If your change adds something the script can check, add a check for it.

## Pull requests

Target the `next` branch, not `master`. `master` only moves for releases. Fill in the template, and expect questions: they're about making it fit, not about whether you're welcome.

## License

Layer8 is MIT licensed. By sending a pull request you confirm the contribution is yours to give (you wrote it, or you have the right to share it), and you agree it's released under the same MIT license.
