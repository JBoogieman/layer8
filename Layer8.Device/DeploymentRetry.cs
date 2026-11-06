using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Monitoring > Deployments: find the devices in Error for an application or software update deployment,
    /// then retry them all (each re-runs the client's evaluation for that kind of deployment) or copy their names.
    /// Status comes from the site's deployment status classes, so it's as fresh as the last summarization.
    /// </summary>
    public static class DeploymentRetry
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(DeploymentRetry));

        // SMS_DeploymentSummary.FeatureType values this works for.
        private const int ApplicationDeployment = 1;
        private const int SoftwareUpdateDeployment = 5;

        // SMS_DeploymentSummary.DeploymentIntent: 1 Required, 2 Available.
        private const int AvailableIntent = 2;

        // The status classes' "Error" value (AppStatusType for applications, StatusType for software updates).
        private const int ErrorStatus = 5;

        // Device IDs per SMS_R_System query, keeping each WQL statement a sensible length.
        private const int IdsPerQuery = 300;

        /// <summary>Console entry point: retries every device in Error for the selected deployment.</summary>
        public static void RetryFailedDevices(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Retry Failed Devices", () => RetryCore(action, selectedResultObjects));
        }

        /// <summary>Console entry point: copies the names of the devices in Error for the selected deployment.</summary>
        public static void CopyFailedDevices(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Copy Failed Devices", () => CopyCore(selectedResultObjects));
        }

        private sealed class FailedDevices
        {
            public string Software;
            public string Collection;
            // What Retry does on each device, its name for the log, and how the confirmation describes it.
            public string RetryName;
            public Func<IResultObject, object> Retry;
            public string RetryDescription;
            // Set when there's nothing to do: the deployment type isn't supported, or no device is in error.
            public string Problem;
            public readonly List<int> Ids = new List<int>();
            public readonly List<string> Names = new List<string>();
            // By device ID: the error code, and the status text where the site has one (software updates do).
            public readonly Dictionary<int, uint> ErrorCodes = new Dictionary<int, uint>();
            public readonly Dictionary<int, string> Descriptions = new Dictionary<int, string>();
        }

        private static void RetryCore(ActionDescription action, IResultObject deployment)
        {
            FailedDevices failed = FindFailed(deployment);
            if (ShowProblem(failed)) return;

            string question = string.Format(
                "Retry {0} on the {1} device{2} in error ({3})?\n\nEach device {4}. Deployment status catches up at the next summarization.",
                failed.Software, failed.Ids.Count, failed.Ids.Count == 1 ? "" : "s", failed.Collection, failed.RetryDescription);
            if (MessageBox.Show(question, "Retry Failed Devices", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<IResultObject> devices = DevicesById(deployment.ConnectionManager, failed.Ids);
            log.InfoFormat("Retrying {0} ({1}) on {2} device(s) in error", failed.Software, failed.Collection, devices.Count);

            using (DeviceProgressDialog dialog = new DeviceProgressDialog(action, devices, failed.RetryName, failed.Retry, "Retry Failed Devices: " + failed.Software))
            {
                dialog.Total = devices.Count;
                dialog.ShowDialog();
            }
        }

        /// <summary>
        /// Copies each device in error with its error code and what the code means, as tab-separated rows for Excel,
        /// and shows the errors grouped by code, so the reason most devices failed is the first thing you see.
        /// </summary>
        private static void CopyCore(IResultObject deployment)
        {
            FailedDevices failed = FindFailed(deployment, withErrorCodes: true);
            if (ShowProblem(failed)) return;

            var rows = failed.Ids.Select((id, i) => new
            {
                Name = failed.Names[i] ?? id.ToString(),
                Code = failed.ErrorCodes.TryGetValue(id, out uint code) ? code : (uint?)null,
                Text = failed.Descriptions.TryGetValue(id, out string text) && !string.IsNullOrWhiteSpace(text) ? text : null,
            })
            .Select(r => new { r.Name, r.Code, Text = r.Text ?? (r.Code.HasValue ? ErrorText(r.Code.Value) : "No error code reported") })
            .OrderBy(r => r.Code ?? uint.MaxValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            StringBuilder table = new StringBuilder("Device\tError code\tError").AppendLine();
            foreach (var row in rows)
            {
                table.Append(row.Name).Append('\t').Append(row.Code.HasValue ? FormatCode(row.Code.Value) : "").Append('\t').Append(row.Text).AppendLine();
            }
            Clipboard.SetDataObject(table.ToString(), true, 10, 100);

            // Most common error first.
            var groups = rows.GroupBy(r => new { r.Code, r.Text }).OrderByDescending(g => g.Count()).ToList();
            StringBuilder summary = new StringBuilder();
            summary.AppendFormat("Copied {0} device{1} in error for {2}, with error codes (pastes into Excel).", rows.Count, rows.Count == 1 ? "" : "s", failed.Software).AppendLine().AppendLine();
            foreach (var group in groups.Take(10))
            {
                summary.AppendFormat("{0} x {1}: {2}", group.Count(), group.Key.Code.HasValue ? FormatCode(group.Key.Code.Value) : "no code", group.Key.Text).AppendLine();
            }
            if (groups.Count > 10) summary.AppendFormat("...and {0} more error codes.", groups.Count - 10).AppendLine();

            log.InfoFormat("Copied {0} failed device(s) with error codes for {1} ({2} distinct errors)", rows.Count, failed.Software, groups.Count);
            MessageBox.Show(summary.ToString(), "Copy Failed Devices", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Shows why there's nothing to do, if there isn't. True means stop.</summary>
        private static bool ShowProblem(FailedDevices failed)
        {
            if (failed.Problem == null) return false;
            MessageBox.Show(failed.Problem, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        /// <summary>Small codes are installer and Windows exit codes, so they read best in decimal too: "1603 (0x643)".</summary>
        internal static string FormatCode(uint code)
        {
            return code < 0x10000 ? string.Format("{0} (0x{0:X})", code) : string.Format("0x{0:X8}", code);
        }

        // ConfigMgr's own codes that Windows has no text for, among the ones admins see most.
        private static readonly Dictionary<uint, string> KnownErrors = new Dictionary<uint, string>
        {
            { 0x87D00213, "Timeout occurred" },
            { 0x87D00324, "The application was not detected after installation completed" },
            { 0x87D00325, "The application was still detected after uninstall completed" },
            { 0x87D00327, "Script is not signed" },
            { 0x87D00607, "Content not found" },
            { 0x87D01106, "Failed to verify the executable file is valid or to construct the associated command line" },
        };

        /// <summary>What an error code means: ConfigMgr's common codes, then Windows' own text for Windows and installer codes.</summary>
        internal static string ErrorText(uint code)
        {
            if (code == 0) return "No error code reported";
            if (KnownErrors.TryGetValue(code, out string known)) return known;

            // A Windows code as it is (1603), or wrapped in an HRESULT (0x80070643).
            uint win32 = (code & 0xFFFF0000) == 0x80070000 ? code & 0xFFFF : code;
            if (win32 < 0x10000)
            {
                string text = new System.ComponentModel.Win32Exception((int)win32).Message.TrimEnd('.', ' ');
                if (!text.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase)) return text;
            }
            return "No description for this code";
        }

        /// <summary>
        /// The devices in Error for a deployment. Shows nothing itself: when there's nothing to do (an unsupported
        /// deployment type, or no device in error) it says why in Problem, so a caller can show it or skip quietly.
        /// </summary>
        private static FailedDevices FindFailed(IResultObject deployment, bool withErrorCodes = false)
        {
            int featureType = deployment["FeatureType"].IntegerValue;
            int assignmentId = deployment["AssignmentID"].IntegerValue;
            FailedDevices failed = new FailedDevices
            {
                Software = deployment["SoftwareName"].StringValue,
                Collection = deployment["CollectionName"].StringValue,
            };

            string query, idColumn, nameColumn;
            switch (featureType)
            {
                case ApplicationDeployment:
                    query = string.Format("SELECT MachineID, MachineName FROM SMS_AppDeploymentAssetDetails WHERE AssignmentID = {0} AND AppStatusType = {1}", assignmentId, ErrorStatus);
                    idColumn = "MachineID";
                    nameColumn = "MachineName";
                    if (deployment["DeploymentIntent"].IntegerValue == AvailableIntent)
                    {
                        // Evaluation doesn't reinstall an Available app, so ask each device to install it, as
                        // Install Application does. ModelName is the ID the client knows the app by.
                        string appId = deployment["ModelName"].StringValue;
                        failed.RetryName = "RetryAvailableApplication";
                        failed.Retry = device => ApplicationInstall.InstallById(device["Name"].StringValue, appId);
                        failed.RetryDescription = "is asked to install it now, like Install Application (an Available deployment isn't retried by evaluation)";
                    }
                    else
                    {
                        failed.RetryName = nameof(ClientActions.RetryApplication);
                        failed.Retry = device => ClientActions.RetryApplication(device);
                        failed.RetryDescription = "re-runs its application deployment evaluation now, so the install is tried again";
                    }
                    break;
                case SoftwareUpdateDeployment:
                    query = string.Format("SELECT ResourceID, DeviceName, StatusErrorCode, StatusDescription FROM SMS_SUMDeploymentAssetDetails WHERE AssignmentID = {0} AND StatusType = {1}", assignmentId, ErrorStatus);
                    idColumn = "ResourceID";
                    nameColumn = "DeviceName";
                    failed.RetryName = nameof(ClientActions.RetryUpdates);
                    failed.Retry = device => ClientActions.RetryUpdates(device);
                    failed.RetryDescription = "scans for updates and re-evaluates its update deployments now, so required updates are tried again";
                    break;
                default:
                    failed.Problem = "This works for application and software update deployments.";
                    return failed;
            }

            using (IResultObject rows = deployment.ConnectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject row in rows)
                {
                    // An application can report one row per deployment type or user; count each device once.
                    int id = row[idColumn].IntegerValue;
                    if (failed.Ids.Contains(id)) continue;
                    failed.Ids.Add(id);
                    failed.Names.Add(row[nameColumn].StringValue);
                    if (featureType == SoftwareUpdateDeployment)
                    {
                        // UInt32 codes such as 0x87D00607 don't fit an int, so read them as 64-bit.
                        failed.ErrorCodes[id] = (uint)row["StatusErrorCode"].LongValue;
                        failed.Descriptions[id] = row["StatusDescription"].StringValue;
                    }
                }
            }

            // An application's error codes are kept in their own class, one row per device and deployment type.
            if (withErrorCodes && featureType == ApplicationDeployment && failed.Ids.Count > 0)
            {
                string errorQuery = string.Format("SELECT MachineID, ErrorCode FROM SMS_AppDeploymentErrorAssetDetails WHERE AssignmentID = {0}", assignmentId);
                using (IResultObject rows = deployment.ConnectionManager.QueryProcessor.ExecuteQuery(errorQuery))
                {
                    foreach (IResultObject row in rows)
                    {
                        int id = row["MachineID"].IntegerValue;
                        uint code = (uint)row["ErrorCode"].LongValue;
                        if (code != 0 && (!failed.ErrorCodes.TryGetValue(id, out uint existing) || existing == 0)) failed.ErrorCodes[id] = code;
                    }
                }
            }

            if (failed.Ids.Count == 0)
            {
                failed.Problem = string.Format("No devices are in error for {0} on {1}.\n\nDeployment status comes from state messages and summarization, so a device that failed in the last few minutes may not show yet.", failed.Software, failed.Collection);
            }
            return failed;
        }

        /// <summary>The SMS_R_System records for the given resource IDs (the progress dialog works on device records).</summary>
        private static List<IResultObject> DevicesById(ConnectionManagerBase connection, List<int> ids)
        {
            List<IResultObject> devices = new List<IResultObject>();
            for (int start = 0; start < ids.Count; start += IdsPerQuery)
            {
                string idList = string.Join(",", ids.Skip(start).Take(IdsPerQuery));
                // The result sets aren't disposed: the dialog reads these records after this returns.
                IResultObject results = connection.QueryProcessor.ExecuteQuery("SELECT * FROM SMS_R_System WHERE ResourceID IN (" + idList + ")");
                foreach (IResultObject device in results)
                {
                    devices.Add(device);
                }
            }
            return devices;
        }
    }
}
