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
                    // Some CCM_Application properties are "lazy": a list query leaves them empty and only reading
                    // the instance itself fills them in. AllowedActions is the one Install depends on.
                    if (app["AllowedActions"] == null)
                    {
                        try
                        {
                            app.Get();
                        }
                        catch (ManagementException ex)
                        {
                            log.Warn("Couldn't read all of " + (app["Name"] as string) + " on " + host, ex);
                        }
                    }

                    string installState = app["InstallState"] as string ?? string.Empty;
                    string[] allowed = app["AllowedActions"] as string[];
                    bool machine = app["IsMachineTarget"] is bool target && target;
                    bool installed = string.Equals(installState, "Installed", StringComparison.OrdinalIgnoreCase);
                    log.InfoFormat("Application on {0}: '{1}' InstallState={2} EvaluationState={3} ApplicabilityState={4} IsMachineTarget={5} AllowedActions={6} ErrorCode={7}",
                        host, app["Name"], installState, app["EvaluationState"], app["ApplicabilityState"], machine,
                        allowed == null ? "(not reported)" : string.Join("/", allowed), app["ErrorCode"]);
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
                        // When the client doesn't report its allowed actions, let Install try: the client answers either way.
                        CanInstall = machine && !installed && (allowed == null || allowed.Contains("Install", StringComparer.OrdinalIgnoreCase)),
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

        // CCM_Application.EvaluationState, numbered as in Microsoft's client SDK reference. Null means there's nothing
        // to add to the install state: 0 no state, 1 already in the deployment's desired state, 3 available to install.
        private static readonly string[] EvaluationStates =
        {
            null, null, "not required", null, "last install failed", "waiting for content", "waiting for content",
            "waiting for dependencies to download", "waiting for a maintenance window", "waiting for a pending restart",
            "waiting for other installs to finish", "installing dependencies", "installing", "restart pending", "restart pending",
            "update available", "failed to evaluate", "waiting for a user session", "waiting for users to sign out",
            "waiting for a user to sign in", "waiting to retry", "waiting for presentation mode to end", "downloading content",
            "downloading dependencies", "download failed", "download failed", "downloaded", "checking the result",
            "waiting for a network connection",
        };

        /// <summary>The install state first (it's what the client decides on), then what the client is doing with the app, if anything.</summary>
        private static string StateText(string installState, object evaluationState, object errorCode)
        {
            long evaluation = evaluationState == null ? -1 : Convert.ToInt64(evaluationState);
            long error = errorCode == null ? 0 : Convert.ToInt64(errorCode);
            string state;
            switch ((installState ?? string.Empty).ToLowerInvariant())
            {
                case "installed": state = "Installed"; break;
                case "notinstalled": state = "Not installed"; break;
                case "error": state = "Error"; break;
                case "": state = "Unknown"; break;
                default: state = installState; break;
            }
            if (evaluation >= 0 && evaluation < EvaluationStates.Length && EvaluationStates[evaluation] != null)
            {
                state += " (" + EvaluationStates[evaluation] + ")";
            }
            else if (evaluation >= EvaluationStates.Length)
            {
                state += " (state " + evaluation + ")";
            }
            if (error != 0) state += string.Format(" (error 0x{0:X8})", error);
            return state;
        }

        /// <summary>Why Install is off for an app, in words the status line can show.</summary>
        internal static string WhyNotInstallable(DeployedApp app)
        {
            if (!app.IsMachineTarget) return app.Name + " is deployed to users: it installs from the user's own Software Center.";
            if (app.Installed) return app.Name + " is already installed.";
            return "The client isn't offering Install for " + app.Name + " right now (" + app.State + "). AppDiscovery.log and AppIntentEval.log on the device say why.";
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
                list.SelectedIndexChanged += (s, e) =>
                {
                    DeployedApp app = Selected();
                    install.Enabled = app?.CanInstall == true;
                    if (app != null && !app.CanInstall) status.Text = WhyNotInstallable(app);
                };
                list.DoubleClick += (s, e) => InstallSelected();
                list.Resize += (s, e) => FitColumns();

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
                FitColumns();
                list.EndUpdate();
                status.Text = apps.Count == 0 ? "No applications are deployed to " + host + "." : string.Format("{0} application{1} deployed to {2}.", apps.Count, apps.Count == 1 ? "" : "s", host);
            }

            /// <summary>Deployment and Status fit their text, and Status takes any width left over, so the error code never hides.</summary>
            private void FitColumns()
            {
                if (list.Columns.Count < 4) return;
                if (list.Items.Count > 0)
                {
                    list.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.ColumnContent);
                    list.AutoResizeColumn(3, ColumnHeaderAutoResizeStyle.ColumnContent);
                    list.Columns[2].Width = Math.Max(list.Columns[2].Width, LogicalToDeviceUnits(90));
                }
                int spare = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width;
                if (spare > list.Columns[3].Width) list.Columns[3].Width = spare;
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
                    install.Enabled = false;
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
    }
}
