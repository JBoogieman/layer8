using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace Layer8.Device
{
    /// <summary>What the Health Dashboard flags on a device. Each has a tile; a device can have several.</summary>
    internal enum HealthIssue
    {
        RestartPending,
        LowDisk,
        BitLockerOff,
        Defender,
        MissingUpdates,
        Client,
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
        public InfoCell SignedIn, Uptime, DiskFree, Restart, BitLocker, Defender, Updates, Client;

        /// <summary>The issues as the Issues column shows them, in the tiles' order. Empty when there are none.</summary>
        public string IssueText
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (HealthIssue issue in Enum.GetValues(typeof(HealthIssue)))
                {
                    if (!Issues.Contains(issue)) continue;
                    parts.Add(issue == HealthIssue.CouldNotCheck ? HealthCheck.Name(issue) + ": " + string.Join(", ", Unread) : HealthCheck.Name(issue));
                }
                return string.Join(", ", parts);
            }
        }
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
                case HealthIssue.MissingUpdates: return "Missing updates";
                case HealthIssue.Client: return "Client";
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
        public static HealthReport Check(string host, int lowDiskGB, int longUptimeDays)
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
