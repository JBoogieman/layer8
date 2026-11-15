using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// System Information on a device's Layer8 menu: one window with what you'd otherwise remote in for. Tabs for
    /// an overview (model, serial, Windows build, uptime, memory, disks, battery), installed programs, installed
    /// updates, services, drivers, user profiles and local administrators. Each tab is read over WMI the first
    /// time it's opened, sorts by any column, filters as you type, and copies or exports what's shown.
    /// Reading changes nothing on the device. The one thing that does is Uninstall on the Programs tab, for a
    /// Windows Installer program, after asking.
    /// </summary>
    public static class SystemInfo
    {
        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "System Information", () => ShowCore(selectedObject));
        }

        private static void ShowCore(IResultObject selectedObject)
        {
            string name = selectedObject["Name"].StringValue;
            if (!RemoteTools.IsComputerName(name))
            {
                MessageBox.Show(name + " isn't a computer name.", "System Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SystemInfoDialog dialog = new SystemInfoDialog(name))
            {
                dialog.ShowDialog();
            }
        }
    }

    /// <summary>One cell: its text, and the value it sorts by when that isn't the text (a date, a size).</summary>
    internal sealed class InfoCell
    {
        public InfoCell(string text, IComparable key = null)
        {
            Text = text ?? string.Empty;
            Key = key;
        }

        public string Text { get; }
        public IComparable Key { get; }

        public static implicit operator InfoCell(string text) => new InfoCell(text);

        public static InfoCell Date(DateTime? value) => value.HasValue ? new InfoCell(value.Value.ToString("d"), value.Value) : new InfoCell(null);

        public static InfoCell Size(double bytes, string format = "0.#") => new InfoCell(Utility.FormatBytes(bytes, format), bytes);
    }

    /// <summary>What one tab shows: its columns and rows.</summary>
    internal sealed class InfoTable
    {
        public InfoTable(params string[] columns)
        {
            Columns = columns;
        }

        public string[] Columns { get; }
        public List<InfoCell[]> Rows { get; } = new List<InfoCell[]>();

        /// <summary>Said after the row count, for anything the rows don't show (why a tab is empty, say).</summary>
        public string Note { get; set; }

        // What a row stands for, for tab buttons that act on the selected rows. Keyed by the row array itself.
        private readonly Dictionary<InfoCell[], object> tags = new Dictionary<InfoCell[], object>();

        public void Add(params InfoCell[] cells) => Rows.Add(cells);

        /// <summary>A row that stands for something a tab button can act on (a program, say).</summary>
        public void AddTagged(object tag, params InfoCell[] cells)
        {
            Rows.Add(cells);
            tags[cells] = tag;
        }

        public object TagOf(InfoCell[] row) => row != null && tags.TryGetValue(row, out object tag) ? tag : null;
    }

    /// <summary>A program on the Programs tab, for Uninstall.</summary>
    internal sealed class InstalledProgram
    {
        private static readonly Regex ProductCode = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

        public string Name, Version;

        /// <summary>Its key under Windows' Uninstall key. For a Windows Installer program it's the product code.</summary>
        public string Key;

        public bool IsWindowsInstaller => Key != null && ProductCode.IsMatch(Key);

        public string NameAndVersion => string.IsNullOrEmpty(Version) ? Name : Name + " " + Version;
    }

    /// <summary>Reads each tab from the device. Every method runs off the UI thread.</summary>
    internal static class SystemInfoReader
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(SystemInfoReader));
        private const uint HKEY_LOCAL_MACHINE = 0x80000002;

        /// <summary>
        /// How long a device gets to answer one query. Without a limit a tab could say "Reading..." forever: in the
        /// lab, Win32_QuickFixEngineering didn't answer at all on one Windows 11 build.
        /// </summary>
        internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

        internal static List<ManagementObject> Query(ManagementScope scope, string wql, ManagementNamedValueCollection context = null)
        {
            EnumerationOptions options = new EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
            if (context != null) options.Context = context;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options))
            {
                return searcher.Get().Cast<ManagementObject>().ToList();
            }
        }

        internal static ManagementObject First(ManagementScope scope, string wql)
        {
            List<ManagementObject> found = Query(scope, wql);
            for (int i = 1; i < found.Count; i++) found[i].Dispose();
            return found.FirstOrDefault();
        }

        public static InfoTable Overview(string host)
        {
            InfoTable table = new InfoTable("Item", "Value");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");

            using (ManagementObject computer = First(cimv2, "SELECT Name, Domain, Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            using (ManagementObject bios = First(cimv2, "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            using (ManagementObject os = First(cimv2, "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                table.Add("Computer", Join(" ", computer?["Name"] as string, Parenthesized(computer?["Domain"] as string)));
                table.Add("Manufacturer", computer?["Manufacturer"] as string);
                table.Add("Model", computer?["Model"] as string);
                table.Add("Serial number", (bios?["SerialNumber"] as string)?.Trim());
                DateTime? biosDate = ClientActions.ToDateTime(bios?["ReleaseDate"]);
                table.Add("BIOS", Join(", ", bios?["SMBIOSBIOSVersion"] as string, biosDate?.ToString("d")));

                // The marketing version (24H2) and the update revision (the .4652 in 26100.4652) are only in the registry.
                string displayVersion = RegistryValue(host, "GetStringValue", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") as string;
                object revision = RegistryValue(host, "GetDWORDValue", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
                string build = os?["BuildNumber"] as string;
                if (!string.IsNullOrEmpty(build) && revision != null) build += "." + Convert.ToUInt32(revision).ToString(CultureInfo.InvariantCulture);
                table.Add("Windows", Join(" ", os?["Caption"] as string, displayVersion));
                table.Add("Build", Join(", ", build, os?["OSArchitecture"] as string));
                table.Add("Windows installed", InfoCell.Date(ClientActions.ToDateTime(os?["InstallDate"])));
                DateTime? lastBoot = ClientActions.ToDateTime(os?["LastBootUpTime"]);
                TimeSpan? uptime = ClientActions.Uptime(cimv2);
                table.Add("Up", uptime.HasValue ? new InfoCell(ClientActions.FormatUptime(uptime.Value), uptime.Value) : new InfoCell(null));
                table.Add("Last restart", lastBoot.HasValue ? new InfoCell(lastBoot.Value.ToString("g"), lastBoot.Value) : new InfoCell(null));

                List<string> users = ClientActions.SignedInUsers(cimv2);
                table.Add("Signed in", users.Count == 0 ? "Nobody" : string.Join(", ", users));

                List<ManagementObject> processors = Query(cimv2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                if (processors.Count > 0)
                {
                    int cores = processors.Sum(p => Convert.ToInt32(p["NumberOfCores"] ?? 0));
                    int threads = processors.Sum(p => Convert.ToInt32(p["NumberOfLogicalProcessors"] ?? 0));
                    string name = (processors[0]["Name"] as string ?? string.Empty).Trim();
                    if (processors.Count > 1) name = processors.Count + " x " + name;
                    table.Add("Processor", string.Format("{0} ({1} cores, {2} threads)", name, cores, threads));
                    processors.ForEach(p => p.Dispose());
                }
                if (computer?["TotalPhysicalMemory"] != null) table.Add("Memory", InfoCell.Size(Convert.ToDouble(computer["TotalPhysicalMemory"]), "0"));
            }

            foreach (ManagementObject disk in Query(cimv2, "SELECT DeviceID, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType = 3"))
            {
                using (disk)
                {
                    if (disk["Size"] == null || disk["FreeSpace"] == null) continue;
                    double size = Convert.ToDouble(disk["Size"]);
                    double free = Convert.ToDouble(disk["FreeSpace"]);
                    string text = string.Format("{0} free of {1} ({2:0}% free)", Utility.FormatBytes(free, "0.#"), Utility.FormatBytes(size, "0"), size > 0 ? free * 100 / size : 0);
                    table.Add("Drive " + disk["DeviceID"], new InfoCell(text, free));
                }
            }

            List<string> addresses = ClientActions.IPv4Addresses(cimv2);
            table.Add("IP address", string.Join(", ", addresses));
            table.Add("Battery", Battery(host, cimv2));

            try
            {
                using (ManagementObject client = First(Utility.GetWMIScope(host, "ccm"), "SELECT ClientVersion FROM SMS_Client"))
                {
                    table.Add("ConfigMgr client", client?["ClientVersion"] as string ?? "Version unknown");
                }
            }
            catch (ManagementException ex) when (ClientActions.IsNoClient(ex))
            {
                table.Add("ConfigMgr client", "None");
            }
            return table;
        }

        /// <summary>
        /// Charge and health of the device's batteries. Health is the full charge it holds now against what it was
        /// built to hold, from the battery's own figures in root\wmi; Windows' battery report uses the same two.
        /// </summary>
        private static string Battery(string host, ManagementScope cimv2)
        {
            List<ManagementObject> batteries = Query(cimv2, "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            if (batteries.Count == 0) return "None";

            List<string> parts = new List<string>();
            foreach (ManagementObject battery in batteries)
            {
                using (battery)
                {
                    string charge = battery["EstimatedChargeRemaining"] != null ? battery["EstimatedChargeRemaining"] + "% charged" : "Charge unknown";
                    string state = BatteryState(Convert.ToInt32(battery["BatteryStatus"] ?? 0));
                    parts.Add(Join(", ", charge, state));
                }
            }

            try
            {
                ManagementScope wmi = Utility.GetWMIScope(host, "wmi");
                double designed = Query(wmi, "SELECT DesignedCapacity FROM BatteryStaticData").Sum(b => Convert.ToDouble(b["DesignedCapacity"] ?? 0));
                double full = Query(wmi, "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Sum(b => Convert.ToDouble(b["FullChargedCapacity"] ?? 0));
                if (designed > 0 && full > 0)
                {
                    parts.Add(string.Format("health {0:0}% (holds {1:N0} of the {2:N0} mWh it was built for)", full * 100 / designed, full, designed));
                }
            }
            catch (ManagementException)
            {
                // Some batteries don't report their capacities; charge alone is still worth showing.
            }
            return string.Join("; ", parts);
        }

        private static string BatteryState(int status)
        {
            switch (status)
            {
                case 1: return "on battery";
                case 2: return "plugged in";
                case 3: return "fully charged";
                case 4: return "low";
                case 5: return "critical";
                case 6: case 7: case 8: case 9: return "charging";
                default: return null;
            }
        }

        /// <summary>
        /// Installed programs as Programs and Features lists them, from the ConfigMgr client's own inventory
        /// classes (the ones Find Software reads). Win32_Product is never touched: reading it makes Windows
        /// check, and sometimes repair, every MSI on the device. The 64-bit class is read with the 64-bit WMI
        /// context: the console is a 32-bit program, and without it the 64-bit class answered with the 32-bit list.
        /// A program in both lists (same name and version) is listed once.
        /// </summary>
        public static InfoTable Programs(string host)
        {
            InfoTable table = new InfoTable("Name", "Version", "Publisher", "Installed");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int classesRead = 0;
            foreach (string inventoryClass in new[] { "Win32Reg_AddRemovePrograms64", "Win32Reg_AddRemovePrograms" })
            {
                try
                {
                    foreach (ManagementObject program in Query(cimv2, "SELECT DisplayName, Version, Publisher, InstallDate, ProdID FROM " + inventoryClass, Utility.ContextFor(inventoryClass)))
                    {
                        using (program)
                        {
                            string name = (program["DisplayName"] as string)?.Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            string version = program["Version"] as string;
                            if (!seen.Add(name + "|" + version)) continue;
                            InstalledProgram tag = new InstalledProgram { Name = name, Version = version, Key = (program["ProdID"] as string)?.Trim() };
                            table.AddTagged(tag, name, version, program["Publisher"] as string, InfoCell.Date(InstallDate(program["InstallDate"] as string)));
                        }
                    }
                    classesRead++;
                }
                catch (ManagementException ex) when (ClientActions.IsNoClient(ex))
                {
                    // A device without the ConfigMgr client has neither class.
                }
            }
            if (classesRead == 0) table.Note = "the program list comes from the ConfigMgr client, and this device doesn't have it";
            return table;
        }

        /// <summary>Uninstall keys write InstallDate as yyyyMMdd, when they write it at all.</summary>
        private static DateTime? InstallDate(string text)
        {
            return DateTime.TryParseExact(text?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : (DateTime?)null;
        }

        /// <summary>How long Uninstall waits for msiexec before saying it's still running.</summary>
        internal static readonly TimeSpan UninstallWait = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Removes a Windows Installer program quietly: msiexec /x {product code} /qn /norestart on the device, started
        /// over WMI as the console user, with a verbose log in the device's Windows\Temp. Waits for msiexec to end, then
        /// takes the result from the events Windows Installer writes when a removal ends (MsiInstaller 1034, with 1038
        /// when it needs a restart), or else from the end of the log over the admin share. Runs off the UI thread.
        /// Windows Installer takes the program off the device's program list as the removal starts (and puts it back
        /// if the removal fails), so while msiexec runs the list can already leave it out.
        /// </summary>
        internal static string UninstallMsi(string host, InstalledProgram program)
        {
            // msiexec /x only sees products installed for the whole computer, or for the account running it. In the
            // lab, the Teams Meeting Add-in was in the machine's program list but registered to one local user, and
            // msiexec answered 1605 ("only valid for products that are currently installed"). Say why instead.
            string registeredTo = InstallerRegistration(host, program.Key);
            if (registeredTo == null)
            {
                return string.Format("Windows Installer has no record of {0} on {1}, so its entry in the program list is left over. Nothing was run", program.Name, host);
            }
            if (registeredTo != MachineSid)
            {
                return string.Format("{0} was installed for one user ({1}), not for the whole computer, so msiexec run as anyone else can't remove it. "
                                     + "Remove it as that user, or with a deployment that runs as that user. Nothing was run", program.Name, DeviceAccountName(host, registeredTo));
            }

            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            string windows, deviceNow;
            using (ManagementObject os = First(cimv2, "SELECT WindowsDirectory, LocalDateTime FROM Win32_OperatingSystem"))
            {
                windows = os?["WindowsDirectory"] as string ?? @"C:\Windows";
                deviceNow = os?["LocalDateTime"] as string;
            }
            string logFile = Path.Combine(windows, "Temp", "Layer8-uninstall-" + program.Key.Trim('{', '}') + ".log");
            string commandLine = string.Format("msiexec.exe /x {0} /qn /norestart /l*v \"{1}\"", program.Key, logFile);
            log.InfoFormat("Uninstall on {0}: {1} ({2})", host, program.NameAndVersion, commandLine);

            uint processId = ClientActions.StartRemoteProcess(host, commandLine, "msiexec.exe");
            DateTime deadline = DateTime.Now + UninstallWait;
            while (ProcessRunning(cimv2, processId))
            {
                if (DateTime.Now > deadline)
                {
                    return string.Format("{0}: msiexec is still running on {1} after {2} minutes. The list may already leave it out: Windows Installer takes a program off it "
                                         + "as the removal starts, and puts it back if the removal fails. Its log there, {3}, ends with the result",
                                         program.Name, host, UninstallWait.TotalMinutes, logFile);
                }
                System.Threading.Thread.Sleep(3000);
            }

            uint? fromEvent = RemovalStatus(cimv2, deviceNow, program.Name);
            uint? code = fromEvent ?? LogExitCode(host, logFile);
            log.InfoFormat("Uninstall on {0}: {1} ended with {2}, from {3}", host, program.Name, code.HasValue ? code.Value.ToString(CultureInfo.InvariantCulture) : "no exit code",
                           fromEvent.HasValue ? "MsiInstaller events 1034 and 1038" : code.HasValue ? "msiexec's log" : "neither event 1034 nor msiexec's log");
            if (!code.HasValue)
            {
                return string.Format("msiexec ended on {0} without saying how it went. The list below shows whether {1} is gone; the log is {2} there", host, program.Name, logFile);
            }
            switch (code.Value)
            {
                case 0: return "Uninstalled " + program.NameAndVersion;
                case 1641:
                case 3010: return "Uninstalled " + program.NameAndVersion + ". " + host + " needs a restart to finish";
                case 1605: return program.Name + " isn't installed on " + host + " as a Windows Installer product (1605). Refresh to see the current list";
                default: return string.Format("Uninstalling {0} failed: {1}. The log is {2} on {3}", program.Name, ErrorCodes.FormatAndDescribe(code.Value), logFile, host);
            }
        }

        /// <summary>Windows Installer's registrations for the whole computer live under SYSTEM's SID.</summary>
        private const string MachineSid = "S-1-5-18";

        private const string InstallerUserData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

        /// <summary>
        /// Whose Windows Installer registration a product has on the device: MachineSid for the whole computer, a
        /// user's SID for a per-user install, or null when it isn't registered at all. Read with WMI's registry
        /// provider in the 64-bit view, where Windows Installer keeps them.
        /// </summary>
        internal static string InstallerRegistration(string host, string productCode)
        {
            string packed = PackedGuid(productCode);
            using (ManagementClass registry = new ManagementClass(Utility.GetWMIScope(host, "default"), new ManagementPath("StdRegProv"), null))
            {
                InvokeMethodOptions options = new InvokeMethodOptions { Context = Utility.Provider64Context() };
                string[] sids = EnumKey(registry, options, InstallerUserData) ?? new string[0];
                foreach (string sid in sids.OrderBy(s => s == MachineSid ? 0 : 1))
                {
                    if (EnumKey(registry, options, InstallerUserData + "\\" + sid + "\\Products\\" + packed) != null) return sid;
                }
                return null;
            }
        }

        /// <summary>A key's subkeys (empty when it has none), or null when the key isn't there.</summary>
        private static string[] EnumKey(ManagementClass registry, InvokeMethodOptions options, string key)
        {
            using (ManagementBaseObject input = registry.GetMethodParameters("EnumKey"))
            {
                input["hDefKey"] = HKEY_LOCAL_MACHINE;
                input["sSubKeyName"] = key;
                using (ManagementBaseObject output = registry.InvokeMethod("EnumKey", input, options))
                {
                    if (Convert.ToUInt32(output?["ReturnValue"] ?? 1u) != 0) return null;
                    return output["sNames"] as string[] ?? new string[0];
                }
            }
        }

        /// <summary>
        /// A product code the way Windows Installer writes it in the registry: each of the first three parts
        /// reversed, then each byte of the rest with its two hex digits swapped.
        /// </summary>
        internal static string PackedGuid(string productCode)
        {
            string hex = productCode.Trim('{', '}').Replace("-", string.Empty).ToUpperInvariant();
            StringBuilder packed = new StringBuilder(32);
            packed.Append(new string(hex.Substring(0, 8).Reverse().ToArray()));
            packed.Append(new string(hex.Substring(8, 4).Reverse().ToArray()));
            packed.Append(new string(hex.Substring(12, 4).Reverse().ToArray()));
            for (int i = 16; i < 32; i += 2) packed.Append(hex[i + 1]).Append(hex[i]);
            return packed.ToString();
        }

        /// <summary>
        /// An account's name, asked of the device itself so local accounts resolve too ("DEV02\labuser"). Falls back
        /// to the console's own lookup, then the SID.
        /// </summary>
        private static string DeviceAccountName(string host, string sid)
        {
            try
            {
                using (ManagementObject account = new ManagementObject(Utility.GetWMIScope(host, "cimv2"), new ManagementPath("Win32_SID.SID='" + sid + "'"), null))
                {
                    account.Get();
                    string name = account["AccountName"] as string, domain = account["ReferencedDomainName"] as string;
                    if (!string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(domain) ? name : domain + "\\" + name;
                }
            }
            catch (ManagementException)
            {
                // Fall through.
            }
            return AccountName(sid) ?? sid;
        }

        private static bool ProcessRunning(ManagementScope cimv2, uint processId)
        {
            List<ManagementObject> found = Query(cimv2, "SELECT ProcessId FROM Win32_Process WHERE ProcessId = " + processId);
            foreach (ManagementObject process in found) process.Dispose();
            return found.Count > 0;
        }

        /// <summary>
        /// The exit code from the newest MsiInstaller 1034 event ("Windows Installer removed the product") written since
        /// the uninstall started, preferring one for this product name. Null when there's none: msiexec stopped before
        /// it got that far (another installation in progress, say).
        ///
        /// Two things the lab showed. The event's text shows the status as %4 but after the manufacturer (%5), so in
        /// InsertionStrings the status is [3] and [4] is the manufacturer. And 1034 says 0 for a removal that needs a
        /// restart, where msiexec itself returns 3010; Windows Installer writes that in a separate event, 1038
        /// ("requires a system restart"), so 0 plus a 1038 for the same product counts as 3010.
        /// </summary>
        private static uint? RemovalStatus(ManagementScope cimv2, string since, string productName)
        {
            if (string.IsNullOrEmpty(since)) return null;
            string wql = "SELECT EventCode, InsertionStrings, TimeWritten FROM Win32_NTLogEvent WHERE Logfile = 'Application' AND SourceName = 'MsiInstaller' "
                       + "AND (EventCode = 1034 OR EventCode = 1038) AND TimeWritten >= '" + since + "'";
            try
            {
                List<ManagementObject> events = Query(cimv2, wql);
                try
                {
                    // Oldest first, so Last is the newest. Same-day WMI times with the device's own offset sort as text.
                    var found = events.Select(e => new { Code = Convert.ToInt32(e["EventCode"], CultureInfo.InvariantCulture), Strings = e["InsertionStrings"] as string[], Time = e["TimeWritten"] as string ?? "" })
                                      .Where(e => e.Strings != null && e.Strings.Length > 0)
                                      .OrderBy(e => e.Time, StringComparer.Ordinal)
                                      .ToList();
                    bool IsProduct(string[] strings, string name) => string.Equals(strings[0]?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase);
                    string[][] removals = found.Where(e => e.Code == 1034 && e.Strings.Length >= 4).Select(e => e.Strings).ToArray();
                    string[] match = removals.LastOrDefault(s => IsProduct(s, productName)) ?? removals.LastOrDefault();
                    if (match == null || !uint.TryParse(match[3]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint code)) return null;
                    return code == 0 && found.Any(e => e.Code == 1038 && IsProduct(e.Strings, match[0])) ? 3010u : code;
                }
                finally
                {
                    foreach (ManagementObject e in events) e.Dispose();
                }
            }
            catch (ManagementException ex)
            {
                log.Warn("Reading MsiInstaller events failed", ex);
                return null;
            }
        }

        private static readonly Regex MainEngineReturn = new Regex(@"MainEngineThread is returning (\d+)", RegexOptions.Compiled);

        /// <summary>msiexec's exit code from the last lines of its verbose log, read over the admin share. Null when the share can't be read.</summary>
        private static uint? LogExitCode(string host, string logFile)
        {
            try
            {
                string unc = @"\\" + host + @"\" + logFile.Substring(0, 1) + "$" + logFile.Substring(2);
                // Verbose MSI logs are UTF-16 with a byte order mark, which ReadAllLines picks up.
                Match last = MainEngineReturn.Matches(string.Join("\n", File.ReadAllLines(unc).Reverse().Take(200))).Cast<Match>().FirstOrDefault();
                return last != null && uint.TryParse(last.Groups[1].Value, out uint code) ? code : (uint?)null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Warn("Reading " + logFile + " on " + host + " failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Updates Windows lists as installed (Win32_QuickFixEngineering, what Get-HotFix reads): cumulative and
        /// servicing stack updates and the like. Defender definitions and drivers aren't in it.
        /// </summary>
        public static InfoTable Updates(string host)
        {
            InfoTable table = new InfoTable("Update", "Type", "Installed", "Installed by");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            foreach (ManagementObject update in Query(cimv2, "SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering"))
            {
                using (update)
                {
                    string installedOn = update["InstalledOn"] as string;
                    // Windows writes it as month/day/year whatever the device's language.
                    InfoCell installed = DateTime.TryParse(installedOn, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime date)
                        ? InfoCell.Date(date)
                        : new InfoCell(installedOn);
                    table.Add(update["HotFixID"] as string, update["Description"] as string, installed, update["InstalledBy"] as string);
                }
            }
            return table;
        }

        public static InfoTable Services(string host)
        {
            InfoTable table = new InfoTable("Name", "Service name", "State", "Start", "Log on as");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            foreach (ManagementObject service in Query(cimv2, "SELECT DisplayName, Name, State, StartMode, StartName FROM Win32_Service"))
            {
                using (service)
                {
                    table.Add(service["DisplayName"] as string, service["Name"] as string, service["State"] as string, service["StartMode"] as string, service["StartName"] as string);
                }
            }
            return table;
        }

        /// <summary>Signed device drivers, one row per device. Windows can take several seconds to answer this one.</summary>
        public static InfoTable Drivers(string host)
        {
            InfoTable table = new InfoTable("Device", "Class", "Manufacturer", "Version", "Date", "INF");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            foreach (ManagementObject driver in Query(cimv2, "SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL"))
            {
                using (driver)
                {
                    table.Add(driver["DeviceName"] as string, driver["DeviceClass"] as string, driver["Manufacturer"] as string, driver["DriverVersion"] as string,
                        InfoCell.Date(ClientActions.ToDateTime(driver["DriverDate"])), driver["InfName"] as string);
                }
            }
            return table;
        }

        /// <summary>
        /// User profiles on the device, without Windows' own (SYSTEM and the service accounts). Names are looked up
        /// from the console's side, so a local account on the device shows its folder name instead.
        /// </summary>
        public static InfoTable Profiles(string host)
        {
            InfoTable table = new InfoTable("User", "Folder", "Last used", "Loaded");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            foreach (ManagementObject profile in Query(cimv2, "SELECT SID, LocalPath, LastUseTime, Loaded FROM Win32_UserProfile WHERE Special = FALSE"))
            {
                using (profile)
                {
                    string folder = profile["LocalPath"] as string;
                    string user = AccountName(profile["SID"] as string) ?? Path.GetFileName(folder ?? string.Empty);
                    DateTime? lastUsed = ClientActions.ToDateTime(profile["LastUseTime"]);
                    bool loaded = profile["Loaded"] is bool flag && flag;
                    table.Add(user, folder, lastUsed.HasValue ? new InfoCell(lastUsed.Value.ToString("g"), lastUsed.Value) : new InfoCell(null), loaded ? "Yes" : "No");
                }
            }
            table.Note = "Loaded means signed in, or a service is using the profile";
            return table;
        }

        private static string AccountName(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return null;
            try
            {
                return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            }
            catch (SystemException)
            {
                // Not found, or a local account of the device that only the device can name.
                return null;
            }
        }

        private static readonly Regex MemberPath = new Regex(@":(?<class>\w+)\.Domain=""(?<domain>(?:[^""\\]|\\.)*)"",Name=""(?<name>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        /// <summary>
        /// Members of the device's local Administrators group, found by its well-known SID so it works in any
        /// language. Reads the group's membership records rather than each member, so domain accounts don't make
        /// the device look each one up.
        /// </summary>
        public static InfoTable Administrators(string host)
        {
            InfoTable table = new InfoTable("Member", "Type", "Account");
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            using (ManagementObject group = First(cimv2, "SELECT Name, Domain FROM Win32_Group WHERE LocalAccount = TRUE AND SID = 'S-1-5-32-544'"))
            {
                if (group == null)
                {
                    table.Note = "the device didn't return its Administrators group";
                    return table;
                }
                string groupName = group["Name"] as string;
                string computer = group["Domain"] as string;
                table.Note = "members of " + computer + "\\" + groupName;
                string query = string.Format("REFERENCES OF {{Win32_Group.Domain=\"{0}\",Name=\"{1}\"}} WHERE ResultClass = Win32_GroupUser", PathText(computer), PathText(groupName));
                foreach (ManagementObject membership in Query(cimv2, query))
                {
                    using (membership)
                    {
                        Match member = MemberPath.Match(membership["PartComponent"] as string ?? string.Empty);
                        if (!member.Success) continue;
                        string domain = Regex.Unescape(member.Groups["domain"].Value);
                        string name = Regex.Unescape(member.Groups["name"].Value);
                        string type = member.Groups["class"].Value == "Win32_Group" ? "Group" : "User";
                        string account = string.Equals(domain, computer, StringComparison.OrdinalIgnoreCase) ? "Local" : "Domain";
                        table.Add(domain + "\\" + name, type, account);
                    }
                }
            }
            return table;
        }

        /// <summary>A key value inside a WMI object path: backslashes and quotes escaped.</summary>
        private static string PathText(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>A registry value read through WMI's registry provider, or null if it isn't there.</summary>
        internal static object RegistryValue(string host, string method, string key, string name)
        {
            try
            {
                using (ManagementClass registry = new ManagementClass(Utility.GetWMIScope(host, "default"), new ManagementPath("StdRegProv"), null))
                using (ManagementBaseObject input = registry.GetMethodParameters(method))
                {
                    input["hDefKey"] = HKEY_LOCAL_MACHINE;
                    input["sSubKeyName"] = key;
                    input["sValueName"] = name;
                    using (ManagementBaseObject output = registry.InvokeMethod(method, input, null))
                    {
                        if (Convert.ToUInt32(output?["ReturnValue"] ?? 1u) != 0) return null;
                        return method == "GetDWORDValue" ? output["uValue"] : output["sValue"];
                    }
                }
            }
            catch (ManagementException)
            {
                return null;
            }
        }

        internal static string Join(string separator, params string[] parts)
        {
            return string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        }

        private static string Parenthesized(string text) => string.IsNullOrWhiteSpace(text) ? null : "(" + text.Trim() + ")";
    }

    internal sealed class SystemInfoDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(SystemInfoDialog));
        private const int RpcServerUnavailable = unchecked((int)0x800706BA);

        private sealed class Tab
        {
            /// <summary>The tab's name, and what one row is ("program"), for the counts.</summary>
            public string Title, Noun;
            public Func<string, InfoTable> Read;
            public ListView List;
            public InfoTable Table;
            public string Error;
            public DateTime ReadAt;
            public bool Loading, Fitted, Descending;
            public int SortColumn = -1;
            /// <summary>Buttons that act on what this tab shows; they're only there while it's open.</summary>
            public List<Button> Buttons = new List<Button>();
            /// <summary>What the last button click on this tab did, kept in the status line.</summary>
            public string ActionResult;
        }

        private readonly string host;
        private readonly string windowTitle;
        private readonly TabControl tabs;
        private readonly TextBox filter;
        private readonly Label status;
        private readonly List<Tab> tabList = new List<Tab>();
        private bool acting;

        public SystemInfoDialog(string host) : this(host, "System Information")
        {
            AddTab("Overview", "item", SystemInfoReader.Overview);
            AddTab("Programs", "program", SystemInfoReader.Programs);
            AddTabButton("Uninstall...", PrepareUninstall);
            AddTab("Updates", "update", SystemInfoReader.Updates);
            AddTab("Services", "service", SystemInfoReader.Services);
            AddTab("Drivers", "driver", SystemInfoReader.Drivers);
            AddTab("Profiles", "profile", SystemInfoReader.Profiles);
            AddTab("Administrators", "member", SystemInfoReader.Administrators);
        }

        /// <summary>An empty window titled "title: host"; AddTab fills it.</summary>
        internal SystemInfoDialog(string host, string title)
        {
            this.host = host;
            windowTitle = title;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = title + ": " + host;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(620, 400);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;

            Label filterLabel = new Label { Text = "Filter:", Location = new Point(12, 15), AutoSize = true };
            filter = new TextBox { Location = new Point(52, 12), Size = new Size(260, 20) };
            tabs = new TabControl { Location = new Point(12, 40), Size = new Size(876, 472), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            status = new Label { Location = new Point(12, 524), Size = new Size(540, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            Button refresh = new Button { Text = "Refresh", Location = new Point(570, 524), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Button copy = new Button { Text = "Copy", Location = new Point(651, 524), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Button export = new Button { Text = "Export...", Location = new Point(732, 524), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(813, 524), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            tabs.SelectedIndexChanged += (s, e) => { ShowButtons(); LoadIfNeeded(Current); ShowTab(Current); };
            filter.TextChanged += (s, e) => ShowTab(Current);
            refresh.Click += (s, e) => { Current.ActionResult = null; Load(Current); };
            copy.Click += (s, e) => CopyRows(Current, false);
            export.Click += (s, e) => Export(Current);
            Shown += (s, e) => { ShowButtons(); LoadIfNeeded(Current); };

            Controls.AddRange(new Control[] { filterLabel, filter, tabs, status, refresh, copy, export, close });
            CancelButton = close;
            ResumeLayout(false);
        }

        private Tab Current => tabList[Math.Max(0, tabs.SelectedIndex)];

        /// <summary>
        /// A button on the tab that was added last, right of the Filter box (after that tab's earlier buttons). Not
        /// top right: with the window maximized, Windows 11 takes clicks there as clicks on the title bar's Restore
        /// and Close buttons. Clicking it calls prepare on the UI thread (to ask anything it needs); null from prepare
        /// cancels. Otherwise what prepare returns runs in the background, its result goes in the status line, and
        /// the tab is read again.
        /// </summary>
        internal void AddTabButton(string text, Func<IWin32Window, string, Func<string>> prepare)
        {
            AddTabButton(text, (owner, device, selected) => prepare(owner, device));
        }

        /// <summary>The same, with prepare also given what the selected rows stand for (their tags), in list order.</summary>
        internal void AddTabButton(string text, Func<IWin32Window, string, IList<object>, Func<string>> prepare)
        {
            Tab tab = tabList[tabList.Count - 1];
            int width = Math.Max(75, TextRenderer.MeasureText(text, Font).Width + 20);
            int left = tab.Buttons.Count == 0 ? filter.Right + 12 : tab.Buttons[tab.Buttons.Count - 1].Right + 6;
            Button button = new Button { Text = text, Size = new Size(width, 23), Location = new Point(left, 10), Anchor = AnchorStyles.Top | AnchorStyles.Left, Visible = false };
            button.Click += (s, e) => Act(tab, text, prepare);
            tab.Buttons.Add(button);
            Controls.Add(button);
        }

        /// <summary>
        /// Uninstall on the Programs tab: one selected program, and only one Windows Installer installed. Other
        /// programs come with their vendor's own uninstaller, each with its own switches for running quietly (if any),
        /// so there's nothing Layer8 could run for them that would be safe to guess.
        /// </summary>
        private static Func<string> PrepareUninstall(IWin32Window owner, string host, IList<object> selected)
        {
            List<InstalledProgram> programs = selected.OfType<InstalledProgram>().ToList();
            if (programs.Count != 1)
            {
                MessageBox.Show(owner, "Select the one program to uninstall.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            InstalledProgram program = programs[0];
            if (!program.IsWindowsInstaller)
            {
                MessageBox.Show(owner, program.Name + " wasn't installed by Windows Installer, so Layer8 can't remove it quietly. Programs like it come with their own uninstaller, and each has its own switches.\n\n"
                                + "Use the vendor's silent uninstall command, through a ConfigMgr application or script.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            string question = string.Format("Uninstall {0} from {1}?\n\nLayer8 runs this on {1}, as you, and waits up to {2} minutes for it:\n\nmsiexec /x {3} /qn /norestart\n\n"
                                            + "Nobody signed in on {1} is asked. If it needs a restart to finish, {1} doesn't restart by itself.",
                                            program.NameAndVersion, host, SystemInfoReader.UninstallWait.TotalMinutes, program.Key);
            return MessageBox.Show(owner, question, "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes
                ? (Func<string>)(() => SystemInfoReader.UninstallMsi(host, program))
                : null;
        }

        private void ShowButtons()
        {
            foreach (Tab tab in tabList)
                foreach (Button button in tab.Buttons) button.Visible = tab == Current;
        }

        private void Act(Tab tab, string text, Func<IWin32Window, string, IList<object>, Func<string>> prepare)
        {
            if (acting || tab.Loading) return;
            List<object> selected = tab.Table == null ? new List<object>() : tab.List.SelectedItems.Cast<ListViewItem>()
                .Select(item => tab.Table.TagOf(item.Tag as InfoCell[])).Where(t => t != null).ToList();
            Func<string> run = prepare(this, host, selected);
            if (run == null) return;
            string name = text.TrimEnd('.');
            acting = true;
            foreach (Button button in tab.Buttons) button.Enabled = false;
            status.Text = name + " on " + host + "...";
            Task.Run(() =>
            {
                try
                {
                    return run();
                }
                catch (Exception ex)
                {
                    log.Error(name + " on " + host + " failed", ex);
                    return name + " failed: " + Describe(ex);
                }
            }).ContinueWith(task =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        acting = false;
                        foreach (Button button in tab.Buttons) button.Enabled = true;
                        log.InfoFormat("{0} {1} on {2}: {3}", windowTitle, name, host, task.Result);
                        tab.ActionResult = task.Result;
                        Load(tab);
                    }));
                }
                catch (InvalidOperationException)
                {
                    // The window closed while the device was answering.
                }
            }, TaskScheduler.Default);
        }

        internal void AddTab(string title, string noun, Func<string, InfoTable> read)
        {
            ListView list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
            };
            Tab tab = new Tab { Title = title, Noun = noun, Read = read, List = list };
            list.ColumnClick += (s, e) => SortBy(tab, e.Column);
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyRows(tab, true); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in list.Items) item.Selected = true; e.Handled = true; }
            };
            TabPage page = new TabPage(title);
            page.Controls.Add(list);
            tabs.TabPages.Add(page);
            tabList.Add(tab);
        }

        private void LoadIfNeeded(Tab tab)
        {
            if (tab.Table == null && tab.Error == null) Load(tab);
        }

        /// <summary>Reads the tab in the background; the window stays usable meanwhile.</summary>
        private void Load(Tab tab)
        {
            if (tab.Loading) return;
            tab.Loading = true;
            if (tab == Current) status.Text = "Reading " + tab.Title.ToLowerInvariant() + " from " + host + "...";
            Task.Run(() =>
            {
                try
                {
                    return Tuple.Create(tab.Read(host), (string)null);
                }
                catch (Exception ex)
                {
                    return Tuple.Create((InfoTable)null, Describe(ex));
                }
            }).ContinueWith(task =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() => Loaded(tab, task.Result.Item1, task.Result.Item2)));
                }
                catch (InvalidOperationException)
                {
                    // The window closed while the device was answering.
                }
            }, TaskScheduler.Default);
        }

        private void Loaded(Tab tab, InfoTable table, string error)
        {
            tab.Loading = false;
            tab.Error = error;
            if (table != null)
            {
                tab.Table = table;
                tab.ReadAt = DateTime.Now;
                tab.Fitted = false;
            }
            log.InfoFormat("{0} {1} on {2}: {3}", windowTitle, tab.Title, host, error ?? Count(table.Rows.Count, tab.Noun));
            if (tab == Current) ShowTab(tab);
        }

        /// <summary>What went wrong, the way the progress dialog says it.</summary>
        private string Describe(Exception ex)
        {
            Exception root = ex.GetBaseException();
            if (root is COMException com && com.ErrorCode == RpcServerUnavailable)
            {
                return ClientActions.AnswersAtAll(host)
                    ? host + " is online, but WMI is blocked (RPC server unavailable): check the device's firewall"
                    : host + " is offline, or its firewall blocks WMI (RPC server unavailable)";
            }
            if (root is ManagementException slow && slow.ErrorCode == ManagementStatus.Timedout)
            {
                return host + " didn't answer within " + SystemInfoReader.QueryTimeout.TotalSeconds + " seconds. Windows can be slow to list these; Refresh tries again";
            }
            string localDenied = Utility.LocalAccessDeniedReason(host, root);
            if (localDenied != null) return localDenied;
            if (root is UnauthorizedAccessException || (root is ManagementException wmi && wmi.ErrorCode == ManagementStatus.AccessDenied))
            {
                return "Access denied: the account running the console needs to be a local administrator on " + host;
            }
            return "Couldn't read it: " + root.Message.Trim();
        }

        /// <summary>Fills the tab's list from what was read, with the filter applied and the sort kept.</summary>
        private void ShowTab(Tab tab)
        {
            ListView list = tab.List;
            if (tab.Table == null)
            {
                status.Text = tab.Error ?? (tab.Loading ? "Reading " + tab.Title.ToLowerInvariant() + " from " + host + "..." : string.Empty);
                return;
            }

            string[] columns = tab.Table.Columns;
            string text = filter.Text.Trim();
            List<ListViewItem> items = new List<ListViewItem>();
            foreach (InfoCell[] row in tab.Table.Rows)
            {
                if (text.Length > 0 && !row.Any(cell => cell.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                ListViewItem item = new ListViewItem(row.Length > 0 ? row[0].Text : string.Empty) { Tag = row };
                item.SubItems[0].Tag = row.Length > 0 ? row[0].Key : null;
                for (int i = 1; i < columns.Length; i++)
                {
                    InfoCell cell = i < row.Length ? row[i] : null;
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, cell?.Text ?? string.Empty) { Tag = cell?.Key });
                }
                items.Add(item);
            }

            list.BeginUpdate();
            list.ListViewItemSorter = null;
            list.Items.Clear();
            if (list.Columns.Count != columns.Length)
            {
                list.Columns.Clear();
                foreach (string column in columns) list.Columns.Add(column);
            }
            list.Items.AddRange(items.ToArray());
            if (tab.SortColumn >= 0) list.ListViewItemSorter = new ListViewSorter(tab.SortColumn, tab.Descending);
            if (!tab.Fitted && items.Count > 0)
            {
                FitColumns(list, columns);
                tab.Fitted = true;
            }
            list.EndUpdate();

            string count = items.Count == tab.Table.Rows.Count
                ? Count(items.Count, tab.Noun)
                : string.Format("{0} of {1}", items.Count, Count(tab.Table.Rows.Count, tab.Noun));
            if (tab.Title == "Overview") count = "Overview";
            string note = string.IsNullOrEmpty(tab.Table.Note) ? string.Empty : "; " + tab.Table.Note;
            status.Text = string.Format("{0}{1}. Read at {2:t}{3}{4}", count, note, tab.ReadAt, tab.Error != null ? ". Refresh failed: " + tab.Error : string.Empty,
                string.IsNullOrEmpty(tab.ActionResult) ? string.Empty : ". " + tab.ActionResult.TrimEnd('.'));
        }

        private static string Count(int count, string noun) => count + " " + (count == 1 ? noun : noun + "s");

        /// <summary>Each column as wide as its header or its widest cell, whichever is wider, up to a limit.</summary>
        private void FitColumns(ListView list, string[] columns)
        {
            int limit = LogicalToDeviceUnits(420);
            for (int i = 0; i < list.Columns.Count; i++)
            {
                list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                int header = TextRenderer.MeasureText(columns[i] + " ▼", list.Font).Width + LogicalToDeviceUnits(12);
                list.Columns[i].Width = Math.Min(Math.Max(list.Columns[i].Width, header), limit);
            }
        }

        private void SortBy(Tab tab, int column)
        {
            if (tab.Table == null) return;
            tab.Descending = column == tab.SortColumn && !tab.Descending;
            tab.SortColumn = column;
            for (int i = 0; i < tab.List.Columns.Count && i < tab.Table.Columns.Length; i++)
            {
                tab.List.Columns[i].Text = tab.Table.Columns[i] + (i == column ? (tab.Descending ? " ▼" : " ▲") : string.Empty);
            }
            tab.List.ListViewItemSorter = new ListViewSorter(column, tab.Descending);
            tab.List.Sort();
        }

        /// <summary>The header, then the rows as the list shows them (filtered and sorted), or just the selected ones.</summary>
        private IEnumerable<string[]> Rows(Tab tab, bool selectedOnly)
        {
            yield return tab.Table.Columns;
            IEnumerable<ListViewItem> items = selectedOnly && tab.List.SelectedItems.Count > 0
                ? tab.List.SelectedItems.Cast<ListViewItem>()
                : tab.List.Items.Cast<ListViewItem>();
            foreach (ListViewItem item in items)
            {
                string[] row = new string[tab.Table.Columns.Length];
                for (int i = 0; i < row.Length; i++) row[i] = i < item.SubItems.Count ? item.SubItems[i].Text : string.Empty;
                yield return row;
            }
        }

        private void CopyRows(Tab tab, bool selectedOnly)
        {
            if (tab.Table == null) return;
            StringBuilder text = new StringBuilder();
            foreach (string[] row in Rows(tab, selectedOnly)) text.AppendLine(string.Join("\t", row));
            Clipboard.SetText(text.ToString());
        }

        private void Export(Tab tab)
        {
            if (tab.Table == null) return;
            using (SaveFileDialog save = new SaveFileDialog
            {
                Title = "Export " + tab.Title,
                Filter = "CSV files (*.csv)|*.csv",
                FileName = string.Format("{0} {1} {2:yyyy-MM-dd HHmm}.csv", host, tab.Title, DateTime.Now),
            })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder csv = new StringBuilder();
                foreach (string[] row in Rows(tab, false)) csv.AppendLine(string.Join(",", row.Select(DeviceProgressDialog.CsvField)));
                // With a byte order mark, Excel reads the file as UTF-8 instead of guessing.
                File.WriteAllText(save.FileName, csv.ToString(), new UTF8Encoding(true));
                log.InfoFormat("{0} {1} on {2}: exported {3} row(s) to {4}", windowTitle, tab.Title, host, tab.List.Items.Count, save.FileName);
            }
        }
    }
}
