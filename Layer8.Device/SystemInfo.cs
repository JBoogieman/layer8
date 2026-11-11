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
    /// Read-only: nothing on the device changes.
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

        public void Add(params InfoCell[] cells) => Rows.Add(cells);
    }

    /// <summary>Reads each tab from the device. Every method runs off the UI thread.</summary>
    internal static class SystemInfoReader
    {
        private const uint HKEY_LOCAL_MACHINE = 0x80000002;

        /// <summary>
        /// How long a device gets to answer one query. Without a limit a tab could say "Reading..." forever: in the
        /// lab, Win32_QuickFixEngineering didn't answer at all on one Windows 11 build.
        /// </summary>
        internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

        private static List<ManagementObject> Query(ManagementScope scope, string wql)
        {
            EnumerationOptions options = new EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options))
            {
                return searcher.Get().Cast<ManagementObject>().ToList();
            }
        }

        private static ManagementObject First(ManagementScope scope, string wql)
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
        /// check, and sometimes repair, every MSI on the device. Asked from the console (a 32-bit program), the
        /// 64-bit and 32-bit classes came back with the same list in the lab, so each program is listed once and
        /// without a 64-bit or 32-bit label that couldn't be trusted.
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
                    foreach (ManagementObject program in Query(cimv2, "SELECT DisplayName, Version, Publisher, InstallDate FROM " + inventoryClass))
                    {
                        using (program)
                        {
                            string name = (program["DisplayName"] as string)?.Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            string version = program["Version"] as string;
                            if (!seen.Add(name + "|" + version)) continue;
                            table.Add(name, version, program["Publisher"] as string, InfoCell.Date(InstallDate(program["InstallDate"] as string)));
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
        private static object RegistryValue(string host, string method, string key, string name)
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

        private static string Join(string separator, params string[] parts)
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
        }

        private readonly string host;
        private readonly TabControl tabs;
        private readonly TextBox filter;
        private readonly Label status;
        private readonly List<Tab> tabList = new List<Tab>();

        public SystemInfoDialog(string host)
        {
            this.host = host;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = "System Information: " + host;
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

            AddTab("Overview", "item", SystemInfoReader.Overview);
            AddTab("Programs", "program", SystemInfoReader.Programs);
            AddTab("Updates", "update", SystemInfoReader.Updates);
            AddTab("Services", "service", SystemInfoReader.Services);
            AddTab("Drivers", "driver", SystemInfoReader.Drivers);
            AddTab("Profiles", "profile", SystemInfoReader.Profiles);
            AddTab("Administrators", "member", SystemInfoReader.Administrators);

            tabs.SelectedIndexChanged += (s, e) => { LoadIfNeeded(Current); ShowTab(Current); };
            filter.TextChanged += (s, e) => ShowTab(Current);
            refresh.Click += (s, e) => Load(Current);
            copy.Click += (s, e) => CopyRows(Current, false);
            export.Click += (s, e) => Export(Current);
            Shown += (s, e) => LoadIfNeeded(Current);

            Controls.AddRange(new Control[] { filterLabel, filter, tabs, status, refresh, copy, export, close });
            CancelButton = close;
            ResumeLayout(false);
        }

        private Tab Current => tabList[Math.Max(0, tabs.SelectedIndex)];

        private void AddTab(string title, string noun, Func<string, InfoTable> read)
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
            log.InfoFormat("System Information {0} on {1}: {2}", tab.Title, host, error ?? Count(table.Rows.Count, tab.Noun));
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
                ListViewItem item = new ListViewItem(row.Length > 0 ? row[0].Text : string.Empty);
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
            status.Text = string.Format("{0}{1}. Read at {2:t}{3}", count, note, tab.ReadAt, tab.Error != null ? ". Refresh failed: " + tab.Error : string.Empty);
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
                log.InfoFormat("System Information {0} on {1}: exported {2} row(s) to {3}", tab.Title, host, tab.List.Items.Count, save.FileName);
            }
        }
    }
}
