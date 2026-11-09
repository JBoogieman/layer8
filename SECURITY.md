# Security

Layer8 runs inside the ConfigMgr console as the admin using it, and reaches devices with that admin's rights. A bug in it can do whatever that admin can, so please report anything that looks exploitable privately.

## Reporting a vulnerability

Use **Report a vulnerability** on this repository's **Security** tab. Please don't open a public issue for it. Include the Layer8 version (the first line of each console session in `%LOCALAPPDATA%\Layer8\Logs\Layer8.log`) and how to reproduce it.

## What counts

- Anything that lets a device, a ConfigMgr record or a downloaded file run code on the admin's computer, or as the admin.
- LAPS passwords or BitLocker recovery keys shown to someone who shouldn't see them, or written to Layer8's log or anywhere else.
- Injection through device names, log names or other text Layer8 puts into command lines, WMI queries or LDAP filters.

## Supported versions

The latest release.
