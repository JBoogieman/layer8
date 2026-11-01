using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
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
            public string RetryMethod;
            public string RetryDescription;
            public readonly List<int> Ids = new List<int>();
            public readonly List<string> Names = new List<string>();
        }

        private static void RetryCore(ActionDescription action, IResultObject deployment)
        {
            FailedDevices failed = FindFailed(deployment);
            if (failed == null || failed.Ids.Count == 0) return;

            string question = string.Format(
                "Retry {0} on the {1} device{2} in error ({3})?\n\nEach device {4} now, so required deployments try again. Deployment status catches up at the next summarization.",
                failed.Software, failed.Ids.Count, failed.Ids.Count == 1 ? "" : "s", failed.Collection, failed.RetryDescription);
            if (MessageBox.Show(question, "Retry Failed Devices", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<IResultObject> devices = DevicesById(deployment.ConnectionManager, failed.Ids);
            log.InfoFormat("Retrying {0} ({1}) on {2} device(s) in error", failed.Software, failed.Collection, devices.Count);

            using (DeviceProgressDialog dialog = new DeviceProgressDialog(action, devices, typeof(ClientActions).GetMethod(failed.RetryMethod), "Retry Failed Devices: " + failed.Software))
            {
                dialog.Total = devices.Count;
                dialog.ShowDialog();
            }
        }

        private static void CopyCore(IResultObject deployment)
        {
            FailedDevices failed = FindFailed(deployment);
            if (failed == null || failed.Ids.Count == 0) return;

            List<string> names = failed.Names.Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Clipboard.SetDataObject(string.Join(Environment.NewLine, names), true, 10, 100);
            log.InfoFormat("Copied {0} failed device name(s) for {1}", names.Count, failed.Software);
            MessageBox.Show(string.Format("Copied {0} device name{1} in error for {2}.", names.Count, names.Count == 1 ? "" : "s", failed.Software),
                            "Copy Failed Devices", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// The devices in Error for a deployment, or null when it isn't an application or software update deployment.
        /// Explains itself (unsupported type, nothing in error) so callers can simply stop.
        /// </summary>
        private static FailedDevices FindFailed(IResultObject deployment)
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
                    failed.RetryMethod = nameof(ClientActions.RetryApplication);
                    failed.RetryDescription = "re-runs its application deployment evaluation";
                    break;
                case SoftwareUpdateDeployment:
                    query = string.Format("SELECT ResourceID, DeviceName FROM SMS_SUMDeploymentAssetDetails WHERE AssignmentID = {0} AND StatusType = {1}", assignmentId, ErrorStatus);
                    idColumn = "ResourceID";
                    nameColumn = "DeviceName";
                    failed.RetryMethod = nameof(ClientActions.RetryUpdates);
                    failed.RetryDescription = "scans for updates and re-evaluates its update deployments";
                    break;
                default:
                    MessageBox.Show("This works for application and software update deployments.", "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
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
                }
            }

            if (failed.Ids.Count == 0)
            {
                MessageBox.Show(
                    string.Format("No devices are in error for {0} on {1}.\n\nDeployment status comes from state messages and summarization, so a device that failed in the last few minutes may not show yet.", failed.Software, failed.Collection),
                    "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
