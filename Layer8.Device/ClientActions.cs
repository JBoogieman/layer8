using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System;
using System.Management;
using System.Threading;
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

        private static readonly log4net.ILog log = Layer8.Layer8Log.GetLogger(typeof(ClientActions));
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

        private static void ProcessAction(ScopeNode scopeNode, ActionDescription action, IResultObject resultObjects)
        {
            bool showProgressDialog = false;
            if (resultObjects.ObjectClass == "SMS_Collection" || resultObjects.Count > 1)
            {
                showProgressDialog = true;
            }

            int total = resultObjects.Count;

            if (resultObjects.ObjectClass == "SMS_Collection")
            {
                try
                {
                    total = 0;
                    string query = string.Format("SELECT * FROM SMS_FullCollectionMembership WHERE CollectionID='{0}'", resultObjects["CollectionID"].StringValue);
                    // query processor does not have count implemented?!?!?
                    foreach (IResultObject tmp in resultObjects.ConnectionManager.QueryProcessor.ExecuteQuery(query))
                    {
                        total++;
                    }

                    resultObjects = resultObjects.ConnectionManager.QueryProcessor.ExecuteQuery(query);
                }
                catch (SmsQueryException ex)
                {
                    SccmExceptionDialog.ShowDialog(SnapIn.Console, ex);
                    return;
                }
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
                // Async actions (like restarting the client service) return a Task; wait for it so errors aren't lost.
                (result as Task)?.GetAwaiter().GetResult();
                log.InfoFormat("{0} on {1}: done", method, device);
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
