using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Windows Security on a device's Layer8 menu: Microsoft Defender, Windows Firewall and BitLocker in one
    /// window, read over WMI. The BitLocker tab can suspend BitLocker on the Windows drive for a number of
    /// restarts (for a BIOS update, say) and resume it; the Defender tab can start a quick scan. Never reads a
    /// recovery password.
    /// </summary>
    public static class WindowsSecurity
    {
        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Windows Security", () => ShowCore(selectedObject));
        }

        private static void ShowCore(IResultObject selectedObject)
        {
            string name = selectedObject["Name"].StringValue;
            if (!RemoteTools.IsComputerName(name))
            {
                MessageBox.Show(name + " isn't a computer name.", "Windows Security", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SystemInfoDialog dialog = new SystemInfoDialog(name, "Windows Security"))
            {
                dialog.AddTab("Defender", "item", WindowsSecurityReader.Defender);
                dialog.AddTabButton("Quick Scan", (owner, host) =>
                    MessageBox.Show(owner, "Start a Microsoft Defender quick scan on " + host + "?\n\nIt takes a few minutes and uses some CPU and disk while it runs.",
                                    "Quick Scan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
                        ? (Func<string>)(() => WindowsSecurityReader.QuickScan(host))
                        : null);
                dialog.AddTab("Firewall", "profile", WindowsSecurityReader.Firewall);
                dialog.AddTab("BitLocker", "volume", WindowsSecurityReader.BitLocker);
                dialog.AddTabButton("Resume", (owner, host) =>
                    (Func<string>)(() => WindowsSecurityReader.ResumeBitLocker(host)));
                dialog.AddTabButton("Suspend...", (owner, host) =>
                {
                    int? restarts = AskRestarts(owner, host);
                    return restarts.HasValue ? (Func<string>)(() => WindowsSecurityReader.SuspendBitLocker(host, restarts.Value)) : null;
                });
                dialog.ShowDialog();
            }
        }

        /// <summary>How many restarts to suspend BitLocker for: 1 to 15, as Windows allows. Null when cancelled.</summary>
        private static int? AskRestarts(IWin32Window owner, string host)
        {
            string answer = "1";
            while (true)
            {
                answer = InputPrompt.Ask("Suspend BitLocker",
                    "Suspend BitLocker on " + host + "'s Windows drive for how many restarts (1 to 15)? It stays encrypted, but starts without its key protectors until then.", answer);
                if (answer == null) return null;
                if (int.TryParse(answer, NumberStyles.Integer, CultureInfo.CurrentCulture, out int restarts) && restarts >= 1 && restarts <= 15) return restarts;
                MessageBox.Show(owner, "Enter a number from 1 to 15.", "Suspend BitLocker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    /// <summary>Reads each Windows Security tab, and the BitLocker and Defender actions. Runs off the UI thread.</summary>
    internal static class WindowsSecurityReader
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(WindowsSecurityReader));
        private const string BitLockerNamespace = @"cimv2\Security\MicrosoftVolumeEncryption";

        public static InfoTable Defender(string host)
        {
            InfoTable table = new InfoTable("Item", "Value");
            ManagementScope defender;
            ManagementObject status;
            try
            {
                defender = Utility.GetWMIScope(host, @"Microsoft\Windows\Defender");
                status = SystemInfoReader.First(defender, "SELECT * FROM MSFT_MpComputerStatus");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                table.Note = "Microsoft Defender isn't on " + host;
                return table;
            }
            if (status == null)
            {
                table.Note = "Defender reports no status: another antivirus may be in charge";
                return table;
            }

            using (status)
            {
                table.Add("Mode", Text(status["AMRunningMode"]) ?? "Unknown");
                table.Add("Antivirus", OnOff(status["AntivirusEnabled"]));
                table.Add("Real-time protection", OnOff(status["RealTimeProtectionEnabled"]));
                table.Add("Behavior monitoring", OnOff(status["BehaviorMonitorEnabled"]));
                table.Add("Network inspection", OnOff(status["NISEnabled"]));
                table.Add("Tamper protection", OnOff(status["IsTamperProtected"]));

                DateTime? updated = ClientActions.ToDateTime(status["AntivirusSignatureLastUpdated"]);
                string age = Days(status["AntivirusSignatureAge"]);
                table.Add("Antivirus signatures", new InfoCell(
                    SystemInfoReader.Join(", ", Text(status["AntivirusSignatureVersion"]), updated.HasValue ? "updated " + updated.Value.ToString("g") : null, age != null ? age + " old" : null),
                    updated));
                table.Add("Engine", Text(status["AMEngineVersion"]));
                table.Add("Platform", Text(status["AMProductVersion"]));
                table.Add("Last quick scan", Scan(status["QuickScanEndTime"], status["QuickScanAge"]));
                table.Add("Last full scan", Scan(status["FullScanEndTime"], status["FullScanAge"]));
                table.Add("Needs", Needs(status["ComputerState"]));
            }

            table.Add("Threats, last 30 days", Threats(defender));

            object onboarded = SystemInfoReader.RegistryValue(host, "GetDWORDValue", @"SOFTWARE\Microsoft\Windows Advanced Threat Protection\Status", "OnboardingState");
            table.Add("Defender for Endpoint", onboarded != null && Convert.ToUInt32(onboarded) == 1 ? "Onboarded" : "Not onboarded");
            return table;
        }

        /// <summary>Threats Defender found in the last 30 days: how many, and their names.</summary>
        private static string Threats(ManagementScope defender)
        {
            try
            {
                DateTime since = DateTime.Now.AddDays(-30);
                List<ManagementObject> detections = SystemInfoReader.Query(defender, "SELECT ThreatID, InitialDetectionTime FROM MSFT_MpThreatDetection");
                List<long> ids = new List<long>();
                foreach (ManagementObject detection in detections)
                {
                    using (detection)
                    {
                        DateTime? found = ClientActions.ToDateTime(detection["InitialDetectionTime"]);
                        if (found.HasValue && found.Value >= since) ids.Add(Convert.ToInt64(detection["ThreatID"]));
                    }
                }
                if (ids.Count == 0) return "None";

                Dictionary<long, string> names = new Dictionary<long, string>();
                foreach (ManagementObject threat in SystemInfoReader.Query(defender, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat"))
                {
                    using (threat) names[Convert.ToInt64(threat["ThreatID"])] = Text(threat["ThreatName"]);
                }
                List<string> found30 = ids.Distinct().Select(id => names.TryGetValue(id, out string name) && name != null ? name : "threat " + id).ToList();
                return string.Format("{0}: {1}", found30.Count, string.Join(", ", found30.Take(5))) + (found30.Count > 5 ? ", ..." : string.Empty);
            }
            catch (ManagementException ex)
            {
                return "Couldn't read: " + ex.Message.Trim();
            }
        }

        /// <summary>
        /// Each firewall profile as Windows applies it (the active store: local settings and Group Policy
        /// together), and the networks it's in use on right now.
        /// </summary>
        public static InfoTable Firewall(string host)
        {
            InfoTable table = new InfoTable("Profile", "Firewall", "Inbound", "Outbound", "In use on");
            ManagementScope scope = Utility.GetWMIScope(host, "StandardCimv2");

            Dictionary<string, List<string>> networks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject connection in SystemInfoReader.Query(scope, "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile"))
            {
                using (connection)
                {
                    string profile = NetworkCategory(Convert.ToUInt32(connection["NetworkCategory"] ?? 0u));
                    if (!networks.TryGetValue(profile, out List<string> list)) networks[profile] = list = new List<string>();
                    list.Add(SystemInfoReader.Join(" ", Text(connection["InterfaceAlias"]), Parenthesized(Text(connection["Name"]))));
                }
            }

            List<ManagementObject> profiles;
            try
            {
                profiles = FirewallProfiles(scope, true);
            }
            catch (ManagementException ex)
            {
                log.Warn("The firewall's active settings didn't answer on " + host + ", so local settings only: " + ex.Message.Trim());
                profiles = FirewallProfiles(scope, false);
                table.Note = "local settings only: Group Policy settings aren't included";
            }

            foreach (ManagementObject profile in profiles.OrderBy(p => ProfileOrder(Text(p["Name"]))))
            {
                using (profile)
                {
                    string name = Text(profile["Name"]);
                    table.Add(
                        name,
                        Enabled(profile["Enabled"]),
                        Action(profile["DefaultInboundAction"], "Block"),
                        Action(profile["DefaultOutboundAction"], "Allow"),
                        networks.TryGetValue(name ?? string.Empty, out List<string> on) ? string.Join("; ", on) : string.Empty);
                }
            }
            return table;
        }

        /// <summary>
        /// The firewall profiles. Active: as Windows applies them, Group Policy included (the PolicyStore option
        /// Get-NetFirewallProfile -PolicyStore ActiveStore uses). Without it WMI answers with the local settings,
        /// and a firewall Group Policy turned on looks off.
        /// </summary>
        private static List<ManagementObject> FirewallProfiles(ManagementScope scope, bool active)
        {
            EnumerationOptions options = new EnumerationOptions { Timeout = SystemInfoReader.QueryTimeout, ReturnImmediately = true, Rewindable = false };
            if (active) options.Context.Add("PolicyStore", "ActiveStore");
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, Enabled, DefaultInboundAction, DefaultOutboundAction FROM MSFT_NetFirewallProfile"), options))
            {
                return searcher.Get().Cast<ManagementObject>().ToList();
            }
        }

        public static InfoTable BitLocker(string host)
        {
            InfoTable table = new InfoTable("Drive", "Type", "Protection", "Encryption", "Method", "Key protectors");
            ManagementScope scope;
            List<ManagementObject> volumes;
            try
            {
                scope = Utility.GetWMIScope(host, BitLockerNamespace);
                volumes = SystemInfoReader.Query(scope, "SELECT * FROM Win32_EncryptableVolume");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                table.Note = "BitLocker isn't on " + host;
                return table;
            }

            foreach (ManagementObject volume in volumes.OrderBy(v => Convert.ToUInt32(v["VolumeType"] ?? 9u)).ThenBy(v => Text(v["DriveLetter"])))
            {
                using (volume)
                {
                    string drive = Text(volume["DriveLetter"]) ?? "(no letter)";
                    uint type = Convert.ToUInt32(volume["VolumeType"] ?? 9u);
                    string typeText = type == 0 ? "Windows" : type == 1 ? "Fixed data" : type == 2 ? "Removable" : "Other";
                    table.Add(drive, typeText, Protection(volume), Conversion(volume), Method(volume), Protectors(volume));
                }
            }
            if (table.Rows.Count == 0) table.Note = "No volumes BitLocker can encrypt";
            return table;
        }

        /// <summary>On, off, or suspended for how many more restarts (0 = until resumed).</summary>
        private static string Protection(ManagementObject volume)
        {
            uint protection = Convert.ToUInt32(volume["ProtectionStatus"] ?? 2u);   // 0 off, 1 on, 2 unknown (locked)
            if (protection == 1) return "On";
            if (protection == 2) return "Unknown (locked)";
            // Off on an encrypted drive means suspended: the key protectors are there but turned off.
            if (Invoke(volume, "GetConversionStatus", "ConversionStatus") is uint conversion && conversion != 0)
            {
                object count = Invoke(volume, "GetSuspendCount", "SuspendCount");
                if (count is uint restarts)
                {
                    return restarts == 0 ? "Suspended until resumed" : string.Format("Suspended for {0} more restart{1}", restarts, restarts == 1 ? string.Empty : "s");
                }
                return "Suspended";
            }
            return "Off";
        }

        // Win32_EncryptableVolume.GetConversionStatus: ConversionStatus.
        private static readonly string[] ConversionStates = { "Not encrypted", "Encrypted", "Encrypting", "Decrypting", "Encryption paused", "Decryption paused" };

        private static string Conversion(ManagementObject volume)
        {
            using (ManagementBaseObject output = volume.InvokeMethod("GetConversionStatus", null, null))
            {
                if (output == null || Convert.ToUInt32(output["ReturnValue"] ?? 1u) != 0) return string.Empty;
                uint status = Convert.ToUInt32(output["ConversionStatus"]);
                string text = status < ConversionStates.Length ? ConversionStates[status] : "State " + status;
                if (status >= 2) text += " " + output["EncryptionPercentage"] + "%";
                return text;
            }
        }

        // Win32_EncryptableVolume.GetEncryptionMethod: EncryptionMethod.
        private static readonly string[] Methods = { "None", "AES 128 with diffuser", "AES 256 with diffuser", "AES 128", "AES 256", "Hardware", "XTS-AES 128", "XTS-AES 256" };

        private static string Method(ManagementObject volume)
        {
            object method = Invoke(volume, "GetEncryptionMethod", "EncryptionMethod");
            if (!(method is uint value)) return string.Empty;
            return value == 0 ? string.Empty : value < Methods.Length ? Methods[value] : "Method " + value;
        }

        // Win32_EncryptableVolume.GetKeyProtectorType: KeyProtectorType.
        private static readonly string[] ProtectorTypes =
        {
            "Unknown", "TPM", "Startup key", "Recovery password", "TPM and PIN", "TPM and startup key",
            "TPM, PIN and startup key", "Certificate", "Password", "TPM certificate", "Active Directory account",
        };

        /// <summary>The kinds of key protector the drive has. Only their types: no recovery password is read.</summary>
        private static string Protectors(ManagementObject volume)
        {
            using (ManagementBaseObject input = volume.GetMethodParameters("GetKeyProtectors"))
            {
                input["KeyProtectorType"] = 0u;   // all of them
                using (ManagementBaseObject output = volume.InvokeMethod("GetKeyProtectors", input, null))
                {
                    if (output == null || Convert.ToUInt32(output["ReturnValue"] ?? 1u) != 0) return string.Empty;
                    List<string> types = new List<string>();
                    foreach (string id in output["VolumeKeyProtectorID"] as string[] ?? new string[0])
                    {
                        using (ManagementBaseObject typeInput = volume.GetMethodParameters("GetKeyProtectorType"))
                        {
                            typeInput["VolumeKeyProtectorID"] = id;
                            using (ManagementBaseObject typeOutput = volume.InvokeMethod("GetKeyProtectorType", typeInput, null))
                            {
                                uint type = Convert.ToUInt32(typeOutput?["KeyProtectorType"] ?? 0u);
                                types.Add(type < ProtectorTypes.Length ? ProtectorTypes[type] : "Type " + type);
                            }
                        }
                    }
                    return string.Join(", ", types.Distinct());
                }
            }
        }

        /// <summary>
        /// Suspends BitLocker on the Windows drive for a number of restarts (Win32_EncryptableVolume.DisableKeyProtectors,
        /// what Suspend-BitLocker -RebootCount does). The drive stays encrypted; Windows turns the protectors back on
        /// by itself once the restarts are used up.
        /// </summary>
        public static string SuspendBitLocker(string host, int restarts)
        {
            using (ManagementObject volume = WindowsVolume(host))
            using (ManagementBaseObject input = volume.GetMethodParameters("DisableKeyProtectors"))
            {
                string notEncrypted = NotEncrypted(volume, "suspend");
                if (notEncrypted != null) return notEncrypted;
                input["DisableCount"] = (uint)restarts;
                using (ManagementBaseObject output = volume.InvokeMethod("DisableKeyProtectors", input, null))
                {
                    Check("Suspending BitLocker", output);
                }
                return string.Format("BitLocker on {0} suspended for {1} restart{2}", Text(volume["DriveLetter"]), restarts, restarts == 1 ? string.Empty : "s");
            }
        }

        /// <summary>Turns the Windows drive's key protectors back on (EnableKeyProtectors, what Resume-BitLocker does).</summary>
        public static string ResumeBitLocker(string host)
        {
            using (ManagementObject volume = WindowsVolume(host))
            {
                string notEncrypted = NotEncrypted(volume, "resume");
                if (notEncrypted != null) return notEncrypted;
                if (Convert.ToUInt32(volume["ProtectionStatus"] ?? 2u) == 1) return "BitLocker on " + Text(volume["DriveLetter"]) + " is already on";
                using (ManagementBaseObject output = volume.InvokeMethod("EnableKeyProtectors", null, null))
                {
                    Check("Resuming BitLocker", output);
                }
                return "BitLocker on " + Text(volume["DriveLetter"]) + " resumed";
            }
        }

        public static string QuickScan(string host)
        {
            ClientActions.StartDefenderQuickScan(host);
            return "Quick scan started; Refresh shows it once it's done";
        }

        /// <summary>
        /// Why there's nothing to suspend or resume, or null when there is. Windows reports success for both on a drive
        /// that isn't encrypted, so Layer8 checks first rather than say it suspended something.
        /// </summary>
        private static string NotEncrypted(ManagementObject volume, string verb)
        {
            if (Invoke(volume, "GetConversionStatus", "ConversionStatus") is uint conversion && conversion == 0)
                return string.Format("BitLocker isn't on for {0}, so there's nothing to {1}", Text(volume["DriveLetter"]), verb);
            return null;
        }

        private static ManagementObject WindowsVolume(string host)
        {
            ManagementObject volume = SystemInfoReader.First(Utility.GetWMIScope(host, BitLockerNamespace), "SELECT * FROM Win32_EncryptableVolume WHERE VolumeType = 0");
            if (volume == null) throw new InvalidOperationException(host + "'s Windows drive isn't one BitLocker can encrypt");
            return volume;
        }

        private static void Check(string what, ManagementBaseObject output)
        {
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            if (code == 0) return;
            // Windows' own description of the BitLocker error (FVE_E_...), as Error Lookup gives it.
            throw new InvalidOperationException(string.Format("{0} failed: {1}", what, ErrorCodes.FormatAndDescribe(code)));
        }

        private static object Invoke(ManagementObject volume, string method, string result)
        {
            try
            {
                using (ManagementBaseObject output = volume.InvokeMethod(method, null, null))
                {
                    return output != null && Convert.ToUInt32(output["ReturnValue"] ?? 1u) == 0 ? output[result] : null;
                }
            }
            catch (ManagementException)
            {
                return null;   // GetSuspendCount is Windows 8 and later
            }
        }

        private static string NetworkCategory(uint category) => category == 2 ? "Domain" : category == 1 ? "Private" : "Public";

        private static int ProfileOrder(string name) => name == "Domain" ? 0 : name == "Private" ? 1 : 2;

        // GpoBoolean: 0 false, 1 true, 2 not configured (on, Windows' default).
        private static string Enabled(object value)
        {
            int enabled = Convert.ToInt32(value ?? 2);
            return enabled == 0 ? "OFF" : "On";
        }

        // Action: 0 not configured (Windows' default), 2 allow, 4 block.
        private static string Action(object value, string notConfigured)
        {
            int action = Convert.ToInt32(value ?? 0);
            return action == 2 ? "Allow" : action == 4 ? "Block" : notConfigured;
        }

        private static string OnOff(object value) => value == null ? "Unknown" : Convert.ToBoolean(value) ? "On" : "OFF";

        private static string Text(object value) => string.IsNullOrWhiteSpace(value as string) ? null : ((string)value).Trim();

        private static string Parenthesized(string text) => string.IsNullOrWhiteSpace(text) ? null : "(" + text + ")";

        /// <summary>Defender's ages are days, with uint.MaxValue for never.</summary>
        private static string Days(object value)
        {
            if (value == null) return null;
            long days = Convert.ToInt64(value);
            if (days >= uint.MaxValue) return null;
            return days == 1 ? "1 day" : days + " days";
        }

        private static InfoCell Scan(object endTime, object age)
        {
            DateTime? ended = ClientActions.ToDateTime(endTime);
            string days = Days(age);
            if (!ended.HasValue && days == null) return "Never";
            return new InfoCell(SystemInfoReader.Join(", ", ended?.ToString("g"), days != null ? days + " ago" : null), ended);
        }

        // MSFT_MpComputerStatus.ComputerState flags.
        private static string Needs(object value)
        {
            uint state = Convert.ToUInt32(value ?? 0u);
            if (state == 0) return "Nothing";
            List<string> needs = new List<string>();
            if ((state & 1) != 0) needs.Add("a full scan");
            if ((state & 2) != 0) needs.Add("a restart");
            if ((state & 4) != 0) needs.Add("manual steps");
            if ((state & 8) != 0) needs.Add("an offline scan");
            if ((state & 16) != 0) needs.Add("attention: critical failure");
            return string.Join(", ", needs);
        }
    }
}
