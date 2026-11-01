using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Install Application: lists the applications deployed to a device (the client's CCM_Application view, the
    /// same list Software Center shows) and installs the selected one now, like clicking Install in Software
    /// Center. Only device-targeted deployments can be installed this way; user-targeted ones belong to the
    /// user's own session.
    /// </summary>
    public static class ApplicationInstall
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ApplicationInstall));

        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Install Application", () => ShowCore(selectedObject));
        }

        private static void ShowCore(IResultObject selectedObject)
        {
            string name = selectedObject["Name"].StringValue;
            if (!RemoteTools.IsComputerName(name))
            {
                MessageBox.Show(name + " isn't a computer name.", "Install Application", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (ApplicationsDialog dialog = new ApplicationsDialog(name))
            {
                dialog.ShowDialog();
            }
        }

        internal sealed class DeployedApp
        {
            public string Id, Revision, Name, Version, State;
            public bool IsMachineTarget, Installed, CanInstall;
            public DateTime? Deadline;
        }

        /// <summary>The applications deployed to the device, from root\ccm\ClientSDK.</summary>
        internal static List<DeployedApp> LoadApps(string host)
        {
            ManagementScope clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
            List<DeployedApp> apps = new List<DeployedApp>();
            foreach (ManagementObject app in Utility.SearchWMIToList(clientSdk, "SELECT * FROM CCM_Application"))
            {
                using (app)
                {
                    string installState = app["InstallState"] as string ?? string.Empty;
                    string[] allowed = app["AllowedActions"] as string[] ?? new string[0];
                    bool machine = app["IsMachineTarget"] is bool target && target;
                    bool installed = string.Equals(installState, "Installed", StringComparison.OrdinalIgnoreCase);
                    apps.Add(new DeployedApp
                    {
                        Id = app["Id"] as string,
                        Revision = app["Revision"] as string,
                        Name = app["Name"] as string ?? app["FullName"] as string ?? "(unnamed)",
                        Version = app["SoftwareVersion"] as string ?? string.Empty,
                        IsMachineTarget = machine,
                        Installed = installed,
                        Deadline = ToDate(app["Deadline"]),
                        State = StateText(installState, app["EvaluationState"], app["ErrorCode"]),
                        CanInstall = machine && !installed && allowed.Contains("Install", StringComparer.OrdinalIgnoreCase),
                    });
                }
            }
            return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Installs now: immediate, high priority, no restart even if one is needed (the deployment's own settings decide restarts later).</summary>
        internal static void Install(string host, DeployedApp app)
        {
            ManagementScope clientSdk = Utility.GetWMIScope(host, @"ccm\ClientSDK");
            using (ManagementClass applications = new ManagementClass(clientSdk, new ManagementPath("CCM_Application"), null))
            using (ManagementBaseObject inParams = applications.GetMethodParameters("Install"))
            {
                inParams["Id"] = app.Id;
                inParams["Revision"] = app.Revision;
                inParams["IsMachineTarget"] = app.IsMachineTarget;
                inParams["EnforcePreference"] = 0u;   // immediate
                inParams["Priority"] = "High";
                inParams["IsRebootIfNeeded"] = false;
                using (ManagementBaseObject outParams = applications.InvokeMethod("Install", inParams, null))
                {
                    long code = Convert.ToInt64(outParams?["ReturnValue"] ?? 0);
                    if (code != 0) throw new InvalidOperationException(string.Format("CCM_Application.Install returned error {0} (0x{0:X}).", code));
                }
            }
            log.InfoFormat("Install of '{0}' {1} requested on {2}", app.Name, app.Version, host);
        }

        // The client's EvaluationState numbers, as Software Center describes them.
        private static readonly string[] EvaluationStates =
        {
            "No state", "Available", "Submitted", "Detecting", "Waiting to download", "Downloading", "Waiting to install", "Installing",
            "Restart pending", "Restart pending", "Waiting for restart", "Verifying", "Installed", "Failed", "Waiting for maintenance window",
            "Waiting for user logon", "Waiting for user logoff", "Waiting for user logon", "Waiting for user to reconnect", "Pending user logoff",
            "Pending update", "Waiting to retry", "Waiting for presentation mode to end", "Waiting for orchestration",
        };

        private static string StateText(string installState, object evaluationState, object errorCode)
        {
            long evaluation = evaluationState == null ? -1 : Convert.ToInt64(evaluationState);
            long error = errorCode == null ? 0 : Convert.ToInt64(errorCode);
            string state = evaluation > 1 && evaluation < EvaluationStates.Length ? EvaluationStates[evaluation] : (string.IsNullOrEmpty(installState) ? "Unknown" : installState);
            if (string.Equals(installState, "Installed", StringComparison.OrdinalIgnoreCase) && evaluation <= 1) state = "Installed";
            if (string.Equals(installState, "NotInstalled", StringComparison.OrdinalIgnoreCase) && evaluation <= 1) state = "Not installed";
            if (error != 0) state += string.Format(" (error 0x{0:X8})", error);
            return state;
        }

        private static DateTime? ToDate(object dmtf)
        {
            string text = dmtf as string;
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                DateTime value = ManagementDateTimeConverter.ToDateTime(text);
                return value.Year > 2000 ? value : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The window: deployed apps, Install, Refresh. Loads on a background thread so the console stays responsive.</summary>
        private sealed class ApplicationsDialog : Form
        {
            private readonly string host;
            private readonly ListView list;
            private readonly Label status;
            private readonly Button install;
            private readonly Button refresh;

            public ApplicationsDialog(string host)
            {
                this.host = host;

                SuspendLayout();
                AutoScaleDimensions = new SizeF(6F, 13F);
                AutoScaleMode = AutoScaleMode.Font;
                Text = "Install Application: " + host;
                ClientSize = new Size(720, 340);
                MinimumSize = new Size(480, 260);
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false;

                Label intro = new Label
                {
                    Location = new Point(12, 10),
                    Size = new Size(696, 30),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Text = "Applications deployed to " + host + ". Install starts the selected one now, like Install in Software Center. Only device-targeted deployments can be installed from here.",
                };
                list = new ListView
                {
                    Location = new Point(12, 44),
                    Size = new Size(696, 246),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    FullRowSelect = true,
                    MultiSelect = false,
                    HideSelection = false,
                };
                list.Columns.Add("Application", 260);
                list.Columns.Add("Version", 80);
                list.Columns.Add("Deployment", 150);
                list.Columns.Add("Status", 190);
                list.SelectedIndexChanged += (s, e) => install.Enabled = Selected()?.CanInstall == true;
                list.DoubleClick += (s, e) => InstallSelected();

                status = new Label
                {
                    Location = new Point(12, 302),
                    Size = new Size(420, 26),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    Text = "Loading...",
                };
                install = new Button { Text = "Install", Location = new Point(471, 302), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
                install.Click += (s, e) => InstallSelected();
                refresh = new Button { Text = "Refresh", Location = new Point(552, 302), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                refresh.Click += (s, e) => Reload();
                Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(633, 302), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

                Controls.AddRange(new Control[] { intro, list, status, install, refresh, close });
                CancelButton = close;
                ResumeLayout(false);
                PerformLayout();

                Shown += (s, e) => Reload();
            }

            private DeployedApp Selected()
            {
                return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as DeployedApp : null;
            }

            private void Reload()
            {
                refresh.Enabled = false;
                install.Enabled = false;
                status.Text = "Loading the applications deployed to " + host + "...";
                Task.Run(() => LoadApps(host)).ContinueWith(task =>
                {
                    if (IsDisposed) return;
                    refresh.Enabled = true;
                    if (task.Exception != null)
                    {
                        Exception error = SafeAction.Unwrap(task.Exception);
                        log.Warn("Loading applications on " + host + " failed", error);
                        status.Text = error is ManagementException wmi && ClientActions.IsNoClient(wmi) ? "No ConfigMgr client found on " + host + "." : "Couldn't read the applications: " + error.Message;
                        return;
                    }
                    Fill(task.Result);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            private void Fill(List<DeployedApp> apps)
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (DeployedApp app in apps)
                {
                    string deployment = (app.Deadline.HasValue ? "Required, deadline " + app.Deadline.Value.ToString("g") : "Available") + (app.IsMachineTarget ? "" : " (user)");
                    ListViewItem item = new ListViewItem(app.Name) { Tag = app };
                    item.SubItems.Add(app.Version);
                    item.SubItems.Add(deployment);
                    item.SubItems.Add(app.State);
                    if (!app.IsMachineTarget) item.ForeColor = SystemColors.GrayText;
                    list.Items.Add(item);
                }
                list.EndUpdate();
                status.Text = apps.Count == 0 ? "No applications are deployed to " + host + "." : string.Format("{0} application{1} deployed to {2}.", apps.Count, apps.Count == 1 ? "" : "s", host);
            }

            private void InstallSelected()
            {
                DeployedApp app = Selected();
                if (app == null || !app.CanInstall) return;
                if (MessageBox.Show(string.Format("Install {0} {1} on {2} now?", app.Name, app.Version, host), "Install Application", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                install.Enabled = false;
                status.Text = "Asking " + host + " to install " + app.Name + "...";
                Task.Run(() => Install(host, app)).ContinueWith(task =>
                {
                    if (IsDisposed) return;
                    if (task.Exception != null)
                    {
                        Exception error = SafeAction.Unwrap(task.Exception);
                        log.Warn("Install of " + app.Name + " on " + host + " failed", error);
                        status.Text = "Install failed: " + error.Message;
                        install.Enabled = true;
                        return;
                    }
                    status.Text = "Install started for " + app.Name + ". Refresh to follow its status.";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
    }
}
