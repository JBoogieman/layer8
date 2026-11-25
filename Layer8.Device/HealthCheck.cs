using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Layer8.Device
{
    /// <summary>What the Health Dashboard flags on a device. Each has a tile; a device can have several.</summary>
    internal enum HealthIssue
    {
        RestartPending,
        LowDisk,
        BitLockerOff,
        Defender,
        /// <summary>Windows wrote a crash dump lately: read from the minidump folder, not the event log.</summary>
        BlueScreen,
        MissingUpdates,
        /// <summary>The client's last software update scan finished too long ago, or it has never finished one.</summary>
        NotScanning,
        /// <summary>A workstation on a Windows build older than the one chosen in Layer8 Settings. Servers don't count.</summary>
        WindowsBehind,
        Client,
        /// <summary>The site hasn't heard from its client lately. Read from the site (SiteContact), not the device.</summary>
        NotReporting,
        LongUptime,
        /// <summary>One of the checks couldn't be read; the report's Unread list says which, and why.</summary>
        CouldNotCheck,
    }

    /// <summary>One device's health: a cell for each of the dashboard's columns, and the issues found.</summary>
    internal sealed class HealthReport
    {
        public readonly HashSet<HealthIssue> Issues = new HashSet<HealthIssue>();
        /// <summary>The checks that couldn't be read, as "BitLocker (access denied)".</summary>
        public readonly List<string> Unread = new List<string>();
        public InfoCell SignedIn, Uptime, DiskFree, Restart, BitLocker, Defender, BlueScreen, Windows, Updates, UpdateScan, Client;
        /// <summary>
        /// A server or domain controller (Win32_OperatingSystem.ProductType 2 or 3); null when Windows couldn't be
        /// read. A fix from the dashboard that restarts devices leaves out servers, and devices it can't tell about.
        /// </summary>
        public bool? IsServer;
        /// <summary>No ConfigMgr client on it (no CcmExec, or CcmExec with no client): Health Check and Repair can't help.</summary>
        public bool NoClient;
        /// <summary>Whether anyone is signed in, for a restart's question; null when it couldn't be read.</summary>
        public bool? SomeoneSignedIn;

        /// <summary>"Couldn't check: BitLocker (access denied)", as the Issues column shows it.</summary>
        public string UnreadText => HealthCheck.Name(HealthIssue.CouldNotCheck) + ": " + string.Join(", ", Unread);

        /// <summary>The issues as the Issues column shows them, in the tiles' order. Empty when there are none.</summary>
        public string IssueText
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (HealthIssue issue in Enum.GetValues(typeof(HealthIssue)))
                {
                    if (!Issues.Contains(issue)) continue;
                    parts.Add(issue == HealthIssue.CouldNotCheck ? UnreadText : HealthCheck.Name(issue));
                }
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>
    /// What the site last heard from a device's client. Read from the site, not the device, so it's there for
    /// offline devices too.
    /// </summary>
    internal sealed class SiteContact
    {
        public bool IsClient;
        /// <summary>When its client last asked the site for policy (every hour by default); null if it never has.</summary>
        public DateTime? LastPolicyRequest;
        /// <summary>A client that hasn't asked for policy in the Not reporting tile's number of days, or ever.</summary>
        public bool NotReporting;
        /// <summary>
        /// The last time the site heard from it at all: its last policy request, heartbeat, client check or
        /// activity, or the fast channel seeing it come online or drop off. Null if never.
        /// </summary>
        public DateTime? LastSeen;
        /// <summary>When its last heartbeat (discovery data) came in, which carries the Windows build; null if never.</summary>
        public DateTime? LastHeartbeat;
        /// <summary>Who was last signed in, as the site last heard it; null if it doesn't know.</summary>
        public string LastUser;
        /// <summary>
        /// The site's pending-restart flags (ClientState), as of the last time the fast channel heard from it: 0 for
        /// none, 1 ConfigMgr, 2 a file rename, 4 Windows Update, 8 adding or removing a Windows feature. Null if unknown.
        /// </summary>
        public int? RestartFlags;
        public string ClientVersion;
        /// <summary>The last client check's result: 1 passed, 2 failed, 3 no results, 0 unknown.</summary>
        public int ClientCheck;
    }

    /// <summary>
    /// What the site's records hold about an offline device beyond its check-ins: C: from its last hardware
    /// inventory, and the Windows build from its device record. Read only for devices that didn't answer.
    /// </summary>
    internal sealed class SiteInventory
    {
        public double? DiskFree, DiskSize;
        /// <summary>When the hardware inventory that read C: ran, on the device's own clock.</summary>
        public DateTime? DiskAsOf;
        public Version Build;
    }

    /// <summary>
    /// Reads one device's health for the Health Dashboard: who's signed in, uptime, free space on C:, recent blue
    /// screens, the Windows build, the ConfigMgr client, a pending restart, BitLocker on C:, Microsoft Defender,
    /// deployed updates that are missing and the last software update scan. Read
    /// only: nothing on the device changes, and BitLocker recovery passwords are never read.
    ///
    /// The checks ask the same code the single-purpose tools use (Check Pending Restart, BitLocker Status, Defender
    /// Status, Quick Status), so the dashboard and those tools can't disagree.
    /// </summary>
    internal static class HealthCheck
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(HealthCheck));
        private const double GB = 1024d * 1024 * 1024;
        private const int RpcServerUnavailable = unchecked((int)0x800706BA);
        private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        /// <summary>A step slower than this is logged with its time at Info, so a slow check shows without Debug logging.</summary>
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Signatures older than this many days count as out of date: Windows' own default for when Defender
        /// reports them as out of date.
        /// </summary>
        internal const int DefenderSignatureDays = 7;

        /// <summary>An issue's name, as its tile and the Issues column show it.</summary>
        internal static string Name(HealthIssue issue)
        {
            switch (issue)
            {
                case HealthIssue.RestartPending: return "Restart pending";
                case HealthIssue.LowDisk: return "Low disk";
                case HealthIssue.BitLockerOff: return "BitLocker off";
                case HealthIssue.Defender: return "Defender";
                case HealthIssue.BlueScreen: return "Blue screen";
                case HealthIssue.MissingUpdates: return "Missing updates";
                case HealthIssue.NotScanning: return "Not scanning";
                case HealthIssue.WindowsBehind: return "Windows behind";
                case HealthIssue.Client: return "Client";
                case HealthIssue.NotReporting: return "Not reporting";
                case HealthIssue.LongUptime: return "Long uptime";
                default: return "Couldn't check";
            }
        }

        /// <summary>
        /// Checks one device. Connecting to its WMI is the one step that fails the whole device (offline, blocked,
        /// access denied), and so does the device dropping off the network partway through. Apart from that each
        /// check stands alone: one that can't be read shows "?", is named in the Issues column, and the rest still
        /// answer.
        /// </summary>
        public static HealthReport Check(string host, int lowDiskGB, int longUptimeDays, int blueScreenDays, Version windowsBuild, int notScanningDays)
        {
            HealthReport report = new HealthReport();
            Stopwatch connecting = Stopwatch.StartNew();
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            LogTime(host, "Connecting", connecting.Elapsed);

            // The registry provider answers both the restart flags and provisioning mode: connect once, when first needed.
            ManagementScope registry = null;
            Func<ManagementScope> registryScope = () => registry ?? (registry = Utility.GetWMIScope(host, "default"));
            // Set by the client check: ConfigMgr's own restart flag and the update list need a running client.
            ManagementScope clientSdk = null;

            report.SignedIn = Read(report, host, "Signed in", () =>
            {
                List<string> users = ClientActions.SignedInUsers(cimv2);
                report.SomeoneSignedIn = users.Count > 0;
                return new InfoCell(users.Count == 0 ? "No one" : string.Join(", ", users));
            });

            report.Uptime = Read(report, host, "Uptime", () =>
            {
                // The boot time first: the uptime counter's first read on a device took 43 seconds on the site server.
                TimeSpan? uptime = ClientActions.Uptime(cimv2, counterFirst: false);
                if (!uptime.HasValue) throw new InvalidOperationException("no uptime counter or boot time");
                if (uptime.Value.TotalDays >= longUptimeDays) report.Issues.Add(HealthIssue.LongUptime);
                return new InfoCell(ClientActions.FormatUptime(uptime.Value), uptime.Value);
            });

            report.DiskFree = Read(report, host, "Disk", () =>
            {
                using (ManagementObject disk = Utility.GetFirstWMIInstance(cimv2, "SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DeviceID = 'C:'"))
                {
                    if (disk == null) return new InfoCell("No C: drive");
                    double free = Convert.ToDouble(disk["FreeSpace"] ?? 0d);
                    double size = Convert.ToDouble(disk["Size"] ?? 0d);
                    if (free < lowDiskGB * GB) report.Issues.Add(HealthIssue.LowDisk);
                    string text = Utility.FormatBytes(free, "0.#") + (size > 0 ? " of " + Utility.FormatBytes(size, "0") : string.Empty);
                    return new InfoCell(text, free);
                }
            });

            report.BlueScreen = Read(report, host, "Blue screen", () =>
            {
                List<DateTime> dumps = RecentCrashDumps(cimv2, blueScreenDays);
                if (dumps.Count == 0) return new InfoCell("None", DateTime.MinValue);
                report.Issues.Add(HealthIssue.BlueScreen);
                DateTime newest = dumps.Max();
                string code = StopCode(cimv2, newest);
                string text = (dumps.Count == 1 ? string.Empty : dumps.Count + ", last ") + newest.ToString("g") + (code == null ? string.Empty : " (" + code + ")");
                return new InfoCell(text, newest);
            });

            report.Windows = Read(report, host, "Windows", () =>
            {
                using (ManagementObject os = Utility.GetFirstWMIInstance(cimv2, "SELECT Caption, BuildNumber, ProductType FROM Win32_OperatingSystem"))
                {
                    if (os == null) throw new InvalidOperationException("no Windows build number");
                    object productType = os["ProductType"];
                    report.IsServer = productType == null ? (bool?)null : Convert.ToUInt32(productType) != 1;
                    if (!int.TryParse(os["BuildNumber"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out int build))
                        throw new InvalidOperationException("no Windows build number");
                    // The update revision (the .4652 in 26100.4652) and the marketing version (24H2) are only in the
                    // registry. Windows 10 before 20H2 has ReleaseId (1909) where later ones have DisplayVersion.
                    object revisionValue = SystemInfoReader.RegistryValue(registryScope(), "GetDWORDValue", CurrentVersionKey, "UBR");
                    Version version = new Version(build, revisionValue == null ? 0 : (int)Convert.ToUInt32(revisionValue));
                    string buildText = revisionValue == null ? build.ToString(CultureInfo.InvariantCulture) : version.ToString();

                    // ProductType 1 is a workstation; 2 and 3 are domain controllers and servers, whose builds follow
                    // their own line (Server 2022 is 20348) and aren't held to a workstation build.
                    if (report.IsServer == true)
                        return new InfoCell(SystemInfoReader.Join(", ", ServerName(os["Caption"] as string), buildText), version);

                    string release = SystemInfoReader.RegistryValue(registryScope(), "GetStringValue", CurrentVersionKey, "DisplayVersion") as string
                        ?? SystemInfoReader.RegistryValue(registryScope(), "GetStringValue", CurrentVersionKey, "ReleaseId") as string;
                    if (version < windowsBuild) report.Issues.Add(HealthIssue.WindowsBehind);
                    return new InfoCell(SystemInfoReader.Join(", ", release, buildText), version);
                }
            });

            report.Client = Read(report, host, "Client", () =>
            {
                // The service first: with CcmExec stopped, the client's WMI classes have no one to answer for them.
                string state;
                using (ManagementObject service = Utility.GetFirstWMIInstance(cimv2, "SELECT State FROM Win32_Service WHERE Name = 'CcmExec'"))
                {
                    if (service == null)
                    {
                        report.Issues.Add(HealthIssue.Client);
                        report.NoClient = true;
                        return new InfoCell("Not installed");
                    }
                    state = service["State"] as string ?? "in an unknown state";
                }
                if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    report.Issues.Add(HealthIssue.Client);
                    return new InfoCell("Service " + state.ToLowerInvariant());
                }

                string version;
                try
                {
                    using (ManagementObject client = Utility.GetFirstWMIInstance(Utility.GetWMIScope(host, "ccm"), "SELECT ClientVersion FROM SMS_Client"))
                    {
                        version = client?["ClientVersion"] as string;
                    }
                    clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
                }
                catch (ManagementException ex) when (ClientActions.IsNoClient(ex))
                {
                    // CcmExec runs but there's no client: a site server's management point runs CcmExec too, with
                    // no client installed. A client whose WMI classes have gone reads the same; Client Info and
                    // Repair Client tell the two apart.
                    report.Issues.Add(HealthIssue.Client);
                    report.NoClient = true;
                    return new InfoCell("Not installed");
                }

                if (ClientActions.ProvisioningModeOn(registryScope()))
                {
                    report.Issues.Add(HealthIssue.Client);
                    return new InfoCell("Provisioning mode");
                }
                return new InfoCell(version ?? "Running", Version.TryParse(version ?? string.Empty, out Version parsed) ? parsed : null);
            });

            report.Restart = Read(report, host, "Restart", () =>
            {
                List<string> reasons = new List<string>();
                if (clientSdk != null)
                {
                    string configMgr = ClientActions.ConfigMgrRestartReason(clientSdk);
                    if (configMgr != null) reasons.Add(configMgr);
                }
                reasons.AddRange(ClientActions.WindowsRestartReasons(registryScope()));
                if (reasons.Count == 0) return new InfoCell("No");
                report.Issues.Add(HealthIssue.RestartPending);
                return new InfoCell(string.Join("; ", reasons));
            });

            report.BitLocker = Read(report, host, "BitLocker", () =>
            {
                ClientActions.BitLockerState state;
                try
                {
                    state = ClientActions.ReadBitLocker(host);
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
                {
                    // No BitLocker on this edition, or a server without the BitLocker feature.
                    return new InfoCell("Not available");
                }
                if (state == null) return new InfoCell("C: not encryptable");
                if (state.Protection == 0)
                {
                    report.Issues.Add(HealthIssue.BitLockerOff);
                    // Encrypted but not protecting: protection was suspended (a BIOS or feature update, usually).
                    if (state.Conversion == "fully encrypted") return new InfoCell("Suspended (encrypted)");
                    return new InfoCell(state.Conversion.Length == 0 || state.Conversion == "fully decrypted" ? "Off" : "Off, " + state.Conversion);
                }
                if (state.Protection == 1)
                {
                    return new InfoCell(state.Conversion.Length == 0 || state.Conversion == "fully encrypted" ? "On" : "On, " + state.Conversion);
                }
                return new InfoCell("Unknown (locked)");
            });

            report.Defender = Read(report, host, "Defender", () =>
            {
                ClientActions.DefenderState state;
                try
                {
                    state = ClientActions.ReadDefender(host);
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
                {
                    return new InfoCell("Not installed");
                }
                if (state == null) return new InfoCell("No status");
                // Another antivirus is in charge, and Defender stands back on purpose: nothing to flag.
                if (state.IsPassive) return new InfoCell("Passive");

                List<string> problems = new List<string>();
                if (!state.AntivirusOn) problems.Add("antivirus off");
                if (!state.RealTimeOn) problems.Add("real-time off");
                if (state.SignatureAge > DefenderSignatureDays) problems.Add("signatures " + state.SignatureAge + " days old");
                if (problems.Count == 0) return new InfoCell("OK");
                report.Issues.Add(HealthIssue.Defender);
                string text = string.Join(", ", problems);
                return new InfoCell(char.ToUpperInvariant(text[0]) + text.Substring(1));
            });

            report.Updates = Read(report, host, "Updates", () =>
            {
                if (clientSdk == null) return new InfoCell("No client");
                // Deployed and missing, counted by KB like List Missing Updates (the store can list one KB twice).
                List<string> missing = new List<string>();
                foreach (ManagementObject update in Utility.SearchWMIToList(clientSdk, "SELECT ArticleID, Name FROM CCM_SoftwareUpdate WHERE ComplianceState = 0"))
                {
                    using (update)
                    {
                        string key = ClientActions.UpdateKey(update["ArticleID"] as string, update["Name"] as string);
                        if (!missing.Contains(key, StringComparer.OrdinalIgnoreCase)) missing.Add(key);
                    }
                }
                if (missing.Count == 0) return new InfoCell("None", 0);
                report.Issues.Add(HealthIssue.MissingUpdates);
                string shown = string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? ", ..." : string.Empty);
                return new InfoCell(missing.Count + ": " + shown, missing.Count);
            });

            report.UpdateScan = Read(report, host, "Update scan", () =>
            {
                if (clientSdk == null) return new InfoCell("No client");
                // Client settings can turn software updates off: such a client never scans, on purpose.
                if (!SoftwareUpdatesEnabled(host)) return new InfoCell("Turned off");
                DateTime? last;
                try
                {
                    last = ClientActions.LastUpdateScan(Utility.GetWMIScope(host, @"ccm\ScanAgent"));
                }
                catch (ManagementException ex) when (ClientActions.IsNoClient(ex))
                {
                    return new InfoCell("Turned off");
                }
                if (!last.HasValue)
                {
                    report.Issues.Add(HealthIssue.NotScanning);
                    return new InfoCell("Never", DateTime.MinValue);
                }
                TimeSpan age = Age(last.Value);
                if (age.TotalDays >= notScanningDays) report.Issues.Add(HealthIssue.NotScanning);
                return new InfoCell(FormatAge(age), last.Value);
            });

            return report;
        }

        /// <summary>
        /// Whether the client's settings turn its software updates agent on (CCM_SoftwareUpdatesClientConfig in its
        /// policy). True when it has no such policy, which the agent's own default is.
        /// </summary>
        private static bool SoftwareUpdatesEnabled(string host)
        {
            using (ManagementObject config = Utility.GetFirstWMIInstance(Utility.GetWMIScope(host, @"ccm\Policy\Machine\ActualConfig"), "SELECT Enabled FROM CCM_SoftwareUpdatesClientConfig"))
            {
                return config == null || !(config["Enabled"] is bool enabled) || enabled;
            }
        }

        /// <summary>"Server 2022" from "Microsoft Windows Server 2022 Datacenter"; "Server" if the caption has no year.</summary>
        internal static string ServerName(string caption)
        {
            Match match = Regex.Match(caption ?? string.Empty, @"Server \d{4}( R2)?");
            return match.Success ? match.Value : "Server";
        }

        /// <summary>
        /// When the crash dumps in C:\Windows\Minidump written in the last days were written: one for each blue screen.
        /// Listing the folder takes milliseconds, where searching the System event log for crashes takes seconds and
        /// grows with the log. Windows keeps the dumps until Disk Cleanup or Storage Sense removes them, and writes
        /// none if crash dumps are turned off, so a blue screen can go unseen here.
        /// </summary>
        internal static List<DateTime> RecentCrashDumps(ManagementScope cimv2, int days)
        {
            DateTime since = DateTime.Now.AddDays(-days);
            List<DateTime> written = new List<DateTime>();
            foreach (ManagementObject file in Utility.SearchWMIToList(cimv2,
                @"SELECT LastModified FROM CIM_DataFile WHERE Drive = 'C:' AND Path = '\\Windows\\Minidump\\' AND Extension = 'dmp'"))
            {
                using (file)
                {
                    if (!(file["LastModified"] is string stamp)) continue;
                    DateTime when = ManagementDateTimeConverter.ToDateTime(stamp);
                    if (when >= since) written.Add(when);
                }
            }
            return written;
        }

        /// <summary>
        /// The stop code of the blue screen whose dump was written at the given time, as 0x000000D1, from the event
        /// Windows writes after restarting from it (WER-SystemErrorReporting 1001: "The computer has rebooted from a
        /// bugcheck. The bugcheck was: 0x000000d1 (...)"). Asked only of devices with a recent dump, since it searches
        /// the event log. Null when the event isn't there or can't be read: the dump's date stands on its own.
        /// </summary>
        internal static string StopCode(ManagementScope cimv2, DateTime dumpWritten)
        {
            try
            {
                string from = ManagementDateTimeConverter.ToDmtfDateTime(dumpWritten.AddMinutes(-10));
                foreach (ManagementObject entry in Utility.SearchWMIToList(cimv2,
                    "SELECT InsertionStrings FROM Win32_NTLogEvent WHERE Logfile = 'System' AND EventCode = 1001 AND TimeGenerated >= '" + from + "'"))
                {
                    using (entry)
                    {
                        string first = (entry["InsertionStrings"] as string[])?.FirstOrDefault();
                        Match match = first == null ? Match.Empty : Regex.Match(first, @"^\s*0x([0-9A-Fa-f]{1,8})\b");
                        if (match.Success) return "0x" + match.Groups[1].Value.ToUpperInvariant().PadLeft(8, '0');
                    }
                }
            }
            catch (ManagementException ex)
            {
                log.DebugFormat("No stop code for the dump written {0}: {1}", dumpWritten, ex.Message);
            }
            catch (COMException ex)
            {
                log.DebugFormat("No stop code for the dump written {0}: {1}", dumpWritten, ex.Message);
            }
            return null;
        }

        private const int IdsPerQuery = 300;

        /// <summary>
        /// When the site last heard from each device's client, by ResourceID: SMS_CombinedDeviceResources, the data
        /// behind the console's Client Activity, 300 devices a query. Runs on a background thread, and stops between
        /// queries when canceled. Devices the site has no record of are left out.
        /// </summary>
        internal static Dictionary<int, SiteContact> ReadSiteContact(ConnectionManagerBase connection, IList<int> ids, int notReportingDays, CancellationToken token)
        {
            Dictionary<int, SiteContact> contact = new Dictionary<int, SiteContact>();
            for (int start = 0; start < ids.Count; start += IdsPerQuery)
            {
                token.ThrowIfCancellationRequested();
                string idList = string.Join(",", ids.Skip(start).Take(IdsPerQuery));
                using (IResultObject results = connection.QueryProcessor.ExecuteQuery(
                    "SELECT ResourceID, IsClient, LastPolicyRequest, LastDDR, LastClientCheckTime, LastActiveTime, CNLastOnlineTime, CNLastOfflineTime, " +
                    "UserName, LastLogonUser, ClientState, ClientVersion, ClientCheckPass FROM SMS_CombinedDeviceResources WHERE ResourceID IN (" + idList + ")"))
                {
                    foreach (IResultObject device in results)
                    {
                        SiteContact site = new SiteContact
                        {
                            IsClient = device["IsClient"].ObjectValue != null && device["IsClient"].BooleanValue,
                            LastPolicyRequest = SiteUtc(device, "LastPolicyRequest"),
                            LastHeartbeat = SiteUtc(device, "LastDDR"),
                            LastUser = SiteText(device, "LastLogonUser") ?? SiteText(device, "UserName"),
                            RestartFlags = device["ClientState"].ObjectValue == null ? (int?)null : device["ClientState"].IntegerValue,
                            ClientVersion = SiteText(device, "ClientVersion"),
                            ClientCheck = device["ClientCheckPass"].ObjectValue == null ? 0 : device["ClientCheckPass"].IntegerValue,
                        };
                        site.LastSeen = new[]
                        {
                            site.LastPolicyRequest, site.LastHeartbeat, SiteUtc(device, "LastClientCheckTime"), SiteUtc(device, "LastActiveTime"),
                            SiteUtc(device, "CNLastOnlineTime"), SiteUtc(device, "CNLastOfflineTime"),
                        }.Where(time => time.HasValue).Select(time => time.Value).DefaultIfEmpty().Max() is DateTime seen && seen.Year >= 2000 ? seen : (DateTime?)null;
                        site.NotReporting = site.IsClient &&
                            (!site.LastPolicyRequest.HasValue || Age(site.LastPolicyRequest.Value).TotalDays >= notReportingDays);
                        contact[device["ResourceID"].IntegerValue] = site;
                    }
                }
            }
            return contact;
        }

        /// <summary>
        /// A time from the site's record of a device's client, in UTC. The site keeps these in UTC but hands them back
        /// unmarked; read as local time they'd sit hours in the future (west of UTC) and every client would look
        /// "just now". An empty date can come back as year 1 or 1970 instead of null.
        /// </summary>
        private static DateTime? SiteUtc(IResultObject device, string property)
        {
            if (device[property].ObjectValue == null) return null;
            DateTime when = device[property].DateTimeValue;
            return when.Year >= 2000 ? DateTime.SpecifyKind(when, DateTimeKind.Utc) : (DateTime?)null;
        }

        /// <summary>
        /// A time from a device's hardware inventory: on the device's own clock, not UTC. On DEV02 a scan run at
        /// 3:21 PM came back as 3:21 PM here, while SMS_CombinedDeviceResources' copy of the same scan came back in UTC.
        /// </summary>
        private static DateTime? InventoryTime(IResultObject row, string property)
        {
            if (row[property].ObjectValue == null) return null;
            DateTime when = row[property].DateTimeValue;
            return when.Year >= 2000 ? DateTime.SpecifyKind(when, DateTimeKind.Local) : (DateTime?)null;
        }

        private static string SiteText(IResultObject row, string property)
        {
            string text = row[property].ObjectValue == null ? null : row[property].StringValue;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <summary>
        /// What the site's records hold about devices that didn't answer, by ResourceID: C: from the last hardware
        /// inventory (SMS_G_System_LOGICAL_DISK, in MB) and the Windows build with its update revision from the device
        /// record (SMS_R_System's BuildExt, "10.0.26300.9974"), 300 devices a query. Runs on a background thread.
        /// </summary>
        internal static Dictionary<int, SiteInventory> ReadSiteInventory(ConnectionManagerBase connection, IList<int> ids, CancellationToken token)
        {
            Dictionary<int, SiteInventory> inventory = new Dictionary<int, SiteInventory>();
            SiteInventory For(int id) => inventory.TryGetValue(id, out SiteInventory found) ? found : inventory[id] = new SiteInventory();
            for (int start = 0; start < ids.Count; start += IdsPerQuery)
            {
                token.ThrowIfCancellationRequested();
                string idList = string.Join(",", ids.Skip(start).Take(IdsPerQuery));
                using (IResultObject results = connection.QueryProcessor.ExecuteQuery(
                    "SELECT ResourceID, FreeSpace, Size, TimeStamp FROM SMS_G_System_LOGICAL_DISK WHERE DeviceID = 'C:' AND ResourceID IN (" + idList + ")"))
                {
                    foreach (IResultObject disk in results)
                    {
                        SiteInventory found = For(disk["ResourceID"].IntegerValue);
                        if (disk["FreeSpace"].ObjectValue != null) found.DiskFree = disk["FreeSpace"].LongValue * 1048576d;
                        if (disk["Size"].ObjectValue != null) found.DiskSize = disk["Size"].LongValue * 1048576d;
                        found.DiskAsOf = InventoryTime(disk, "TimeStamp");
                    }
                }
                token.ThrowIfCancellationRequested();
                using (IResultObject results = connection.QueryProcessor.ExecuteQuery(
                    "SELECT ResourceID, BuildExt FROM SMS_R_System WHERE ResourceID IN (" + idList + ")"))
                {
                    foreach (IResultObject device in results)
                    {
                        // "10.0.26300.9974": the dashboard shows the build and revision, as the device's own check does.
                        string[] parts = (SiteText(device, "BuildExt") ?? string.Empty).Split('.');
                        if (parts.Length == 4 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int build) &&
                            int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
                        {
                            For(device["ResourceID"].IntegerValue).Build = new Version(build, revision);
                        }
                    }
                }
            }
            return inventory;
        }

        /// <summary>The site's pending-restart flags in the device check's words: "No", or why ("ConfigMgr; Windows Update").</summary>
        internal static string RestartText(int flags)
        {
            if (flags == 0) return "No";
            List<string> reasons = new List<string>();
            if ((flags & 1) != 0) reasons.Add("ConfigMgr");
            if ((flags & 2) != 0) reasons.Add("File rename");
            if ((flags & 4) != 0) reasons.Add("Windows Update");
            if ((flags & 8) != 0) reasons.Add("Windows feature");
            return reasons.Count == 0 ? "Pending" : string.Join("; ", reasons);
        }

        /// <summary>How long ago a time was, whether it came back as local time or UTC. Never negative.</summary>
        internal static TimeSpan Age(DateTime when)
        {
            TimeSpan age = (when.Kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now) - when;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }

        /// <summary>"just now", "40m ago", "5h ago", "3d ago".</summary>
        internal static string FormatAge(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + "m ago";
            if (age.TotalHours < 48) return (int)age.TotalHours + "h ago";
            return (int)age.TotalDays + "d ago";
        }

        /// <summary>
        /// Runs one check. If it can't be read, the report says which and why, and the cell shows "?". The device
        /// stopping answering altogether (RPC server unavailable) ends the whole check instead: every check after it
        /// would wait for the same timeout.
        /// </summary>
        private static InfoCell Read(HealthReport report, string host, string check, Func<InfoCell> read)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                if (root is COMException rpc && rpc.ErrorCode == RpcServerUnavailable) throw;
                string why = root is UnauthorizedAccessException || (root is ManagementException denied && denied.ErrorCode == ManagementStatus.AccessDenied)
                    ? "access denied"
                    : root.Message.Trim().TrimEnd('.').ToLowerInvariant();
                report.Unread.Add(check + " (" + why + ")");
                report.Issues.Add(HealthIssue.CouldNotCheck);
                log.Warn(string.Format("Health Dashboard on {0}: couldn't read {1}", host, check), root);
                return new InfoCell("?");
            }
            finally
            {
                LogTime(host, check, watch.Elapsed);
            }
        }

        /// <summary>How long one step took on one device: every step at Debug, and at Info when it's slow.</summary>
        private static void LogTime(string host, string step, TimeSpan took)
        {
            if (took >= SlowStep)
                log.InfoFormat("Health Dashboard on {0}: {1} took {2:0.0} seconds", host, step, took.TotalSeconds);
            else if (log.IsDebugEnabled)
                log.DebugFormat("Health Dashboard on {0}: {1} took {2} ms", host, step, (long)took.TotalMilliseconds);
        }
    }
}
