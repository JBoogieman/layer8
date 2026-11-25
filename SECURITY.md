# Security

Layer8 runs inside the ConfigMgr console as the admin using it, and reaches devices with that admin's rights. A bug in it can do whatever that admin can, so please report anything that looks exploitable privately.

## Reporting a vulnerability

Use **Report a vulnerability** on this repository's **Security** tab. Please don't open a public issue for it. Include the Layer8 version (the first line of each console session in `%LOCALAPPDATA%\Layer8\Logs\Layer8.log`) and how to reproduce it.

## What counts

- Anything that lets a device, a ConfigMgr record or a downloaded file run code on the admin's computer, or as the admin.
- LAPS passwords or BitLocker recovery keys shown to someone who shouldn't see them, or written to Layer8's log or anywhere else.
- Injection through device names, log names or other text Layer8 puts into command lines, WMI queries or LDAP filters.

## What to expect

Reports are handled in spare time. I'll reply as soon as I can, fix the serious ones first, and credit you in the release notes if you'd like. There's no guaranteed response time.

## How changes are checked

- **Every build** runs Microsoft's .NET security analyzers (the Security rules of `Microsoft.CodeAnalysis.NetAnalyzers`) inside the compile, where the ConfigMgr console's DLLs are there to read. A finding stops the build.
- **Every test build** also runs PSScriptAnalyzer's security rules and InjectionHunter over the scripts in `tools`, which build, sign and install Layer8 ([REGRESSION.md](docs/REGRESSION.md#s-security-scans)).
- **On GitHub:** CodeQL on pull requests to `master` and every week, Dependabot alerts, and secret scanning with push protection.

None of these treats a device name or other value from WMI or ConfigMgr as untrusted input, so Layer8's own checks cover that ([CONTRIBUTING.md](CONTRIBUTING.md#what-a-good-change-looks-like)).

## Supported versions

The latest release.
