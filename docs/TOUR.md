# Layer8 tour

What Layer8 looks like in the ConfigMgr console. The screenshots are from a lab site (ConfigMgr 2509, two Windows 11 clients). Everything here is on a right-click menu or the ribbon; there's no separate app to open.

## Right-click menus

Right-click a device collection, **Client Actions**: the eleven standard client cycles, then Layer8's own actions. Each one runs on every device in the collection. The menu scrolls for the rest.

![A device collection's context menu, Client Actions open: the client cycles, then Restart SMS Agent Host Service, Online Status, Quick Status, Find Software, Check Pending Restart, update, policy and repair actions, Send Message, restarts and Defender](images/tour/collection-client-actions.png)

Right-click a device, **Layer8**: the tools for that one device. Its own Client Actions and Remote Tools menus are further down.

![A device's context menu, Layer8 open: Driver Grabber, Client Cache, Copy Names, Copy Device Info, Install Application, BitLocker Recovery Key, Error Lookup, LAPS Password, Layer8 Settings, System Information, Windows Security](images/tour/device-menu.png)

## One row per device

Every client action opens a window like these, with one answer per device: done, offline, blocked, or why it failed. Copy Results pastes into Excel, Export saves a file, and Run Again on Failed retries only the devices that didn't work.

**Quick Status**: who's signed in, uptime, free space on C:, IP address and client version.

![Quick Status for a collection: two devices online, one with LAB\Administrator signed in, with uptime, free space, IP address and client version](images/tour/quick-status.png)

**Check Pending Restart**

![Check Pending Restart for a collection: no restart pending on either device](images/tour/check-pending-restart.png)

## One device

**System Information**: one window instead of remoting in. It only reads; nothing on the device changes.

![System Information for a device, Overview tab: model, BIOS, Windows version and build, uptime, last restart, processor, memory, free space, IP address, battery and client version](images/tour/system-information-overview.png)

The other tabs (programs, updates, services, drivers, profiles, local administrators) work the same way: sort, filter as you type, copy or export what's shown.

![System Information, Programs tab: installed programs with version, publisher and install date](images/tour/system-information-programs.png)

**Windows Security**: Microsoft Defender, Windows Firewall and BitLocker, as the device reports them. Quick Scan starts a Defender quick scan.

![Windows Security for a device, Defender tab: mode, real-time and tamper protection, signature age, last scans, threats and Defender for Endpoint onboarding](images/tour/windows-security-defender.png)

Firewall: each profile as Windows applies it, Group Policy included, and the network it's in use on.

![Windows Security, Firewall tab: Domain, Private and Public profiles on, blocking inbound, with the Domain profile in use on the Ethernet connection](images/tour/windows-security-firewall.png)

BitLocker: each drive's protection, encryption, method and kinds of key protector, never a recovery password. Suspend pauses protection on the Windows drive for 1 to 15 restarts (for a BIOS update, say), and Resume turns it back on. This lab device isn't encrypted, so there's nothing to suspend.

![Windows Security, BitLocker tab: drive C:, protection off, not encrypted, with Suspend and Resume buttons](images/tour/windows-security-bitlocker.png)

**LAPS Password**: from Active Directory (Windows LAPS, encrypted or not, and legacy LAPS). The password stays hidden until you tick Show. Expire Now has the device change it at its next LAPS check.

![LAPS Password for a device: the account, a hidden password, when it changed and expires, and that it's stored as Windows LAPS, encrypted](images/tour/laps-password.png)

**Install Application**: the apps deployed to the device and where each one stands, with the meaning of any error code. Install starts the selected one now, like Software Center.

![Install Application for a device: three deployed apps, one failed with error 1603 (0x643), fatal error during installation](images/tour/install-application.png)

**Error Lookup**: paste a code as hex, decimal or negative and see what it means, from ConfigMgr's error catalog and Windows'.

![Error Lookup: 0x87D00324 means the application was not detected after installation completed](images/tour/error-lookup.png)

## Deployments

Monitoring > Deployments, right-click a deployment, **Layer8**: Retry Failed Devices re-runs the deployment on every device in error. Copy Failed Devices copies them with their error codes, ready to paste into Excel.

![A deployment's context menu, Layer8 open: Retry Failed Devices, Copy Failed Devices, Re-evaluate Non-compliant Devices, Error Lookup, Layer8 Settings](images/tour/deployment-menu.png)

![Copy Failed Devices: copied 2 devices in error, both 1603 (0x643), fatal error during installation](images/tour/copy-failed-devices.png)

## Collections

**Add Resources List**: paste names, one per line, and Search says which ones ConfigMgr doesn't have. OK adds the rest. Remove Resources List works the same way.

![Add Resources List: three names pasted, two found, PC-0042 isn't in ConfigMgr](images/tour/add-resources-list.png)

**Collection Evaluation Statistics** (Layer8 on the Device Collections ribbon): the last incremental evaluation, read from colleval.log on the site server, and how long each collection took.

![Collection Evaluation Statistics: evaluation start and end, time taken, and the time for each collection](images/tour/collection-evaluation-statistics.png)

## Driver packages

Software Library > Driver Packages, **Layer8** on the ribbon: the Driver Package Importer and the Dell, HP and Lenovo driver packs.

![The Layer8 ribbon menu on Driver Packages: Driver Package Importer, Dell Driver Pack, HP Driver Pack, Lenovo Driver Pack](images/tour/driver-packages-menu.png)

**Lenovo Driver Pack**: Lenovo's packs for the Windows release you pick. The models your site has come ticked, packs you already have say Downloaded, and each download is checked against Lenovo's signed catalog.

![Lenovo Driver Pack: Lenovo's packs for Windows 11 25H2, one already downloaded and one ticked to download](images/tour/lenovo-driver-pack.png)

## Settings

**Layer8 Settings** (on the Layer8 menus): how many devices to contact at once, when to ask before a big run, the quick check that each device answers, and the log level. They're saved for your Windows account.

![Layer8 Settings: 32 devices at once, ask before more than 100, check that each device answers within 2 seconds, log level Info](images/tour/layer8-settings.png)
