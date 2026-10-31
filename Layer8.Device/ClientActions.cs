using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System;
using System.Collections.Generic;
using System.Management;
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
        private static string scheduleId;
        private static bool fullScan = false;
        private static string method;

        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ClientActions));
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
            // set the thread pool method to run
            method = "ClientAction";
            // check if we need full scan
            if (!string.IsNullOrEmpty(action.DialogId))
            {
                string[] tmp = action.DialogId.Split(',');
                scheduleId = tmp[0];
                fullScan = tmp.Length > 1 ? true : false;
            }

            ProcessAction(scopeNode, action, selectedResultObjects);
        }

        /// <summary>Console entry point: runs RunClientActionRestartServiceCore behind Layer8's crash guard.</summary>
        public static void RunClientActionRestartService(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "RunClientActionRestartService", () => RunClientActionRestartServiceCore(sender, scopeNode, action, selectedResultObjects, dataUpdatedDelegate, status));
        }

        private static void RunClientActionRestartServiceCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            // set the thread pool method to run
            method = "RestartServiceAsync";
            ProcessAction(scopeNode, action, selectedResultObjects);
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
                method = tool;
                ProcessAction(scopeNode, action, selectedResultObjects, alwaysShowDialog: true, confirmQuestion: confirmQuestion);
            });
        }

        // Each client tool's method name, and the question to ask before running it ({0} is what it runs on),
        // or null to run without asking.
        private static readonly Dictionary<string, string> ClientTools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { nameof(CheckPendingRestart), null },
            { nameof(InstallMissingUpdates), "Install every missing software update on {0} now?\n\nThis installs the updates deployed to each device (required or available) that it reports as missing, without waiting for their deadlines. A device may restart afterwards, depending on the deployments' restart settings." },
            { nameof(FullMachinePolicyRefresh), null },
            { nameof(TurnOffProvisioningMode), null },
            { nameof(RepairClient), "Repair the ConfigMgr client on {0}?\n\nEach client runs ccmrepair, which restarts the SMS Agent Host service and can take several minutes. Watch ccmrepair.log on the device." },
        };

        private static void ProcessAction(ScopeNode scopeNode, ActionDescription action, IResultObject resultObjects, bool alwaysShowDialog = false, string confirmQuestion = null)
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
                Type type = typeof(ClientActions);
                MethodInfo methodInfo = type.GetMethod(method);

                deviceProgressDialog = new DeviceProgressDialog(action, resultObjects, methodInfo)
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
                    Task.Run(() => RunAction(target));
                }
            }

            return;
        }

        private static void RunAction(IResultObject resultObject)
        {
            string device = "(unknown device)";
            try
            {
                device = resultObject["Name"].StringValue;
                object result = typeof(ClientActions).GetMethod(method).Invoke(null, new object[] { resultObject });
                string outcome = WaitForResult(result);
                log.InfoFormat("{0} on {1}: {2}", method, device, outcome ?? "done");
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                log.Error(string.Format("{0} on {1} failed", method, device), root);
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

        public static void ClientAction(IResultObject resultObject)
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
