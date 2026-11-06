using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using System.ServiceProcess;
using System.Reflection;
using System.Threading.Tasks;

namespace Layer8.Device
{
    public static class ClientActions
    {
        private static DeviceProgressDialog deviceProgressDialog;

        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ClientActions));

        // Find Software's last search, offered again by the next prompt. Runs get their own copy of the search.
        private static string softwareSearch;
        private static SynchronizationContext uiContext;

        // A single-device action has no progress dialog, so nothing can cancel it.
        private static CancellationToken CancelToken => deviceProgressDialog?.CancellationTokenSource?.Token ?? CancellationToken.None;

        /// <summary>Console entry point: runs RunClientActionCore behind Layer8's crash guard.</summary>
        public static void RunClientAction(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "RunClientAction", () => RunClientActionCore(sender, scopeNode, action, selectedResultObjects, dataUpdatedDelegate, status));
        }

        private static void RunClientActionCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            // DialogId is the schedule ID, plus ",full" (anything after a comma) for a full inventory.
            if (string.IsNullOrEmpty(action.DialogId))
            {
                throw new InvalidOperationException("The client action '" + action.DisplayName + "' has no schedule ID in the menu XML.");
            }
            string[] parts = action.DialogId.Split(',');
            string scheduleId = parts[0];
            bool fullScan = parts.Length > 1;

            ProcessAction(scopeNode, action, selectedResultObjects, nameof(ClientAction), device => { ClientAction(device, scheduleId, fullScan); return null; });
        }

        /// <summary>Console entry point: runs RunClientActionRestartServiceCore behind Layer8's crash guard.</summary>
        public static void RunClientActionRestartService(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "RunClientActionRestartService", () => RunClientActionRestartServiceCore(sender, scopeNode, action, selectedResultObjects, dataUpdatedDelegate, status));
        }

        private static void RunClientActionRestartServiceCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            ProcessAction(scopeNode, action, selectedResultObjects, nameof(RestartServiceAsync), device => RestartServiceAsync(device));
        }

        /// <summary>
        /// Console entry point for the client tools (Repair Client, Install Missing Updates, ...). The menu XML
        /// names the tool in DialogId, which must be one of the methods listed in ClientTools.
        /// These always show the progress dialog, so each device's answer is on screen.
        /// </summary>
        public static void RunClientTool(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "RunClientTool", () =>
            {
                string tool = action?.DialogId;
                if (tool == null || !ClientTools.TryGetValue(tool, out string confirmQuestion))
                {
                    throw new InvalidOperationException("Unknown client tool '" + tool + "' in the menu XML.");
                }

                string title = null;
                Func<IResultObject, object> run;
                if (tool == nameof(FindSoftware))
                {
                    string search = InputPrompt.Ask("Find Software", "Software name, or part of it (for example Chrome, 7-Zip or Office):", softwareSearch);
                    if (search == null) return;
                    softwareSearch = search;
                    title = "Find Software: " + search;
                    run = device => FindSoftware(device, search);
                }
                else
                {
                    MethodInfo toolMethod = typeof(ClientActions).GetMethod(tool, new[] { typeof(IResultObject) });
                    run = device => toolMethod.Invoke(null, new object[] { device });
                }
                ProcessAction(scopeNode, action, selectedResultObjects, tool, run, title, alwaysShowDialog: true, confirmQuestion: confirmQuestion);
            });
        }

        // Each client tool's method name, and the question to ask before running it ({0} is what it runs on),
        // or null to run without asking.
        private static readonly Dictionary<string, string> ClientTools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { nameof(OnlineStatus), null },
            { nameof(QuickStatus), null },
            { nameof(FindSoftware), null },
            { nameof(MissingUpdates), null },
            { nameof(CheckPendingRestart), null },
            { nameof(InstallMissingUpdates), "Install every missing software update on {0} now?\n\nThis installs the updates deployed to each device (required or available) that it reports as missing, without waiting for their deadlines. A device may restart afterwards, depending on the deployments' restart settings." },
            { nameof(FullMachinePolicyRefresh), null },
            { nameof(TurnOffProvisioningMode), null },
            { nameof(GroupPolicyUpdate), null },
            { nameof(ClientHealthCheck), null },
            { nameof(ClearClientCache), "Clear the ConfigMgr client cache on {0}?\n\nCached content is deleted, except content marked to stay in the cache and content a device is using right now. Devices download content again when they next need it." },
            { nameof(RepairClient), "Repair the ConfigMgr client on {0}?\n\nEach client runs ccmrepair, which restarts the SMS Agent Host service and can take several minutes. Watch ccmrepair.log on the device." },
            { nameof(ClientInfo), null },
            { nameof(RestartComputer), "Restart {0} in 5 minutes?\n\nWhoever is signed in gets a 5-minute warning, then Windows restarts even if programs have unsaved work. Cancel Restart stops it during those 5 minutes." },
        };

        private static void ProcessAction(ScopeNode scopeNode, ActionDescription action, IResultObject resultObjects, string actionName, Func<IResultObject, object> run,
                                          string title = null, bool alwaysShowDialog = false, string confirmQuestion = null)
        {
            bool showProgressDialog = alwaysShowDialog;
            if (resultObjects.ObjectClass == "SMS_Collection" || resultObjects.Count > 1)
            {
                showProgressDialog = true;
            }

            int total = resultObjects.Count;
            string targetDescription = total == 1 ? resultObjects["Name"].StringValue : total + " devices";

            if (resultObjects.ObjectClass == "SMS_Collection")
            {
                try
                {
                    string collectionName = resultObjects["Name"].StringValue;
                    total = 0;
                    string query = string.Format("SELECT * FROM SMS_FullCollectionMembership WHERE CollectionID='{0}'", resultObjects["CollectionID"].StringValue);
                    // query processor does not have count implemented?!?!?
                    foreach (IResultObject tmp in resultObjects.ConnectionManager.QueryProcessor.ExecuteQuery(query))
                    {
                        total++;
                    }

                    resultObjects = resultObjects.ConnectionManager.QueryProcessor.ExecuteQuery(query);
                    targetDescription = string.Format("the {0} device{1} in {2}", total, total == 1 ? "" : "s", collectionName);
                }
                catch (SmsQueryException ex)
                {
                    SccmExceptionDialog.ShowDialog(SnapIn.Console, ex);
                    return;
                }
            }

            if (confirmQuestion != null &&
                MessageBox.Show(string.Format(confirmQuestion, targetDescription), action.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            if (showProgressDialog)
            {
                deviceProgressDialog = new DeviceProgressDialog(action, resultObjects, actionName, run, title)
                {
                    Total = total
                };
                deviceProgressDialog.ShowDialog();
            }
            else
            {
                deviceProgressDialog = null;
                uiContext = SynchronizationContext.Current;
                foreach (IResultObject resultObject in resultObjects)
                {
                    IResultObject target = resultObject;
                    Task.Run(() => RunAction(target, actionName, run));
                }
            }

            return;
        }

        private static void RunAction(IResultObject resultObject, string actionName, Func<IResultObject, object> run)
        {
            string device = "(unknown device)";
            try
            {
                device = resultObject["Name"].StringValue;
                object result = run(resultObject);
                string outcome = WaitForResult(result);
                log.InfoFormat("{0} on {1}: {2}", actionName, device, outcome ?? "done");
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                log.Error(string.Format("{0} on {1} failed", actionName, device), root);
                ShowError(root);
            }
        }

        // Single-device actions run on a background thread; show any error back on the console's UI thread.
        private static void ShowError(Exception error)
        {
            SendOrPostCallback show = state =>
            {
                try
                {
                    SccmExceptionDialog.ShowDialog(SnapIn.Console, error);
                }
                catch (Exception ex)
                {
                    log.Error("Could not show the error dialog", ex);
                }
            };
            if (uiContext != null) uiContext.Post(show, null); else show(null);
        }

        /// <summary>
        /// Actions return nothing, a short status for the device's row ("No missing updates"), or a Task of
        /// either. Waits for tasks, so their errors are reported where the action ran instead of being lost.
        /// </summary>
        internal static string WaitForResult(object result)
        {
            if (result is Task<string> textTask) return textTask.GetAwaiter().GetResult();
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                return null;
            }
            return result as string;
        }

        /// <summary>
        /// Is the device reachable? Pings it twice, and when ping is blocked (Windows Firewall blocks it by
        /// default) tries port 445, the file-sharing port Browse C$ and the client logs use anyway.
        /// </summary>
        public static string OnlineStatus(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            IPAddress address = Resolve(host);
            using (Ping ping = new Ping())
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    PingReply reply = ping.Send(address, 1000);
                    if (reply.Status == IPStatus.Success)
                    {
                        return string.Format("Online: ping {0} ms ({1})", reply.RoundtripTime, address);
                    }
                }
            }

            if (PortAnswers(address, 445, 1000))
            {
                return string.Format("Online: ping blocked, port 445 answers ({0})", address);
            }
            throw new DeviceOfflineException(string.Format("Offline: no ping reply, port 445 closed ({0})", address));
        }

        /// <summary>The device's address from DNS, IPv4 when it has one (that's the address people recognise).</summary>
        private static IPAddress Resolve(string host)
        {
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                throw new DeviceOfflineException("Not found in DNS");
            }
            IPAddress best = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (best == null) throw new DeviceOfflineException("Not found in DNS");
            return best;
        }

        private static bool PortAnswers(IPAddress address, int port, int timeoutMs)
        {
            return AnyPortAnswers(address, new[] { port }, timeoutMs);
        }

        /// <summary>
        /// The quick offline check Layer8 Settings turns on: before connecting, does the device answer on port 135
        /// (WMI) or 445 (file sharing, and the service manager)? One that answers on neither is offline, or
        /// firewalled so nothing Layer8 does would reach it, and saying so takes a second or two instead of the
        /// 20 or more a WMI connection takes to give up.
        /// </summary>
        internal static void EnsureReachable(string host, int timeoutMs)
        {
            IPAddress address = Resolve(host);
            if (!AnyPortAnswers(address, new[] { 135, 445 }, timeoutMs))
            {
                throw new DeviceOfflineException(string.Format("Offline: ports 135 and 445 don't answer ({0})", address));
            }
        }

        /// <summary>
        /// Tries the ports together and returns as soon as one connects; false when all refuse or the time runs
        /// out. Waits on the connections themselves, so it doesn't tie up a thread per port.
        /// </summary>
        private static bool AnyPortAnswers(IPAddress address, int[] ports, int timeoutMs)
        {
            List<TcpClient> clients = new List<TcpClient>();
            try
            {
                List<Task> pending = new List<Task>();
                foreach (int port in ports)
                {
                    TcpClient client = new TcpClient(address.AddressFamily);
                    clients.Add(client);
                    Task connect = client.ConnectAsync(address, port);
                    // A connect still running when we give up fails later; observe that so it isn't reported as unhandled.
                    connect.ContinueWith(t => { Exception ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    pending.Add(connect);
                }

                DateTime giveUp = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (pending.Count > 0)
                {
                    int left = (int)(giveUp - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return false;
                    int done = Task.WaitAny(pending.ToArray(), left);
                    if (done < 0) return false;
                    if (pending[done].Status == TaskStatus.RanToCompletion) return true;
                    pending.RemoveAt(done);   // refused: wait on the others
                }
                return false;
            }
            finally
            {
                foreach (TcpClient client in clients) client.Close();
            }
        }

        /// <summary>The updates the client reports as missing (ComplianceState 0), by KB number.</summary>
        public static string MissingUpdates(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
            List<string> updates = new List<string>();
            foreach (ManagementObject update in Utility.SearchWMIToList(clientSdk, "SELECT ArticleID, Name FROM CCM_SoftwareUpdate WHERE ComplianceState = 0"))
            {
                using (update)
                {
                    string article = update["ArticleID"] as string;
                    updates.Add(!string.IsNullOrEmpty(article) ? "KB" + article : (update["Name"] as string ?? "(unnamed update)"));
                }
            }
            if (updates.Count == 0) return "No missing updates";
            if (updates.Count > ShownInRow) log.InfoFormat("MissingUpdates on {0}, all {1}: {2}", host, updates.Count, string.Join(", ", updates));
            return string.Format("{0} missing: {1}", updates.Count, Summarize(updates));
        }

        /// <summary>
        /// Is the software Find Software asked for installed, and which version? Reads the ConfigMgr client's
        /// view of Add/Remove Programs (Win32Reg_AddRemovePrograms and its 64-bit twin), which matches what
        /// hardware inventory reports, but live.
        /// </summary>
        public static string FindSoftware(IResultObject resultObject, string search)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");

            List<string> found = new List<string>();
            ManagementException missingClass = null;
            int classesRead = 0;
            foreach (string inventoryClass in new[] { "Win32Reg_AddRemovePrograms64", "Win32Reg_AddRemovePrograms" })
            {
                string query = string.Format("SELECT DisplayName, Version FROM {0} WHERE DisplayName LIKE '%{1}%'", inventoryClass, WqlLikeText(search));
                try
                {
                    foreach (ManagementObject program in Utility.SearchWMIToList(cimv2, query))
                    {
                        using (program)
                        {
                            string entry = ((program["DisplayName"] as string) + " " + (program["Version"] as string)).Trim();
                            if (!found.Contains(entry, StringComparer.OrdinalIgnoreCase)) found.Add(entry);
                        }
                    }
                    classesRead++;
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass)
                {
                    missingClass = ex;   // the 64-bit class only exists on 64-bit Windows
                }
            }
            if (classesRead == 0 && missingClass != null) throw missingClass;   // neither class: no ConfigMgr client

            if (found.Count == 0) return "Not installed";
            found.Sort(StringComparer.OrdinalIgnoreCase);
            if (found.Count > ShownInRow) log.InfoFormat("FindSoftware '{0}' on {1}, all {2}: {3}", search, host, found.Count, string.Join("; ", found));
            return "Installed: " + Summarize(found, "; ");
        }

        // Rows stay readable: list this many items, then "(+N more)". The log gets the full list.
        private const int ShownInRow = 5;

        private static string Summarize(List<string> items, string separator = ", ")
        {
            if (items.Count <= ShownInRow) return string.Join(separator, items);
            return string.Join(separator, items.Take(ShownInRow)) + string.Format(" (+{0} more)", items.Count - ShownInRow);
        }

        /// <summary>Text for inside a WQL LIKE '%...%': wildcards and brackets taken literally, quotes escaped.</summary>
        internal static string WqlLikeText(string text)
        {
            System.Text.StringBuilder escaped = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                switch (c)
                {
                    case '[': escaped.Append("[[]"); break;
                    case '%': escaped.Append("[%]"); break;
                    case '_': escaped.Append("[_]"); break;
                    case '\\': escaped.Append(@"\\"); break;
                    case '\'': escaped.Append(@"\'"); break;
                    default: escaped.Append(c); break;
                }
            }
            return escaped.ToString();
        }

        /// <summary>
        /// One line per device: who's signed in, time since the last restart, and the ConfigMgr client version.
        /// Signed-in users are the owners of explorer.exe, which covers console and Remote Desktop sessions alike
        /// (Win32_ComputerSystem.UserName only knows the console).
        /// </summary>
        public static string QuickStatus(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            List<string> parts = new List<string>();

            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            List<string> users = SignedInUsers(cimv2);
            parts.Add(users.Count == 0 ? "Nobody signed in" : "Signed in: " + string.Join(", ", users));

            ManagementObject os = Utility.GetFirstWMIInstance(cimv2, "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            DateTime? lastBoot = ToDateTime(os?["LastBootUpTime"]);
            if (lastBoot.HasValue) parts.Add("up " + FormatUptime(DateTime.Now - lastBoot.Value));

            // A full system drive is a common reason updates and installs fail.
            ManagementObject systemDrive = Utility.GetFirstWMIInstance(cimv2, "SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DeviceID = 'C:'");
            if (systemDrive?["FreeSpace"] != null && systemDrive["Size"] != null)
            {
                parts.Add(string.Format("C: {0} free of {1}", Utility.FormatBytes(Convert.ToDouble(systemDrive["FreeSpace"]), "0.#"), Utility.FormatBytes(Convert.ToDouble(systemDrive["Size"]), "0")));
            }

            try
            {
                ManagementScope ccm = Utility.GetWMIScope(host, "ccm");
                ManagementObject client = Utility.GetFirstWMIInstance(ccm, "SELECT ClientVersion FROM SMS_Client");
                string version = client?["ClientVersion"] as string;
                parts.Add(string.IsNullOrEmpty(version) ? "client version unknown" : "client " + version);
            }
            catch (ManagementException ex) when (IsNoClient(ex))
            {
                parts.Add("no ConfigMgr client");
            }

            return string.Join("; ", parts);
        }

        /// <summary>
        /// True when a WMI error means the ConfigMgr client isn't there: no root\ccm namespace, or (on a site
        /// server, which has root\ccm without a client) no client class.
        /// </summary>
        internal static bool IsNoClient(ManagementException ex)
        {
            return ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass;
        }

        private static List<string> SignedInUsers(ManagementScope cimv2)
        {
            List<string> users = new List<string>();
            foreach (ManagementObject explorer in Utility.SearchWMIToList(cimv2, "SELECT Handle FROM Win32_Process WHERE Name = 'explorer.exe'"))
            {
                using (explorer)
                using (ManagementBaseObject owner = explorer.InvokeMethod("GetOwner", null, null))
                {
                    string user = owner?["User"] as string;
                    if (string.IsNullOrEmpty(user)) continue;
                    string domain = owner["Domain"] as string;
                    string name = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
                    if (!users.Contains(name, StringComparer.OrdinalIgnoreCase)) users.Add(name);
                }
            }
            return users;
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1) return string.Format("{0}d {1}h", (int)uptime.TotalDays, uptime.Hours);
            if (uptime.TotalHours >= 1) return string.Format("{0}h {1}m", (int)uptime.TotalHours, uptime.Minutes);
            return string.Format("{0}m", Math.Max(0, (int)uptime.TotalMinutes));
        }

        /// <summary>Asks the client whether it's waiting on a restart: ConfigMgr's own view, plus Windows servicing and Windows Update.</summary>
        public static string CheckPendingRestart(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            List<string> reasons = new List<string>();

            ManagementScope clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
            using (ManagementClass utilities = new ManagementClass(clientSdk, new ManagementPath("CCM_ClientUtilities"), null))
            using (ManagementBaseObject outParams = utilities.InvokeMethod("DetermineIfRebootPending", null, null))
            {
                if (IsTrue(outParams["RebootPending"]) || IsTrue(outParams["IsHardRebootPending"]))
                {
                    string reason = IsTrue(outParams["IsHardRebootPending"]) ? "ConfigMgr (hard restart)" : "ConfigMgr";
                    DateTime? deadline = ToDateTime(outParams["RebootDeadline"]);
                    if (deadline.HasValue) reason += ", deadline " + deadline.Value.ToString("g");
                    reasons.Add(reason);
                }
            }

            // The same registry keys Windows itself uses, read through WMI's registry provider.
            ManagementScope registryScope = Utility.GetWMIScope(host, "default");
            using (ManagementClass registry = new ManagementClass(registryScope, new ManagementPath("StdRegProv"), null))
            {
                if (HasSubKey(registry, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing", "RebootPending"))
                    reasons.Add("Windows servicing");
                if (HasSubKey(registry, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update", "RebootRequired"))
                    reasons.Add("Windows Update");
            }

            return reasons.Count == 0 ? "No restart pending" : "Restart pending: " + string.Join("; ", reasons);
        }

        /// <summary>Installs every update the client reports as missing (ComplianceState 0) from its deployments.</summary>
        public static string InstallMissingUpdates(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
            List<ManagementObject> missing = Utility.SearchWMIToList(clientSdk, "SELECT * FROM CCM_SoftwareUpdate WHERE ComplianceState = 0");
            if (missing.Count == 0)
            {
                return "No missing updates";
            }

            using (ManagementClass manager = new ManagementClass(clientSdk, new ManagementPath("CCM_SoftwareUpdatesManager"), null))
            using (ManagementBaseObject inParams = manager.GetMethodParameters("InstallUpdates"))
            {
                inParams["CCMUpdates"] = missing.ToArray();
                using (ManagementBaseObject outParams = manager.InvokeMethod("InstallUpdates", inParams, null))
                {
                    CheckReturnValue("InstallUpdates", outParams);
                }
            }
            return string.Format("Installing {0} update{1}", missing.Count, missing.Count == 1 ? "" : "s");
        }

        /// <summary>
        /// Makes the next policy request a full one instead of only the changes (SMS_Client.ResetPolicy 0), then
        /// requests and evaluates machine policy. Doesn't purge policy: Microsoft warns a purge (flag 1) can make
        /// deployed programs run again.
        /// </summary>
        public static string FullMachinePolicyRefresh(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ObjectGetOptions options = new ObjectGetOptions(null, TimeSpan.FromSeconds(5), true);
            using (ManagementClass client = new ManagementClass(string.Format(@"\\{0}\root\ccm:SMS_Client", host), options))
            {
                client.InvokeMethod("ResetPolicy", new object[] { 0u });
                client.InvokeMethod("TriggerSchedule", new object[] { "{00000000-0000-0000-0000-000000000021}" });   // request machine policy
                client.InvokeMethod("TriggerSchedule", new object[] { "{00000000-0000-0000-0000-000000000022}" });   // evaluate machine policy
            }
            return "Full policy requested";
        }

        /// <summary>Starts ccmrepair on the client (SMS_Client.RepairClient). It runs on its own for several minutes.</summary>
        public static string RepairClient(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ObjectGetOptions options = new ObjectGetOptions(null, TimeSpan.FromSeconds(5), true);
            using (ManagementClass client = new ManagementClass(string.Format(@"\\{0}\root\ccm:SMS_Client", host), options))
            {
                client.InvokeMethod("RepairClient", null);
            }
            return "Repair started (see ccmrepair.log)";
        }

        /// <summary>
        /// Takes the client out of provisioning mode (SMS_Client.SetClientProvisioningMode false). A client left
        /// in it after a task sequence ignores policy, so nothing deploys to it.
        /// </summary>
        public static string TurnOffProvisioningMode(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;

            // Read the current state first so the result says whether this changed anything.
            bool wasOn;
            ManagementScope registryScope = Utility.GetWMIScope(host, "default");
            using (ManagementClass registry = new ManagementClass(registryScope, new ManagementPath("StdRegProv"), null))
            using (ManagementBaseObject inParams = registry.GetMethodParameters("GetStringValue"))
            {
                inParams["hDefKey"] = 0x80000002u;   // HKEY_LOCAL_MACHINE
                inParams["sSubKeyName"] = @"SOFTWARE\Microsoft\CCM\CcmExec";
                inParams["sValueName"] = "ProvisioningMode";
                using (ManagementBaseObject outParams = registry.InvokeMethod("GetStringValue", inParams, null))
                {
                    wasOn = string.Equals(outParams["sValue"] as string, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            ObjectGetOptions options = new ObjectGetOptions(null, TimeSpan.FromSeconds(5), true);
            using (ManagementClass client = new ManagementClass(string.Format(@"\\{0}\root\ccm:SMS_Client", host), options))
            {
                client.InvokeMethod("SetClientProvisioningMode", new object[] { false });
            }
            return wasOn ? "Was in provisioning mode; turned it off" : "Wasn't in provisioning mode";
        }

        /// <summary>Retry Failed Devices for an application: the client re-evaluates its application deployments now.</summary>
        public static string RetryApplication(IResultObject resultObject)
        {
            TriggerSchedules(resultObject["Name"].StringValue, "{00000000-0000-0000-0000-000000000121}");
            return "Application evaluation started";
        }

        /// <summary>Retry Failed Devices for software updates: a fresh scan, then the update deployments are re-evaluated.</summary>
        public static string RetryUpdates(IResultObject resultObject)
        {
            TriggerSchedules(resultObject["Name"].StringValue, "{00000000-0000-0000-0000-000000000113}", "{00000000-0000-0000-0000-000000000108}");
            return "Update scan and evaluation started";
        }

        private static void TriggerSchedules(string host, params string[] scheduleIds)
        {
            ObjectGetOptions options = new ObjectGetOptions(null, TimeSpan.FromSeconds(5), true);
            using (ManagementClass client = new ManagementClass(string.Format(@"\\{0}\root\ccm:SMS_Client", host), options))
            {
                foreach (string scheduleId in scheduleIds)
                {
                    client.InvokeMethod("TriggerSchedule", new object[] { scheduleId });
                }
            }
        }

        /// <summary>
        /// Deletes the client's cached content (folders and CacheInfoEx entries), the same way the Client Cache
        /// window does, but for every device at once. Content marked to persist in the cache stays; content in use
        /// can't be deleted and is counted as skipped.
        /// </summary>
        public static string ClearClientCache(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope scope = Utility.GetWMIScope(host, @"ccm\SoftMgmtAgent");
            int cleared = 0, kept = 0, skipped = 0;
            double freedKilobytes = 0;
            foreach (ManagementObject item in Utility.SearchWMIToList(scope, "SELECT * FROM CacheInfoEx"))
            {
                using (item)
                {
                    if (Convert.ToInt64(item["PersistInCache"] ?? 0) != 0)
                    {
                        kept++;
                        continue;
                    }
                    string location = item["Location"] as string;
                    try
                    {
                        if (!string.IsNullOrEmpty(location))
                        {
                            string share = string.Format(@"\\{0}\{1}", host, location.Replace(':', '$'));
                            if (Directory.Exists(share)) Directory.Delete(share, true);
                        }
                        item.Delete();
                        cleared++;
                        freedKilobytes += Convert.ToDouble(item["ContentSize"] ?? 0);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        skipped++;
                        log.WarnFormat("ClearClientCache on {0}: kept {1}: {2}", host, location, ex.Message);
                    }
                }
            }

            if (cleared == 0 && skipped == 0) return kept > 0 ? string.Format("Nothing to clear ({0} item(s) kept on purpose)", kept) : "Cache already empty";
            string result = string.Format("Cleared {0} item(s), {1} freed", cleared, Utility.FormatBytes(freedKilobytes * Utility.BytesPerKilobyte, "0.#"));
            if (kept > 0) result += string.Format("; {0} kept on purpose", kept);
            if (skipped > 0) result += string.Format("; {0} in use or access denied", skipped);
            return result;
        }

        /// <summary>
        /// Runs gpupdate for computer policy on the device. "echo n" answers the restart question some policies
        /// ask, so it never waits on a prompt nobody can see. User policy isn't refreshed: that needs the user's own session.
        /// </summary>
        public static string GroupPolicyUpdate(IResultObject resultObject)
        {
            StartRemoteProcess(resultObject["Name"].StringValue, "cmd.exe /c echo n | gpupdate.exe /target:computer /force", "gpupdate");
            return "Computer policy update started";
        }

        /// <summary>Runs the client's own health check (CcmEval.exe), which repairs what it can and writes CcmEval.log.</summary>
        public static string ClientHealthCheck(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementObject os = Utility.GetFirstWMIInstance(Utility.GetWMIScope(host, "cimv2"), "SELECT WindowsDirectory FROM Win32_OperatingSystem");
            string windows = os?["WindowsDirectory"] as string ?? @"C:\Windows";
            StartRemoteProcess(host, "\"" + Path.Combine(windows, @"CCM\CcmEval.exe") + "\"", "CcmEval.exe");
            return "Health check started (see CcmEval.log)";
        }

        /// <summary>Starts a program on the device through WMI (Win32_Process.Create); doesn't wait for it to finish.</summary>
        private static void StartRemoteProcess(string host, string commandLine, string programName)
        {
            using (ManagementClass process = new ManagementClass(Utility.GetWMIScope(host, "cimv2"), new ManagementPath("Win32_Process"), null))
            using (ManagementBaseObject inParams = process.GetMethodParameters("Create"))
            {
                inParams["CommandLine"] = commandLine;
                using (ManagementBaseObject outParams = process.InvokeMethod("Create", inParams, null))
                {
                    long code = Convert.ToInt64(outParams?["ReturnValue"] ?? 0);
                    switch (code)
                    {
                        case 0: return;
                        case 2: throw new UnauthorizedAccessException("Access denied starting " + programName + " on " + host + ".");
                        case 9: throw new FileNotFoundException(programName + " isn't on " + host + ".");
                        default: throw new InvalidOperationException(string.Format("Starting {0} on {1} failed (Win32_Process.Create returned {2}).", programName, host, code));
                    }
                }
            }
        }

        private static bool HasSubKey(ManagementClass registry, string keyPath, string subKey)
        {
            const uint HKEY_LOCAL_MACHINE = 0x80000002;
            using (ManagementBaseObject inParams = registry.GetMethodParameters("EnumKey"))
            {
                inParams["hDefKey"] = HKEY_LOCAL_MACHINE;
                inParams["sSubKeyName"] = keyPath;
                using (ManagementBaseObject outParams = registry.InvokeMethod("EnumKey", inParams, null))
                {
                    // A missing key returns an error code and no names: that just means "not pending".
                    string[] names = outParams["sNames"] as string[];
                    return names != null && Array.Exists(names, n => string.Equals(n, subKey, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        private static void CheckReturnValue(string methodName, ManagementBaseObject outParams)
        {
            object value = outParams?["ReturnValue"];
            if (value != null && Convert.ToInt64(value) != 0)
            {
                throw new InvalidOperationException(string.Format("{0} returned error {1} (0x{1:X}).", methodName, Convert.ToInt64(value)));
            }
        }

        private static bool IsTrue(object value)
        {
            return value is bool flag && flag;
        }

        private static DateTime? ToDateTime(object dmtfValue)
        {
            string text = dmtfValue as string;
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                DateTime value = ManagementDateTimeConverter.ToDateTime(text);
                return value.Year > 2000 ? value : (DateTime?)null;   // no deadline comes back as 1970
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Site, management point, client version and client GUID, straight from the client.</summary>
        public static string ClientInfo(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope ccm = Utility.GetWMIScope(host, "ccm");
            List<string> parts = new List<string>();
            using (ManagementObject authority = Utility.GetFirstWMIInstance(ccm, "SELECT Name, CurrentManagementPoint FROM SMS_Authority"))
            {
                if (authority != null) parts.Add(string.Format("site {0} via {1}", (authority["Name"] as string ?? "").Replace("SMS:", ""), authority["CurrentManagementPoint"]));
            }
            using (ManagementObject client = Utility.GetFirstWMIInstance(ccm, "SELECT ClientVersion FROM SMS_Client"))
            {
                if (client != null) parts.Add("client " + client["ClientVersion"]);
            }
            using (ManagementObject identity = Utility.GetFirstWMIInstance(ccm, "SELECT ClientId FROM CCM_Client"))
            {
                if (identity != null) parts.Add(identity["ClientId"] as string);
            }
            return parts.Count == 0 ? "No client details" : string.Join("; ", parts);
        }

        /// <summary>Restarts the device after a 5-minute warning to whoever is signed in.</summary>
        public static string RestartComputer(IResultObject resultObject)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope cimv2 = Utility.GetWMIScope(host, "cimv2");
            using (ManagementObject os = Utility.GetFirstWMIInstance(cimv2, "SELECT * FROM Win32_OperatingSystem"))
            {
                if (os == null) throw new InvalidOperationException("No Win32_OperatingSystem on this device");
                using (ManagementBaseObject inParams = os.GetMethodParameters("Win32ShutdownTracker"))
                {
                    inParams["Timeout"] = 300u;                                  // seconds of warning
                    inParams["Comment"] = "IT is restarting this computer in 5 minutes. Save your work now.";
                    inParams["ReasonCode"] = 0x80040001u;                        // planned, application, maintenance
                    inParams["Flags"] = 6;                                       // 2 restart + 4 force
                    using (ManagementBaseObject outParams = os.InvokeMethod("Win32ShutdownTracker", inParams, null))
                    {
                        uint code = Convert.ToUInt32(outParams["ReturnValue"]);
                        if (code != 0) throw new InvalidOperationException(string.Format("Windows refused the restart (Win32ShutdownTracker returned {0})", code));
                    }
                }
            }
            log.InfoFormat("Restart requested on {0}, in 5 minutes", host);
            return "Restarting in 5 minutes";
        }

        public static void ClientAction(IResultObject resultObject, string scheduleId, bool fullScan)
        {
            try
            {
                if (fullScan)
                {
                    ManagementScope scope = Utility.GetWMIScope(resultObject["Name"].StringValue, @"ccm\InvAgt");
                    ManagementObject result = Utility.GetFirstWMIInstance(scope, string.Format("SELECT * FROM InventoryActionStatus WHERE InventoryActionID = '{0}'", scheduleId));
                    if (result != null)
                        result.Delete();
                }

                ObjectGetOptions o = new ObjectGetOptions(null, TimeSpan.FromSeconds(5), true);
                using (ManagementClass clientaction = new ManagementClass(string.Format(@"\\{0}\root\ccm:SMS_Client", resultObject["Name"].StringValue), o))
                {
                    object[] methodArgs = { scheduleId };
                    clientaction.InvokeMethod("TriggerSchedule", methodArgs);
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        public static async Task RestartServiceAsync(IResultObject resultObject)
        {
            using (ServiceController service = new ServiceController("SMS Agent Host", resultObject["Name"].StringValue))
            {
                if (service.Status == ServiceControllerStatus.Running)
                {
                    service.Stop();

                    await service.WaitForStatusAsync(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20), CancelToken);

                    service.Start();
                }
                else if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                }
            }
        }
    }

    /// <summary>
    /// A device that can't be reached, as an expected answer rather than a fault: the progress dialog shows the
    /// message and counts the device as offline.
    /// </summary>
    public sealed class DeviceOfflineException : Exception
    {
        public DeviceOfflineException(string message) : base(message) { }
    }

    public static class ServiceControllerExtensions
    {
        public static async Task WaitForStatusAsync(this ServiceController controller, ServiceControllerStatus desiredStatus, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var utcNow = DateTime.UtcNow;
            controller.Refresh();
            while (controller.Status != desiredStatus)
            {
                if (DateTime.UtcNow - utcNow > timeout)
                {
                    throw new System.TimeoutException($"Failed to wait for '{controller.ServiceName}' to change status to '{desiredStatus}'.");
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                controller.Refresh();
            }
        }
    }
}
