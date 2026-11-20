using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
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
        public InfoCell SignedIn, Uptime, DiskFree, Restart, BitLocker, Defender, BlueScreen, Updates, Client;

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
    }

    /// <summary>
    /// Reads one device's health for the Health Dashboard: who's signed in, uptime, free space on C:, the ConfigMgr
    /// client, a pending restart, BitLocker on C:, Microsoft Defender and deployed updates that are missing. Read
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
        public static HealthReport Check(string host, int lowDiskGB, int longUptimeDays, int blueScreenDays)
        {
            HealthReport report = new HealthReport();
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");

            // The registry provider answers both the restart flags and provisioning mode: connect once, when first needed.
            ManagementScope registry = null;
            Func<ManagementScope> registryScope = () => registry ?? (registry = Utility.GetWMIScope(host, "default"));
            // Set by the client check: ConfigMgr's own restart flag and the update list need a running client.
            ManagementScope clientSdk = null;

            report.SignedIn = Read(report, host, "Signed in", () =>
            {
                List<string> users = ClientActions.SignedInUsers(cimv2);
                return new InfoCell(users.Count == 0 ? "No one" : string.Join(", ", users));
            });

            report.Uptime = Read(report, host, "Uptime", () =>
            {
                TimeSpan? uptime = ClientActions.Uptime(cimv2);
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

            report.Client = Read(report, host, "Client", () =>
            {
                // The service first: with CcmExec stopped, the client's WMI classes have no one to answer for them.
                string state;
                using (ManagementObject service = Utility.GetFirstWMIInstance(cimv2, "SELECT State FROM Win32_Service WHERE Name = 'CcmExec'"))
                {
                    if (service == null)
                    {
                        report.Issues.Add(HealthIssue.Client);
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

            return report;
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
                    "SELECT ResourceID, IsClient, LastPolicyRequest FROM SMS_CombinedDeviceResources WHERE ResourceID IN (" + idList + ")"))
                {
                    foreach (IResultObject device in results)
                    {
                        SiteContact site = new SiteContact
                        {
                            IsClient = device["IsClient"].ObjectValue != null && device["IsClient"].BooleanValue,
                        };
                        if (device["LastPolicyRequest"].ObjectValue != null)
                        {
                            DateTime when = device["LastPolicyRequest"].DateTimeValue;
                            // An empty date can come back as year 1 or 1970 instead of null.
                            // The site keeps this time in UTC but hands it back unmarked; read as local time it
                            // would sit hours in the future (west of UTC) and every client would look "just now".
                            if (when.Year >= 2000) site.LastPolicyRequest = DateTime.SpecifyKind(when, DateTimeKind.Utc);
                        }
                        site.NotReporting = site.IsClient &&
                            (!site.LastPolicyRequest.HasValue || Age(site.LastPolicyRequest.Value).TotalDays >= notReportingDays);
                        contact[device["ResourceID"].IntegerValue] = site;
                    }
                }
            }
            return contact;
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
        }
    }
}
