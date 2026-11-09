using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        private const int BaselineDeployment = 6;

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
        /// <summary>Console entry point for baselines: re-evaluates every non-compliant device (not in error, just drifted) now.</summary>
        public static void ReevaluateNonCompliant(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Re-evaluate Non-compliant Devices", () => RetryCore(action, selectedResultObjects, nonCompliant: true));
        }

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

        private static void RetryCore(ActionDescription action, IResultObject deployment, bool nonCompliant = false)
        {
            FailedDevices failed = FindFailed(deployment, nonCompliant: nonCompliant);
            if (ShowProblem(failed)) return;

            string question = string.Format(
                "{5} {0} on the {1} device{2} {6} ({3})?\n\nEach device {4}. Deployment status catches up at the next summarization.",
                failed.Software, failed.Ids.Count, failed.Ids.Count == 1 ? "" : "s", failed.Collection, failed.RetryDescription,
                nonCompliant ? "Re-evaluate" : "Retry", nonCompliant ? "that aren't compliant" : "in error");
            string caption = nonCompliant ? "Re-evaluate Non-compliant Devices" : "Retry Failed Devices";
            if (MessageBox.Show(question, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<IResultObject> devices = DevicesById(deployment.ConnectionManager, failed.Ids);
            log.InfoFormat("Retrying {0} ({1}) on {2} device(s) in error", failed.Software, failed.Collection, devices.Count);

            using (DeviceProgressDialog dialog = new DeviceProgressDialog(action, devices, failed.RetryName, failed.Retry, caption + ": " + failed.Software))
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
            .Select(r => new { r.Name, r.Code, Text = r.Text ?? (r.Code.HasValue && r.Code.Value != 0 ? ErrorCodes.Describe(r.Code.Value) : "No error code reported") })
            .OrderBy(r => r.Code ?? uint.MaxValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            StringBuilder table = new StringBuilder("Device\tError code\tError").AppendLine();
            foreach (var row in rows)
            {
                table.Append(row.Name).Append('\t').Append(row.Code.HasValue ? ErrorCodes.Format(row.Code.Value) : "").Append('\t').Append(row.Text).AppendLine();
            }
            Clipboard.SetDataObject(table.ToString(), true, 10, 100);

            // Most common error first.
            var groups = rows.GroupBy(r => new { r.Code, r.Text }).OrderByDescending(g => g.Count()).ToList();
            StringBuilder summary = new StringBuilder();
            summary.AppendFormat("Copied {0} device{1} in error for {2}, with error codes (pastes into Excel).", rows.Count, rows.Count == 1 ? "" : "s", failed.Software).AppendLine().AppendLine();
            foreach (var group in groups.Take(10))
            {
                summary.AppendFormat("{0} x {1}: {2}", group.Count(), group.Key.Code.HasValue ? ErrorCodes.Format(group.Key.Code.Value) : "no code", group.Key.Text).AppendLine();
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

        /// <summary>
        /// The devices in Error for a deployment. Shows nothing itself: when there's nothing to do (an unsupported
        /// deployment type, or no device in error) it says why in Problem, so a caller can show it or skip quietly.
        /// </summary>
        private static FailedDevices FindFailed(IResultObject deployment, bool withErrorCodes = false, bool nonCompliant = false)
        {
            int featureType = deployment["FeatureType"].IntegerValue;
            int assignmentId = deployment["AssignmentID"].IntegerValue;
            FailedDevices failed = new FailedDevices
            {
                Software = deployment["SoftwareName"].StringValue,
                Collection = deployment["CollectionName"].StringValue,
            };

            string query, idColumn, nameColumn;
            if (nonCompliant && featureType != BaselineDeployment)
            {
                failed.Problem = "Re-evaluating non-compliant devices works for configuration baseline deployments.";
                return failed;
            }

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
                case BaselineDeployment:
                    // One row per device and CI in error (with the CI's error code), or per device and CI that's non-compliant.
                    query = nonCompliant
                        ? string.Format("SELECT AssetID, AssetName FROM SMS_DCMDeploymentNonCompliantAssetDetails WHERE AssignmentID = {0}", assignmentId)
                        : string.Format("SELECT AssetID, AssetName, ErrorCode, ErrorDescription FROM SMS_DCMDeploymentErrorAssetDetails WHERE AssignmentID = {0}", assignmentId);
                    idColumn = "AssetID";
                    nameColumn = "AssetName";
                    string baselineName = Regex.Replace(deployment["ModelName"].StringValue ?? string.Empty, @"/\d+$", "");
                    string displayName = failed.Software;
                    failed.RetryName = nameof(ClientActions.EvaluateBaseline);
                    failed.Retry = device => ClientActions.EvaluateBaseline(device, baselineName, displayName);
                    failed.RetryDescription = "re-evaluates the baseline now";
                    break;
                default:
                    failed.Problem = "This works for application, software update and configuration baseline deployments.";
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
                    if (featureType == BaselineDeployment && !nonCompliant)
                    {
                        failed.ErrorCodes[id] = (uint)row["ErrorCode"].LongValue;
                        failed.Descriptions[id] = row["ErrorDescription"].StringValue;
                    }
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
                failed.Problem = string.Format((nonCompliant ? "No devices are non-compliant" : "No devices are in error") + " for {0} on {1}.\n\nDeployment status comes from state messages and summarization, so a device that failed in the last few minutes may not show yet.", failed.Software, failed.Collection);
            }
            return failed;
        }

        /// <summary>
        /// Device Collections: every application, software update and baseline deployment to the collection that has
        /// devices in error, retried in one run. Each device gets every retry that applies to it, in one row.
        /// </summary>
        public static void RetryCollection(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selected, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Retry All Failed Deployments", () =>
            {
                string collectionId = selected["CollectionID"].StringValue;
                string collectionName = selected["Name"].StringValue;
                string wql = string.Format("SELECT * FROM SMS_DeploymentSummary WHERE CollectionID = '{0}' AND FeatureType IN ({1}, {2}, {3})",
                    collectionId, ApplicationDeployment, SoftwareUpdateDeployment, BaselineDeployment);

                // device ID -> the deployments to retry on it
                var work = new Dictionary<int, List<FailedDevices>>();
                var deployments = new List<FailedDevices>();
                using (IResultObject summaries = selected.ConnectionManager.QueryProcessor.ExecuteQuery(wql))
                {
                    foreach (IResultObject deployment in summaries)
                    {
                        FailedDevices failed = FindFailed(deployment);
                        if (failed.Problem != null || failed.Ids.Count == 0) continue;
                        deployments.Add(failed);
                        foreach (int id in failed.Ids)
                        {
                            if (!work.TryGetValue(id, out List<FailedDevices> list)) work[id] = list = new List<FailedDevices>();
                            list.Add(failed);
                        }
                    }
                }
                if (work.Count == 0)
                {
                    MessageBox.Show(string.Format("Nothing is in error for the deployments to {0}.\n\nDeployment status comes from summarization, so very recent failures may not show yet.", collectionName),
                        "Retry All Failed Deployments", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string question = string.Format("Retry {0} deployment{1} on the {2} device{3} in error in {4}?\n\n{5}",
                    deployments.Count, deployments.Count == 1 ? "" : "s", work.Count, work.Count == 1 ? "" : "s", collectionName,
                    string.Join("\n", deployments.Take(15).Select(d => string.Format("{0} ({1} device{2})", d.Software, d.Ids.Count, d.Ids.Count == 1 ? "" : "s"))));
                if (MessageBox.Show(question, "Retry All Failed Deployments", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                List<IResultObject> devices = DevicesById(selected.ConnectionManager, work.Keys.ToList());
                log.InfoFormat("Retrying {0} deployment(s) on {1} device(s) in {2}", deployments.Count, devices.Count, collectionName);
                using (DeviceProgressDialog dialog = new DeviceProgressDialog(action, devices, "RetryCollection", device =>
                {
                    List<string> results = new List<string>();
                    foreach (FailedDevices failed in work[device["ResourceID"].IntegerValue])
                    {
                        try
                        {
                            results.Add(failed.Software + ": " + (ClientActions.WaitForResult(failed.Retry(device)) ?? "started"));
                        }
                        catch (Exception ex)
                        {
                            // The first failure that means the device can't be reached ends the row; the dialog
                            // says whether it's offline or blocked.
                            Exception root = SafeAction.Unwrap(ex);
                            if (root is DeviceOfflineException || root is System.Runtime.InteropServices.COMException) throw;
                            results.Add(failed.Software + ": " + root.Message);
                        }
                    }
                    return string.Join("; ", results);
                }, "Retry All Failed Deployments: " + collectionName))
                {
                    dialog.Total = devices.Count;
                    dialog.ShowDialog();
                }
            });
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
