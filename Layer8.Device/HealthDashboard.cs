using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Health Dashboard: one live pass over a collection, or the selected devices, with what's wrong where. Tiles
    /// across the top count each issue (offline, restart pending, low disk, BitLocker off, Defender, missing
    /// updates, client, long uptime); clicking one shows just those devices. Below is a grid to sort, filter, copy
    /// and export, and double-clicking a device opens System Information. Read only.
    /// </summary>
    public static class HealthDashboard
    {
        /// <summary>Console entry point, on a collection or on selected devices.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Health Dashboard", () => ShowCore(action, selectedObjects));
        }

        private static void ShowCore(ActionDescription action, IResultObject selected)
        {
            string title = action?.DisplayName ?? "Health Dashboard";
            bool isCollection = selected.ObjectClass == "SMS_Collection";
            // Name and ResourceID: the name to check, the ID to find its record again for a client action.
            List<KeyValuePair<string, int>> found = new List<KeyValuePair<string, int>>();
            string scope;
            if (isCollection)
            {
                scope = selected["Name"].StringValue;
                string query = string.Format("SELECT * FROM SMS_FullCollectionMembership WHERE CollectionID='{0}'", selected["CollectionID"].StringValue);
                try
                {
                    foreach (IResultObject member in selected.ConnectionManager.QueryProcessor.ExecuteQuery(query))
                    {
                        found.Add(new KeyValuePair<string, int>(member["Name"].StringValue, member["ResourceID"].IntegerValue));
                    }
                }
                catch (SmsQueryException ex)
                {
                    SccmExceptionDialog.ShowDialog(SnapIn.Console, ex);
                    return;
                }
            }
            else
            {
                foreach (IResultObject device in selected)
                {
                    found.Add(new KeyValuePair<string, int>(device["Name"].StringValue, device["ResourceID"].IntegerValue));
                }
                scope = found.Count == 1 ? found[0].Key : found.Count + " selected devices";
            }

            // Each device once, even when two records share its name (a reimaged device, say).
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, int>> devices = found.Where(device => !string.IsNullOrWhiteSpace(device.Key) && seen.Add(device.Key)).ToList();
            if (devices.Count == 0)
            {
                MessageBox.Show(scope + " has no devices to check.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // A big collection asks first (Layer8 Settings), like every action that runs across devices.
            int confirmAbove = Layer8Settings.ConfirmAbove;
            string target = isCollection
                ? string.Format("the {0} device{1} in {2}", devices.Count, devices.Count == 1 ? "" : "s", scope)
                : devices.Count + " devices";
            if (confirmAbove > 0 && devices.Count > confirmAbove &&
                MessageBox.Show(string.Format("Run {0} on {1}? It only reads, {2} devices at a time.", title, target, Layer8Settings.DevicesAtOnce),
                                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            // A collection's runs are kept to compare the next one with; a selection of devices has nothing to key them by.
            string collectionId = isCollection ? selected["CollectionID"].StringValue : null;
            using (HealthDashboardDialog dialog = new HealthDashboardDialog(action, title, scope, devices, selected.ConnectionManager, collectionId))
            {
                dialog.ShowDialog();
            }
        }
    }

    internal sealed class HealthDashboardDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(HealthDashboardDialog));

        private enum RowState { Queued, Checking, Checked, Offline, Failed, Skipped, Canceled }

        private enum Tone { Neutral, Good, Muted, Warn, Bad, Info }

        /// <summary>One device: its row in the grid (kept while a filter hides it), and what the last check found.</summary>
        private sealed class Row
        {
            public Row(string host, int resourceId, int columns)
            {
                Host = host;
                ResourceId = resourceId;
                Item = new ListViewItem(host) { Tag = this };
                for (int i = 1; i < columns; i++) Item.SubItems.Add(string.Empty);
            }

            public readonly string Host;
            public readonly int ResourceId;
            public readonly ListViewItem Item;
            /// <summary>Changed on the window's thread only, like everything the grid shows.</summary>
            public RowState State;
            public HealthReport Report;
            /// <summary>What the site last heard from it, read alongside the device's own check; null until then.</summary>
            public SiteContact Site;
            /// <summary>Whether this run's read of the site has come back for it (it can come after the device answers).</summary>
            public bool SiteRead;
            /// <summary>What the site's records hold when it didn't answer (C:, the Windows build), read after the run; null until then.</summary>
            public SiteInventory Inventory;
            /// <summary>What its check said, before an offline row's Status becomes "Offline, last seen ..."; the tooltip keeps it.</summary>
            public string StatusText;
            /// <summary>What changed since the last run, for the Since last run column and the Changed tile; null when nothing did.</summary>
            public string Change;

            public bool Has(HealthIssue issue) => issue == HealthIssue.NotReporting
                ? Site != null && Site.NotReporting
                : Report != null && Report.Issues.Contains(issue);

            /// <summary>
            /// What's wrong with it, in the tiles' order: that it didn't answer or couldn't be checked, what its check
            /// found, and what the site says. The Issues column, the tiles and the row's tooltip all read this.
            /// </summary>
            public List<string> IssueNames()
            {
                List<string> names = new List<string>();
                if (State == RowState.Offline) names.Add("Offline");
                else if (State == RowState.Failed) names.Add(HealthCheck.Name(HealthIssue.CouldNotCheck));
                foreach (HealthIssue issue in Enum.GetValues(typeof(HealthIssue)))
                {
                    if (Has(issue)) names.Add(issue == HealthIssue.CouldNotCheck ? Report.UnreadText : HealthCheck.Name(issue));
                }
                return names;
            }

            /// <summary>
            /// Its issues as a kept run holds them, by tile name: Couldn't check without its reasons, which follow the
            /// wording of an error rather than what's wrong with the device.
            /// </summary>
            public List<string> IssueKeys()
            {
                List<string> keys = new List<string>();
                if (State == RowState.Offline) keys.Add("Offline");
                else if (State == RowState.Failed) keys.Add(HealthCheck.Name(HealthIssue.CouldNotCheck));
                foreach (HealthIssue issue in Enum.GetValues(typeof(HealthIssue)))
                {
                    string name = HealthCheck.Name(issue);
                    if (Has(issue) && !keys.Contains(name)) keys.Add(name);
                }
                return keys;
            }

            /// <summary>Checked, offline or couldn't be checked: a device a run says something about (not queued, skipped or canceled).</summary>
            public bool Answered => State == RowState.Checked || State == RowState.Offline || State == RowState.Failed;
        }

        /// <summary>A client action the dashboard runs on selected devices, named as the console's Client Actions menu names it.</summary>
        private sealed class DeviceAction
        {
            public DeviceAction(string name, string tool, string schedule, bool full)
            {
                Name = name;
                Tool = tool;
                Schedule = schedule;
                Full = full;
            }

            public readonly string Name;
            /// <summary>A client tool (a ClientActions method), or null for a client action cycle.</summary>
            public readonly string Tool;
            /// <summary>A client action cycle's schedule ID, and whether it's a full inventory.</summary>
            public readonly string Schedule;
            public readonly bool Full;
        }

        private static DeviceAction ClientTool(string name, string tool) => new DeviceAction(name, tool, null, false);

        private static DeviceAction Cycle(string name, string schedule, bool full = false) => new DeviceAction(name, null, schedule, full);

        /// <summary>What a tile's Fix button offers: the button's words, and the client actions (two or more make a menu).</summary>
        private sealed class TileFix
        {
            public TileFix(string label, params DeviceAction[] actions)
            {
                Label = label;
                Actions = actions;
            }

            public readonly string Label;
            public readonly DeviceAction[] Actions;
        }

        /// <summary>One of the right-click menu's client actions, by name, so a tile's fix is the very same action.</summary>
        private static DeviceAction Named(string name) => DeviceActionGroups.SelectMany(group => group).Single(each => each.Name == name);

        // Grouped by what the tiles find: policy and inventory, updates, restarts, the client itself, and the rest.
        // The read-only tools the grid already answers (Quick Status, Check Pending Restart, BitLocker and Defender
        // Status) are left out.
        private static readonly DeviceAction[][] DeviceActionGroups =
        {
            new[]
            {
                Cycle("Machine Policy Retrieval and Evaluation Cycle", "{00000000-0000-0000-0000-000000000021}"),
                ClientTool("Full Machine Policy Refresh", nameof(ClientActions.FullMachinePolicyRefresh)),
                Cycle("Application Deployment Evaluation Cycle", "{00000000-0000-0000-0000-000000000121}"),
                ClientTool("Group Policy Update (Computer)", nameof(ClientActions.GroupPolicyUpdate)),
                Cycle("Hardware Inventory Cycle", "{00000000-0000-0000-0000-000000000001}"),
            },
            new[]
            {
                Cycle("Software Updates Scan Cycle", "{00000000-0000-0000-0000-000000000113}"),
                Cycle("Software Updates Deployment Evaluation Cycle", "{00000000-0000-0000-0000-000000000108}"),
                ClientTool("List Missing Updates", nameof(ClientActions.MissingUpdates)),
                ClientTool("Install Missing Updates", nameof(ClientActions.InstallMissingUpdates)),
            },
            new[]
            {
                ClientTool("Send Message...", nameof(ClientActions.SendMessage)),
                ClientTool("Restart Computer", nameof(ClientActions.RestartComputer)),
                ClientTool("Cancel Restart", nameof(ClientActions.CancelRestart)),
            },
            new[]
            {
                ClientTool("Run Client Health Check", nameof(ClientActions.ClientHealthCheck)),
                ClientTool("Repair Client", nameof(ClientActions.RepairClient)),
                ClientTool("Reset Policy", nameof(ClientActions.ResetPolicy)),
                ClientTool("Turn Off Provisioning Mode", nameof(ClientActions.TurnOffProvisioningMode)),
                ClientTool("Clear Client Cache", nameof(ClientActions.ClearClientCache)),
                ClientTool("Client Info", nameof(ClientActions.ClientInfo)),
                ClientTool("Collect Client Logs", nameof(ClientActions.CollectLogs)),
            },
            new[]
            {
                ClientTool("Defender Quick Scan", nameof(ClientActions.DefenderQuickScan)),
                ClientTool("Find Software...", nameof(ClientActions.FindSoftware)),
            },
        };

        private static readonly string[] Columns = { "Device", "Status", "Issues", "Since last run", "Signed in", "Up", "C: free", "Restart", "BitLocker", "Defender", "Blue screen", "Windows", "Updates missing", "Update scan", "Client", "Policy request" };
        private static readonly int[] StartWidths = { 120, 90, 220, 150, 130, 60, 110, 110, 90, 90, 110, 120, 130, 90, 110, 100 };
        private const int StatusColumn = 1;
        private const int IssuesColumn = 2;
        private const int ChangeColumn = 3;
        private const int PolicyColumn = 15;

        private readonly ActionDescription action;
        private readonly ConnectionManagerBase connection;
        private readonly string title;
        private readonly string scope;
        private readonly List<Row> rows;
        // Read once, so the tiles' rules and the checks agree for the whole window.
        private readonly int lowDiskGB = Layer8Settings.LowDiskGB;
        private readonly int longUptimeDays = Layer8Settings.LongUptimeDays;
        private readonly int notReportingDays = Layer8Settings.NotReportingDays;
        private readonly int blueScreenDays = Layer8Settings.BlueScreenDays;
        private readonly int notScanningDays = Layer8Settings.NotScanningDays;
        private readonly string windowsBuildText = Layer8Settings.WindowsBuild;
        private readonly Version windowsBuild;
        // Why the site's side couldn't be read this pass, for the status line; null when it could.
        private string siteNote;
        // Whether this pass's read of the site has come back, and whether it was read.
        private bool siteDone, siteOk;
        // Which run this is: a read of the site that comes back after the next run started is dropped.
        private int runNumber;

        // The collection's last full run, kept to compare with: where it's kept (null for selected devices), the run
        // the grid compares with now, and one this window has just kept, which the next pass compares with.
        private readonly string server;
        private readonly string historyPath;
        private HealthRun previous;
        private HealthRun kept;
        private bool fullRun, keepWhenSiteDone;
        private readonly Tile changedTile;

        private readonly ToolTip tips = new ToolTip();
        private readonly FlowLayoutPanel tilePanel;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly Tile allTile;
        private Tile activeTile;
        private readonly TextBox filter;
        private readonly Label shown;
        private readonly ListView list;
        private readonly Label status;
        private readonly ProgressBar progress;
        private readonly Button runButton;
        // Runs the active tile's fix on the devices the grid shows; hidden on tiles without one.
        private readonly Button fixButton;
        private readonly ContextMenuStrip fixMenu = new ContextMenuStrip();
        private readonly Tile clientTile;
        // Devices answer up to 32 at a time; the tiles and the status line catch up a few times a second.
        private readonly System.Windows.Forms.Timer summaryTimer = new System.Windows.Forms.Timer { Interval = 300 };

        private CancellationTokenSource cancel;
        private bool running, stopping, dirty, needsSort, fitted;
        private int runTotal, pending;
        private readonly Stopwatch stopwatch = new Stopwatch();
        private DateTime finishedAt;
        private int sortColumn;
        private bool descending;

        public HealthDashboardDialog(ActionDescription action, string title, string scope, IEnumerable<KeyValuePair<string, int>> devices, ConnectionManagerBase connection, string collectionId)
        {
            this.action = action;
            this.connection = connection;
            this.title = title;
            this.scope = scope;
            Layer8Settings.TryParseWindowsBuild(windowsBuildText, out windowsBuild);
            rows = devices.Select(device => new Row(device.Key, device.Value, Columns.Length)).ToList();
            if (collectionId != null)
            {
                try
                {
                    server = connection.NamedValueDictionary["ServerName"]?.ToString();
                }
                catch (KeyNotFoundException)
                {
                }
                historyPath = HealthRun.PathFor(collectionId);
                previous = HealthRun.Load(historyPath, server);
            }

            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = title + ": " + scope;
            ClientSize = new Size(1160, 640);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;

            // The tiles, left to right in the order the Issues column lists them. They wrap on a narrow window.
            tilePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(9, 10, 9, 2),
            };
            allTile = AddTile("Devices", "Every device in " + scope + ". Click to show them all", Tone.Neutral, row => true);
            allTile.Selected = true;
            AddTile("Healthy", "Checked, with none of the issues on the other tiles", Tone.Good,
                row => row.State == RowState.Checked && row.IssueNames().Count == 0);
            AddTile("Offline", "Didn't answer: ports 135 and 445 closed, or WMI's RPC server unreachable", Tone.Muted,
                row => row.State == RowState.Offline);
            changedTile = AddTile("Changed", string.Empty, Tone.Info, row => row.Change != null);
            AddIssueTile(HealthIssue.RestartPending, "ConfigMgr, Windows servicing or Windows Update is waiting on a restart");
            AddIssueTile(HealthIssue.LowDisk, string.Format("Less than {0} GB free on C:", lowDiskGB));
            AddIssueTile(HealthIssue.BitLockerOff, "C: isn't protected by BitLocker: off, or protection suspended");
            AddIssueTile(HealthIssue.Defender, string.Format(
                "Microsoft Defender antivirus or real-time protection is off, or its signatures are more than {0} days old. Not counted when Defender is passive behind another antivirus",
                HealthCheck.DefenderSignatureDays));
            AddIssueTile(HealthIssue.BlueScreen, string.Format(
                "Windows wrote a crash dump (a blue screen) in the last {0} days. Read from C:\\Windows\\Minidump: a blue screen whose dump was turned off or cleaned up doesn't show", blueScreenDays));
            AddIssueTile(HealthIssue.MissingUpdates, "Deployed software updates the client reports as missing");
            AddIssueTile(HealthIssue.NotScanning, string.Format(
                "The client's last software update scan finished {0} days ago or more, or it has never finished one (its scan history, as List Missing Updates reads it). Not counted where client settings turn software updates off", notScanningDays))
                .Fix = new TileFix("Run Update Scan", Named("Software Updates Scan Cycle"));
            AddIssueTile(HealthIssue.WindowsBehind, string.Format(
                "A workstation on a Windows build older than {0} (set in Layer8 Settings). Servers aren't counted: their builds follow their own line", windowsBuildText));
            clientTile = AddIssueTile(HealthIssue.Client, "No ConfigMgr client, its service (CcmExec) isn't running, or it's in provisioning mode");
            // Health Check first; Repair for what's still here once it's had time. They can't be chained: CcmEval runs on its own for minutes.
            clientTile.Fix = new TileFix("Fix Client", Named("Run Client Health Check"), Named("Repair Client"));
            AddIssueTile(HealthIssue.NotReporting, string.Format(
                "The site hasn't had a policy request from its client in {0} days or more. Read from the site, so offline devices count too", notReportingDays))
                .Fix = new TileFix("Request Policy", Named("Machine Policy Retrieval and Evaluation Cycle"));
            AddIssueTile(HealthIssue.LongUptime, string.Format("Up {0} days or more since the last restart", longUptimeDays));
            AddTile(HealthCheck.Name(HealthIssue.CouldNotCheck),
                "Answered, but couldn't be checked (access denied, a WMI error), or one check couldn't be read: the Issues column says which", Tone.Bad,
                row => row.State == RowState.Failed || row.Has(HealthIssue.CouldNotCheck));
            ShowPrevious();

            Panel filterBar = new Panel { Dock = DockStyle.Top, Height = 32 };
            Label filterLabel = new Label { Text = "Filter:", Location = new Point(12, 9), AutoSize = true };
            filter = new TextBox { Location = new Point(52, 6), Size = new Size(260, 20) };
            shown = new Label { Location = new Point(324, 9), AutoSize = true };
            filterBar.Controls.AddRange(new Control[] { filterLabel, filter, shown });

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                ShowItemToolTips = true,
            };
            // Rows change while devices answer; double buffering keeps the list from flickering as they do.
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(list, true, null);
            for (int i = 0; i < Columns.Length; i++) list.Columns.Add(Columns[i] + (i == 0 ? " ▲" : string.Empty), LogicalToDeviceUnits(StartWidths[i]));
            list.ListViewItemSorter = new ListViewSorter(0, false);
            Panel listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            listPanel.Controls.Add(list);

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem systemInfo = new ToolStripMenuItem("System Information...", null, (s, e) => OpenSystemInfo());
            ToolStripMenuItem checkAgain = new ToolStripMenuItem("Check Again", null, (s, e) => CheckAgain());
            ToolStripMenuItem copySelected = new ToolStripMenuItem("Copy", null, (s, e) => CopyRows(true));
            // Find something, fix it here: the console's client actions, on the selected devices.
            ToolStripMenuItem clientActions = new ToolStripMenuItem("Client Actions");
            foreach (DeviceAction[] group in DeviceActionGroups)
            {
                if (clientActions.DropDownItems.Count > 0) clientActions.DropDownItems.Add(new ToolStripSeparator());
                foreach (DeviceAction deviceAction in group)
                {
                    DeviceAction chosen = deviceAction;
                    clientActions.DropDownItems.Add(new ToolStripMenuItem(chosen.Name, null, (s, e) => SafeAction.Run(chosen.Name, () => RunDeviceAction(chosen, SelectedDevices()))));
                }
            }
            menu.Items.AddRange(new ToolStripItem[] { systemInfo, checkAgain, clientActions, new ToolStripSeparator(), copySelected });
            menu.Opening += (s, e) =>
            {
                int selectedCount = list.SelectedItems.Count;
                if (selectedCount == 0)
                {
                    e.Cancel = true;
                    return;
                }
                systemInfo.Enabled = selectedCount == 1;
                checkAgain.Enabled = !running;
                checkAgain.Text = selectedCount == 1 ? "Check Again" : string.Format("Check These {0} Again", selectedCount);
                clientActions.Enabled = SelectedDevices().Count > 0;
            };
            list.ContextMenuStrip = menu;

            // The bottom bar. Buttons stay off the top right: with the window maximized, Windows 11 takes clicks
            // there as clicks on the title bar's buttons.
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Size = new Size(1160, 44) };
            Button copy = new Button { Text = "Copy", Location = new Point(12, 10), Size = new Size(75, 23) };
            Button export = new Button { Text = "Export...", Location = new Point(93, 10), Size = new Size(75, 23) };
            fixButton = new Button
            {
                Location = new Point(174, 10),
                MinimumSize = new Size(75, 23),
                Size = new Size(75, 23),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Visible = false,
            };
            status = new Label
            {
                Location = new Point(180, 10),
                Size = new Size(640, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            progress = new ProgressBar { Location = new Point(832, 13), Size = new Size(150, 17), Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false };
            runButton = new Button { Text = "Refresh", Location = new Point(992, 10), Size = new Size(75, 23), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(1073, 10), Size = new Size(75, 23), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bottom.Controls.AddRange(new Control[] { copy, export, fixButton, status, progress, runButton, close });

            filter.TextChanged += (s, e) => ApplyFilter();
            list.ColumnClick += (s, e) => SortBy(e.Column);
            // Double-click or Enter on a device.
            list.ItemActivate += (s, e) => OpenSystemInfo();
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyRows(true); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in list.Items) item.Selected = true; e.Handled = true; }
            };
            copy.Click += (s, e) => CopyRows(false);
            export.Click += (s, e) => Export();
            fixButton.Click += (s, e) => SafeAction.Run("Fix", FixClicked);
            fixButton.SizeChanged += (s, e) => PlaceStatus();
            fixButton.VisibleChanged += (s, e) => PlaceStatus();
            runButton.Click += (s, e) => { if (running) Stop(); else Run(rows); };
            summaryTimer.Tick += (s, e) =>
            {
                if (needsSort)
                {
                    needsSort = false;
                    list.Sort();
                }
                if (!dirty) return;
                dirty = false;
                UpdateSummary();
            };
            Shown += (s, e) =>
            {
                summaryTimer.Start();
                Run(rows);
            };
            // Closing stops the devices still waiting their turn; ones already answering finish out of sight.
            FormClosing += (s, e) => cancel?.Cancel();

            // Docked controls are laid out last-added first: the bottom bar, then the tiles at the top, the filter
            // under them, and the list in what's left.
            Controls.AddRange(new Control[] { listPanel, filterBar, tilePanel, bottom });
            CancelButton = close;
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cancel?.Cancel();
                summaryTimer.Dispose();
                tips.Dispose();
                fixMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        private Tile AddTile(string caption, string tip, Tone tone, Func<Row, bool> match)
        {
            Tile tile = new Tile(caption, tone, match) { Tip = tip };
            tile.Click += (s, e) => TileClicked(tile);
            tips.SetToolTip(tile, tip);
            tilePanel.Controls.Add(tile);
            tiles.Add(tile);
            return tile;
        }

        /// <summary>
        /// The run the grid compares with: each tile's count then, beside its own once a pass ends and in its hover
        /// text, and each device's change. Called when the window opens and when a pass starts on a newly kept run.
        /// </summary>
        private void ShowPrevious()
        {
            foreach (Tile tile in tiles)
            {
                tile.Previous = tile != changedTile && previous != null && previous.Tiles.TryGetValue(tile.Caption, out int count) ? count : (int?)null;
                string tip = tile == changedTile ? ChangedTip() : tile.Tip;
                if (tile.Previous.HasValue) tip += string.Format("\n\nThe last run, at {0:g}: {1:N0}", previous.Finished, tile.Previous.Value);
                tips.SetToolTip(tile, tip);
            }
            foreach (Row row in rows) ShowIssues(row);
        }

        private string ChangedTip()
        {
            if (historyPath == null) return "Devices whose issues changed since the last run. Only a collection's runs are kept to compare with, not a selection of devices";
            if (previous == null) return "Devices whose issues changed since the last full run of " + scope + ". There's none to compare with yet: this one is kept for the next";
            return string.Format("Devices whose issues changed since the full run at {0:g}, or that weren't in it. The Since last run column says what changed", previous.Finished);
        }

        /// <summary>
        /// What changed on a device since the run the grid compares with: "New: Not scanning; gone: Blue screen", or
        /// "Not in the last run". Null when nothing did, while it's still being checked, or with nothing to compare
        /// with. Not reporting comes from the site, which can answer after the device does: until it has, the last
        /// run's word on it stands.
        /// </summary>
        private string ChangeText(Row row)
        {
            if (previous == null || !row.Answered) return null;
            if (!previous.Devices.TryGetValue(row.ResourceId, out HealthRun.DeviceIssues before)) return "Not in the last run";
            List<string> now = row.IssueKeys();
            string notReporting = HealthCheck.Name(HealthIssue.NotReporting);
            if (!row.SiteRead && before.Issues.Contains(notReporting) && !now.Contains(notReporting)) now.Add(notReporting);
            List<string> added = now.Where(issue => !before.Issues.Contains(issue)).ToList();
            List<string> gone = before.Issues.Where(issue => !now.Contains(issue)).ToList();
            if (added.Count == 0 && gone.Count == 0) return null;
            List<string> parts = new List<string>();
            if (added.Count > 0) parts.Add("New: " + string.Join(", ", added));
            if (gone.Count > 0) parts.Add((parts.Count == 0 ? "Gone: " : "gone: ") + string.Join(", ", gone));
            return string.Join("; ", parts);
        }

        /// <summary>Devices in the run the grid compares with that aren't in the collection now.</summary>
        private int LeftSincePrevious()
        {
            if (previous == null) return 0;
            HashSet<int> now = new HashSet<int>(rows.Select(row => row.ResourceId));
            return previous.Devices.Keys.Count(id => !now.Contains(id));
        }

        /// <summary>
        /// Keeps a full pass for the next one to compare with, once the devices and the site have both answered, and
        /// logs what changed since the run before. A pass whose site read failed isn't kept: its Not reporting is unknown.
        /// </summary>
        private void KeepRun()
        {
            keepWhenSiteDone = false;
            if (!siteOk)
            {
                log.InfoFormat("{0} on {1}: this run isn't kept to compare with, since the site's policy requests couldn't be read", title, scope);
                return;
            }
            foreach (Tile tile in tiles) tile.Count = rows.Count(tile.Match);
            LogChanges();

            HealthRun run = new HealthRun(server, finishedAt);
            foreach (Row row in rows.Where(row => row.Answered)) run.Devices[row.ResourceId] = new HealthRun.DeviceIssues(row.Host, row.IssueKeys());
            foreach (Tile tile in tiles.Where(tile => tile != changedTile)) run.Tiles[tile.Caption] = tile.Count;
            try
            {
                run.Save(historyPath);
                kept = run;
                log.InfoFormat("{0} on {1}: kept this run to compare the next one with ({2})", title, scope, historyPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Warn(string.Format("{0} on {1}: couldn't keep this run to compare the next one with", title, scope), ex);
            }
        }

        /// <summary>What changed since the run the grid compares with, in one line: which devices, and how.</summary>
        private void LogChanges()
        {
            if (previous == null)
            {
                log.InfoFormat("{0} on {1}: no earlier run to compare with", title, scope);
                return;
            }
            List<Row> changed = rows.Where(row => row.Change != null).ToList();
            string which = changed.Count == 0
                ? string.Empty
                : ": " + string.Join("; ", changed.Take(20).Select(row => row.Host + " (" + row.Change + ")")) + (changed.Count > 20 ? "; ..." : string.Empty);
            log.InfoFormat("{0} on {1}: since the run at {2:g}, {3} changed{4}; {5} no longer in it",
                title, scope, previous.Finished, changed.Count, which, LeftSincePrevious());
        }

        private Tile AddIssueTile(HealthIssue issue, string tip)
        {
            return AddTile(HealthCheck.Name(issue), tip, Tone.Warn, row => row.Has(issue));
        }

        /// <summary>A tile shows just its devices; clicking it again, or Devices, shows them all.</summary>
        private void TileClicked(Tile tile)
        {
            activeTile = tile == activeTile || tile == allTile ? null : tile;
            foreach (Tile each in tiles) each.Selected = each == (activeTile ?? allTile);
            ApplyFilter();
        }

        private bool Matches(Row row)
        {
            if (activeTile != null && !activeTile.Match(row)) return false;
            string text = filter.Text.Trim();
            if (text.Length == 0) return true;
            foreach (ListViewItem.ListViewSubItem cell in row.Item.SubItems)
            {
                if (cell.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Shows the rows the active tile and the filter box let through, in the current sort.</summary>
        private void ApplyFilter()
        {
            ListViewItem[] items = rows.Where(Matches).Select(row => row.Item).ToArray();
            list.BeginUpdate();
            IComparer sorter = list.ListViewItemSorter;
            list.ListViewItemSorter = null;
            list.Items.Clear();
            list.Items.AddRange(items);
            list.ListViewItemSorter = sorter;   // setting it sorts
            list.EndUpdate();
            UpdateSummary();
        }

        /// <summary>Checks these devices, Layer8 Settings' number at a time. The grid fills in as each one answers.</summary>
        private void Run(List<Row> targets)
        {
            if (running || targets.Count == 0) return;
            running = true;
            stopping = false;
            // After a pass this window kept, that pass is the last run: compare with it now.
            if (kept != null)
            {
                previous = kept;
                kept = null;
                ShowPrevious();
            }
            fullRun = targets.Count == rows.Count;
            keepWhenSiteDone = false;
            siteDone = siteOk = false;
            foreach (Tile tile in tiles) tile.ShowDelta = false;
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            int devicesAtOnce = Layer8Settings.DevicesAtOnce;
            SemaphoreSlim slots = new SemaphoreSlim(devicesAtOnce);
            bool checkReachable = Layer8Settings.CheckReachable;
            int reachableMs = Layer8Settings.ReachableSeconds * 1000;

            runTotal = pending = targets.Count;
            list.BeginUpdate();
            runNumber++;
            foreach (Row row in targets)
            {
                row.State = RowState.Queued;
                row.Report = null;
                row.Site = null;
                row.SiteRead = false;
                row.Inventory = null;
                FillRow(row, "Queued");
            }
            list.EndUpdate();
            // A tile's view lets go of the devices being checked again; each comes back if it still matches.
            ApplyFilter();

            runButton.Text = "Stop";
            runButton.Enabled = true;
            progress.Maximum = runTotal;
            progress.Value = 0;
            progress.Visible = true;
            stopwatch.Restart();
            siteNote = null;
            log.InfoFormat("{0} on {1}: checking {2} device(s), {3} at a time", title, scope, runTotal, devicesAtOnce);
            UpdateSummary();

            // What the site last heard from each one: a query per 300 devices, alongside the devices' own checks.
            List<int> ids = targets.Select(row => row.ResourceId).ToList();
            int days = notReportingDays;
            Task.Run(() =>
            {
                try
                {
                    Dictionary<int, SiteContact> contact = HealthCheck.ReadSiteContact(connection, ids, days, token);
                    OnUi(() => ApplySiteContact(targets, contact));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Exception root = SafeAction.Unwrap(ex);
                    log.Warn(string.Format("{0} on {1}: couldn't read when the site last heard from each device", title, scope), root);
                    OnUi(() =>
                    {
                        siteNote = "couldn't read the site's policy requests: " + root.Message;
                        siteDone = true;
                        if (keepWhenSiteDone) KeepRun();
                        dirty = true;
                    });
                }
            });

            foreach (Row row in targets)
            {
                Row target = row;
                Task.Run(async () =>
                {
                    try
                    {
                        await slots.WaitAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Finish(target, RowState.Canceled, "Canceled", null);
                        return;
                    }
                    try
                    {
                        CheckRow(target, checkReachable, reachableMs, token);
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
            }
        }

        /// <summary>
        /// One device, on a background thread. What it finds goes back to the window's thread, exactly once whatever
        /// happens: the run ends when every device has answered.
        /// </summary>
        private void CheckRow(Row row, bool checkReachable, int reachableMs, CancellationToken token)
        {
            RowState state = RowState.Failed;
            string text = "Error: the check didn't finish";
            HealthReport report = null;
            try
            {
                if (token.IsCancellationRequested)
                {
                    state = RowState.Canceled;
                    text = "Canceled";
                }
                else if (!RemoteTools.IsComputerName(row.Host))
                {
                    // Placeholder records (Unknown Computer, Provisioning Device) aren't machines.
                    state = RowState.Skipped;
                    text = "Skipped: not a computer name";
                }
                else
                {
                    if (checkReachable)
                    {
                        Progress(row, "Checking it answers");
                        ClientActions.EnsureReachable(row.Host, reachableMs);
                    }
                    Progress(row, "Checking");
                    report = HealthCheck.Check(row.Host, lowDiskGB, longUptimeDays, blueScreenDays, windowsBuild, notScanningDays);
                    int issues = report.Issues.Count;
                    state = RowState.Checked;
                    text = issues == 0 ? "Healthy" : issues + (issues == 1 ? " issue" : " issues");
                    log.InfoFormat("{0} on {1}: {2}", title, row.Host, issues == 0 ? "healthy" : report.IssueText);
                }
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                report = null;
                text = DeviceProgressDialog.DescribeFailure(row.Host, root, out bool offline);
                state = offline ? RowState.Offline : RowState.Failed;
                log.Warn(string.Format("{0} on {1}: {2}", title, row.Host, text), root);
            }
            finally
            {
                Finish(row, state, text, report);
            }
        }

        private void Progress(Row row, string text)
        {
            OnUi(() =>
            {
                if (row.State != RowState.Queued && row.State != RowState.Checking) return;
                row.State = RowState.Checking;
                row.Item.SubItems[StatusColumn].Text = text;
            });
        }

        private void Finish(Row row, RowState state, string text, HealthReport report)
        {
            OnUi(() =>
            {
                row.State = state;
                row.Report = report;
                FillRow(row, text);
                // A tile or the filter box hides rows that didn't match before; this one may now. It's sorted into
                // place on the next tick, not now: sorting for every device that answers would crawl on a big list.
                if (row.Item.ListView == null && Matches(row))
                {
                    list.Items.Add(row.Item);
                    needsSort = true;
                }
                pending--;
                progress.Value = Math.Min(progress.Maximum, runTotal - pending);
                dirty = true;
                if (pending == 0) RunEnded();
            });
        }

        private void FillRow(Row row, string text)
        {
            HealthReport report = row.Report;
            ListViewItem item = row.Item;
            row.StatusText = text;
            SetCell(item, StatusColumn, new InfoCell(text));
            SetCell(item, 4, report?.SignedIn);
            SetCell(item, 5, report?.Uptime);
            SetCell(item, 6, report?.DiskFree);
            SetCell(item, 7, report?.Restart);
            SetCell(item, 8, report?.BitLocker);
            SetCell(item, 9, report?.Defender);
            SetCell(item, 10, report?.BlueScreen);
            SetCell(item, 11, report?.Windows);
            SetCell(item, 12, report?.Updates);
            SetCell(item, 13, report?.UpdateScan);
            SetCell(item, 14, report?.Client);
            ShowIssues(row);
            item.ForeColor = row.State == RowState.Offline || row.State == RowState.Skipped || row.State == RowState.Canceled
                ? SystemColors.GrayText
                : SystemColors.WindowText;
        }

        /// <summary>
        /// What's wrong, for the Issues column: the issues found, or that the device didn't answer or couldn't be
        /// checked (one issue each). Sorted by how many, so sorting the column puts every device that needs a look
        /// first, ahead of healthy and skipped ones, which leave it blank. What changed since the last run goes beside it.
        /// </summary>
        private void ShowIssues(Row row)
        {
            List<string> names = row.IssueNames();
            string issues = string.Join(", ", names);
            SetCell(row.Item, IssuesColumn, names.Count == 0 ? null : new InfoCell(issues, names.Count));
            row.Change = ChangeText(row);
            SetCell(row.Item, ChangeColumn, row.Change == null ? null : new InfoCell(row.Change));
            SetCell(row.Item, PolicyColumn, PolicyCell(row.Site));
            row.Item.ToolTipText = names.Count > 0 ? issues : row.Item.SubItems[StatusColumn].Text;
            if (row.State == RowState.Offline) ShowLastKnown(row);
        }

        /// <summary>
        /// A device that didn't answer: what ConfigMgr last had on it, in the row's grey, so it never reads as live.
        /// Status says when the site last heard from it; Signed in, Restart and Client come from its last check-ins,
        /// Windows from its last heartbeat, and C: from its last hardware inventory, with that inventory's age (it
        /// can be days old). Up, BitLocker, Defender, Blue screen and the update columns stay blank: only the device
        /// can answer those now. The tiles count live answers only, so none of this changes them.
        /// </summary>
        private void ShowLastKnown(Row row)
        {
            ListViewItem item = row.Item;
            SiteContact site = row.Site;
            SiteInventory inventory = row.Inventory;
            List<string> tip = new List<string> { item.ToolTipText, row.StatusText };

            if (site != null && site.LastSeen.HasValue)
            {
                TimeSpan age = HealthCheck.Age(site.LastSeen.Value);
                SetCell(item, StatusColumn, new InfoCell("Offline, last seen " + HealthCheck.FormatAge(age), age));
                tip.Add(string.Format("ConfigMgr last heard from it {0:g}. Grey values are ConfigMgr's last record, not live:", site.LastSeen.Value.ToLocalTime()));
            }
            if (site != null && site.IsClient)
            {
                if (site.LastUser != null) SetCell(item, 4, new InfoCell(site.LastUser + " (last)"));
                if (site.RestartFlags.HasValue) SetCell(item, 7, new InfoCell(HealthCheck.RestartText(site.RestartFlags.Value)));
                if (site.ClientVersion != null)
                {
                    SetCell(item, 14, new InfoCell(site.ClientVersion + (site.ClientCheck == 2 ? ", client check failed" : string.Empty),
                        Version.TryParse(site.ClientVersion, out Version version) ? version : null));
                }
                tip.Add("  Signed in, Restart, Client: its last check-ins");
            }
            if (inventory != null)
            {
                if (inventory.Build != null)
                {
                    SetCell(item, 11, new InfoCell(inventory.Build.ToString(), inventory.Build));
                    tip.Add(site != null && site.LastHeartbeat.HasValue
                        ? string.Format("  Windows: its last heartbeat, {0:g}", site.LastHeartbeat.Value.ToLocalTime())
                        : "  Windows: its device record");
                }
                if (inventory.DiskFree.HasValue)
                {
                    string asOf = inventory.DiskAsOf.HasValue ? " (" + HealthCheck.FormatAge(HealthCheck.Age(inventory.DiskAsOf.Value)) + ")" : string.Empty;
                    string size = inventory.DiskSize.HasValue && inventory.DiskSize.Value > 0 ? " of " + Utility.FormatBytes(inventory.DiskSize.Value, "0") : string.Empty;
                    SetCell(item, 6, new InfoCell(Utility.FormatBytes(inventory.DiskFree.Value, "0.#") + size + asOf, inventory.DiskFree.Value));
                    tip.Add(inventory.DiskAsOf.HasValue
                        ? string.Format("  C: free: its last hardware inventory, {0:g}", inventory.DiskAsOf.Value)
                        : "  C: free: its last hardware inventory");
                }
            }
            item.ToolTipText = string.Join(Environment.NewLine, tip.Where(line => !string.IsNullOrEmpty(line)).Distinct());
        }

        /// <summary>
        /// After a run: what the site's records hold for the devices that didn't answer (C: from the last hardware
        /// inventory, the Windows build from the device record), two queries per 300 of them, in the background.
        /// </summary>
        private void ReadLastKnown()
        {
            List<Row> offline = rows.Where(row => row.State == RowState.Offline).ToList();
            if (offline.Count == 0) return;
            // Not the run's token: a run stopped early still shows what the site has for the devices that didn't answer.
            int run = runNumber;
            CancellationToken token = CancellationToken.None;
            List<int> ids = offline.Select(row => row.ResourceId).ToList();
            Task.Run(() =>
            {
                try
                {
                    Dictionary<int, SiteInventory> inventory = HealthCheck.ReadSiteInventory(connection, ids, token);
                    OnUi(() =>
                    {
                        if (run != runNumber) return;
                        list.BeginUpdate();
                        foreach (Row row in offline)
                        {
                            if (row.State != RowState.Offline || !inventory.TryGetValue(row.ResourceId, out row.Inventory)) continue;
                            ShowIssues(row);
                        }
                        list.EndUpdate();
                        log.InfoFormat("{0} on {1}: ConfigMgr's last inventory read for {2} offline device(s)", title, scope, inventory.Count);
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    log.Warn(string.Format("{0} on {1}: couldn't read ConfigMgr's last inventory for the offline devices", title, scope), SafeAction.Unwrap(ex));
                }
            });
        }

        /// <summary>When the site last had a policy request from its client: "40m ago", "3d ago", "Never". Blank without a client.</summary>
        private static InfoCell PolicyCell(SiteContact site)
        {
            if (site == null || !site.IsClient) return null;
            if (!site.LastPolicyRequest.HasValue) return new InfoCell("Never", TimeSpan.MaxValue);
            TimeSpan age = HealthCheck.Age(site.LastPolicyRequest.Value);
            return new InfoCell(HealthCheck.FormatAge(age), age);
        }

        /// <summary>
        /// The site's side arrives: each row's Policy request cell, and Not reporting where it applies. Rows a tile
        /// or the filter box hid may match now.
        /// </summary>
        private void ApplySiteContact(List<Row> targets, Dictionary<int, SiteContact> contact)
        {
            list.BeginUpdate();
            foreach (Row row in targets)
            {
                contact.TryGetValue(row.ResourceId, out row.Site);
                row.SiteRead = true;
                ShowIssues(row);
                if (row.Item.ListView == null && Matches(row))
                {
                    list.Items.Add(row.Item);
                    needsSort = true;
                }
            }
            list.EndUpdate();
            dirty = true;
            log.InfoFormat("{0} on {1}: the site's last policy request read for {2} device(s); {3} not reporting in {4} days",
                title, scope, contact.Count, targets.Count(row => row.Has(HealthIssue.NotReporting)), notReportingDays);
            siteDone = siteOk = true;
            if (keepWhenSiteDone) KeepRun();
        }

        private static void SetCell(ListViewItem item, int column, InfoCell cell)
        {
            ListViewItem.ListViewSubItem subItem = item.SubItems[column];
            subItem.Text = cell?.Text ?? string.Empty;
            subItem.Tag = cell?.Key;
        }

        /// <summary>Devices waiting their turn are canceled; ones already answering finish and show what they found.</summary>
        private void Stop()
        {
            if (!running || stopping) return;
            stopping = true;
            cancel.Cancel();
            runButton.Text = "Stopping...";
            runButton.Enabled = false;
            log.InfoFormat("{0} on {1}: stopped", title, scope);
            UpdateSummary();
        }

        private void RunEnded()
        {
            running = false;
            stopwatch.Stop();
            finishedAt = DateTime.Now;
            runButton.Text = "Refresh";
            runButton.Enabled = true;
            progress.Visible = false;
            if (!fitted) fitted = FitColumns();
            // Rows that answered after the last sort go where they belong.
            needsSort = false;
            list.Sort();
            UpdateSummary();
            log.InfoFormat("{0} on {1} finished: {2} device(s) in {3}{4}; {5}", title, scope, runTotal, Elapsed(), stopping ? " (stopped early)" : string.Empty,
                string.Join(", ", tiles.Skip(1).Select(tile => tile.Caption.ToLowerInvariant() + " " + tile.Count)));

            foreach (Tile tile in tiles) tile.ShowDelta = true;
            ReadLastKnown();
            // Only a whole pass is kept: Check Again on a few devices, or a pass stopped early, would leave the rest
            // looking unchanged next time.
            if (fullRun && !stopping && historyPath != null)
            {
                if (siteDone) KeepRun();
                else keepWhenSiteDone = true;
            }
            else if (previous != null)
            {
                LogChanges();
            }
        }

        private void UpdateSummary()
        {
            foreach (Tile tile in tiles) tile.Count = rows.Count(tile.Match);

            if (running && stopping)
            {
                status.Text = string.Format("Stopping: waiting for {0} device(s) still answering", rows.Count(row => row.State == RowState.Checking));
            }
            else if (running)
            {
                status.Text = string.Format("Checking: {0} of {1} done", runTotal - pending, runTotal);
            }
            else if (runTotal > 0)
            {
                status.Text = string.Format("Checked {0} in {1}, at {2:t}{3}", Count(runTotal, "device"), Elapsed(), finishedAt, stopping ? " (stopped early)" : string.Empty);
                if (previous != null)
                {
                    int left = LeftSincePrevious();
                    status.Text += string.Format("; {0} changed since the run at {1}{2}", changedTile.Count,
                        previous.Finished.ToString(previous.Finished.Date == DateTime.Today ? "t" : "g"),
                        left > 0 ? string.Format(", {0} no longer in {1}", left, scope) : string.Empty);
                }
            }
            if (siteNote != null) status.Text += "; " + siteNote;

            int visible = list.Items.Count;
            shown.Text = visible == rows.Count
                ? "Showing all " + Count(rows.Count, "device")
                : string.Format("Showing {0} of {1}{2}", visible, Count(rows.Count, "device"), activeTile != null ? ": " + activeTile.Caption : string.Empty);
            UpdateFixButton();
        }

        /// <summary>
        /// The Fix button follows the active tile: shown when it has a fix, with how many of the devices shown it
        /// would run on, and off while a check is running (the rows are still changing). With none to run on it
        /// stays clickable, to say why: a disabled button shows no hover text.
        /// </summary>
        private void UpdateFixButton()
        {
            TileFix fix = activeTile?.Fix;
            fixButton.Visible = fix != null;
            if (fix == null) return;
            List<Row> targets = FixTargets(activeTile, out string leftOut);
            string text = string.Format("{0} ({1})...", fix.Label, targets.Count.ToString("N0"));
            string tip = running
                ? "Wait for the check to finish"
                : string.Format("{0}, on the {1} the grid shows that can take it{2}", string.Join(" or ", fix.Actions.Select(each => each.Name)),
                                Count(targets.Count, "device"), leftOut != null ? ". Left out: " + leftOut : string.Empty);
            bool enabled = !running;
            if (fixButton.Text != text) fixButton.Text = text;
            if (fixButton.Enabled != enabled) fixButton.Enabled = enabled;
            if (tips.GetToolTip(fixButton) != tip) tips.SetToolTip(fixButton, tip);
        }

        /// <summary>The status line starts after the Fix button when it's there, and where it was before when it isn't.</summary>
        private void PlaceStatus()
        {
            int left = fixButton.Visible ? fixButton.Right + LogicalToDeviceUnits(12) : fixButton.Left + LogicalToDeviceUnits(6);
            status.SetBounds(left, status.Top, Math.Max(0, progress.Left - LogicalToDeviceUnits(12) - left), status.Height);
        }

        /// <summary>
        /// The devices a tile's fix runs on: the rows the grid shows (the tile and the filter box), less those it
        /// can't reach or help. Why each was left out comes back counted ("2 offline, 1 not checked yet"), or null.
        /// </summary>
        private List<Row> FixTargets(Tile tile, out string leftOut)
        {
            List<Row> targets = new List<Row>();
            string[] reasons = { "not a computer name", "offline", "couldn't be checked", "not checked yet", "no client" };
            int[] counts = new int[reasons.Length];
            foreach (Row row in rows.Where(Matches))
            {
                int reason;
                if (!RemoteTools.IsComputerName(row.Host)) reason = 0;
                else if (row.State == RowState.Offline) reason = 1;
                else if (row.State == RowState.Failed) reason = 2;
                else if (row.State != RowState.Checked || row.Report == null) reason = 3;
                else if (tile == clientTile && row.Report.NoClient) reason = 4;
                else
                {
                    targets.Add(row);
                    continue;
                }
                counts[reason]++;
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < reasons.Length; i++)
            {
                if (counts[i] > 0) parts.Add(counts[i].ToString("N0") + " " + reasons[i]);
            }
            leftOut = parts.Count == 0 ? null : string.Join(", ", parts);
            return targets;
        }

        private void FixClicked()
        {
            Tile tile = activeTile;
            TileFix fix = tile?.Fix;
            if (fix == null || running) return;
            List<Row> targets = FixTargets(tile, out string leftOut);
            if (targets.Count == 0)
            {
                MessageBox.Show(this, string.Format("None of the devices shown can take {0}{1}.", string.Join(" or ", fix.Actions.Select(each => each.Name)),
                                leftOut != null ? ": " + leftOut : string.Empty), fix.Label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (fix.Actions.Length == 1)
            {
                RunFix(tile, fix.Actions[0]);
                return;
            }
            fixMenu.Items.Clear();
            foreach (DeviceAction each in fix.Actions)
            {
                DeviceAction chosen = each;
                fixMenu.Items.Add(new ToolStripMenuItem(chosen.Name + "...", null, (s, e) => SafeAction.Run(chosen.Name, () => RunFix(tile, chosen))));
            }
            fixMenu.Show(fixButton, new Point(0, fixButton.Height));
        }

        /// <summary>A tile's fix, on that tile's devices as the grid shows them now, through the right-click menu's own code.</summary>
        private void RunFix(Tile tile, DeviceAction chosen)
        {
            if (running || tile != activeTile) return;
            List<Row> targets = FixTargets(tile, out string leftOut);
            RunDeviceAction(chosen, targets, tile.Caption, leftOut);
        }

        private string Elapsed()
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            return elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        }

        private static string Count(int count, string noun) => count.ToString("N0") + " " + (count == 1 ? noun : noun + "s");

        /// <summary>
        /// Each column as wide as its header or its widest shown cell, up to a limit, once the first pass is done.
        /// Skipped records (Unknown Computer and the like) don't count: their long names say nothing about the
        /// devices, and would leave too little room for the columns that do. They're taken out while the list is
        /// measured and put back after; the caller sorts. False when there's nothing else shown yet to measure.
        /// </summary>
        private bool FitColumns()
        {
            ListViewItem[] skipped = list.Items.Cast<ListViewItem>().Where(item => ((Row)item.Tag).State == RowState.Skipped).ToArray();
            if (list.Items.Count == skipped.Length) return false;
            list.BeginUpdate();
            foreach (ListViewItem item in skipped) list.Items.Remove(item);
            for (int i = 0; i < list.Columns.Count; i++)
            {
                list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                int header = TextRenderer.MeasureText(Columns[i] + " ▼", list.Font).Width + LogicalToDeviceUnits(12);
                int limit = LogicalToDeviceUnits(i == StatusColumn || i == IssuesColumn || i == ChangeColumn ? 320 : 240);
                list.Columns[i].Width = Math.Min(Math.Max(list.Columns[i].Width + LogicalToDeviceUnits(6), header), limit);
            }
            list.Items.AddRange(skipped);
            list.EndUpdate();
            return true;
        }

        private void SortBy(int column)
        {
            descending = column == sortColumn && !descending;
            sortColumn = column;
            for (int i = 0; i < Columns.Length; i++)
            {
                list.Columns[i].Text = Columns[i] + (i == column ? (descending ? " ▼" : " ▲") : string.Empty);
            }
            list.ListViewItemSorter = new ListViewSorter(column, descending);   // setting it sorts
        }

        private void OpenSystemInfo()
        {
            if (list.SelectedItems.Count == 0) return;
            ListViewItem item = list.FocusedItem != null && list.FocusedItem.Selected ? list.FocusedItem : list.SelectedItems[0];
            string host = ((Row)item.Tag).Host;
            if (!RemoteTools.IsComputerName(host))
            {
                MessageBox.Show(this, host + " isn't a computer name.", "System Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            log.InfoFormat("{0}: System Information on {1}", title, host);
            using (SystemInfoDialog dialog = new SystemInfoDialog(host))
            {
                dialog.ShowDialog(this);
            }
        }

        /// <summary>The selected rows that are machines: placeholder records (Unknown Computer and the like) are left out.</summary>
        private List<Row> SelectedDevices()
        {
            return list.SelectedItems.Cast<ListViewItem>().Select(item => (Row)item.Tag).Where(row => RemoteTools.IsComputerName(row.Host)).ToList();
        }

        /// <summary>
        /// Runs a client action on these devices, in the same progress window as the console's Client Actions menu.
        /// From the right-click menu (no tile) it asks what the menus ask. From a tile's Fix button it always asks,
        /// once: what runs, on how many, and which devices were left out and why. The grid doesn't change by itself:
        /// check the devices again once the action has had time to work.
        /// </summary>
        private void RunDeviceAction(DeviceAction chosen, List<Row> targets, string tile = null, string leftOut = null)
        {
            if (targets.Count == 0) return;

            string actionTitle = chosen.Name.TrimEnd('.');
            string confirmQuestion = null;
            string actionName;
            Func<IResultObject, object> run;
            if (chosen.Tool != null)
            {
                if (!ClientActions.PrepareClientTool(chosen.Tool, out run, out string toolTitle, out confirmQuestion)) return;
                actionName = chosen.Tool;
                actionTitle = toolTitle ?? actionTitle;
            }
            else
            {
                string schedule = chosen.Schedule;
                bool full = chosen.Full;
                actionName = nameof(ClientActions.ClientAction);
                run = device => { ClientActions.ClientAction(device, schedule, full); return null; };
            }

            // The same questions the menus ask: the action's own, or, for a big selection, whether to go ahead.
            string target = targets.Count == 1 ? targets[0].Host : targets.Count + " devices";
            string from = tile == null ? string.Empty : "tile " + tile + ": ";
            string left = leftOut == null ? string.Empty : " (left out: " + leftOut + ")";
            int confirmAbove = Layer8Settings.ConfirmAbove;
            if (tile != null)
            {
                string question = confirmQuestion != null ? string.Format(confirmQuestion, target) : RunQuestion(actionTitle, target);
                if (leftOut != null) question += "\n\nLeft out: " + leftOut + ".";
                if (MessageBox.Show(this, question, actionTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    log.InfoFormat("{0} on {1}: {2}{3} on {4}{5}: canceled at the question", title, scope, from, actionTitle, target, left);
                    return;
                }
            }
            else if (confirmQuestion != null)
            {
                if (MessageBox.Show(this, string.Format(confirmQuestion, target), actionTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            else if (confirmAbove > 0 && targets.Count > confirmAbove &&
                     MessageBox.Show(this, RunQuestion(actionTitle, target), actionTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            List<IResultObject> devices = DeploymentRetry.DevicesById(connection, targets.Select(row => row.ResourceId).ToList());
            log.InfoFormat("{0} on {1}: {2}{3} on {4}{5}", title, scope, from, actionTitle, target, left);
            ClientActions.RunOnDevices(action, devices, actionName, run, actionTitle);
        }

        /// <summary>"Run Software Updates Scan Cycle on 3 devices?", and "Run Client Health Check on 3 devices?" rather than "Run Run ...".</summary>
        private static string RunQuestion(string actionTitle, string target) =>
            (actionTitle.StartsWith("Run ", StringComparison.Ordinal) ? string.Empty : "Run ") + actionTitle + " on " + target + "?";

        private void CheckAgain()
        {
            if (running) return;
            Run(list.SelectedItems.Cast<ListViewItem>().Select(item => (Row)item.Tag).ToList());
        }

        /// <summary>The header, then the rows as the grid shows them (filtered and sorted), or just the selected ones.</summary>
        private IEnumerable<string[]> TableRows(bool selectedOnly)
        {
            yield return Columns;
            IEnumerable<ListViewItem> items = selectedOnly && list.SelectedItems.Count > 0
                ? list.SelectedItems.Cast<ListViewItem>()
                : list.Items.Cast<ListViewItem>();
            foreach (ListViewItem item in items)
            {
                string[] row = new string[Columns.Length];
                for (int i = 0; i < row.Length; i++) row[i] = i < item.SubItems.Count ? item.SubItems[i].Text : string.Empty;
                yield return row;
            }
        }

        private void CopyRows(bool selectedOnly)
        {
            StringBuilder text = new StringBuilder();
            foreach (string[] row in TableRows(selectedOnly)) text.AppendLine(string.Join("\t", row));
            Clipboard.SetDataObject(text.ToString(), true, 10, 100);
        }

        private void Export()
        {
            string name = title + " " + scope;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');
            using (SaveFileDialog save = new SaveFileDialog
            {
                Title = "Export " + title,
                Filter = "CSV files (*.csv)|*.csv",
                FileName = string.Format("{0} {1:yyyy-MM-dd HHmm}.csv", name, DateTime.Now),
            })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder csv = new StringBuilder();
                foreach (string[] row in TableRows(false)) csv.AppendLine(string.Join(",", row.Select(DeviceProgressDialog.CsvField)));
                // With a byte order mark, Excel reads the file as UTF-8 instead of guessing.
                File.WriteAllText(save.FileName, csv.ToString(), new UTF8Encoding(true));
                log.InfoFormat("{0} on {1}: exported {2} row(s) to {3}", title, scope, list.Items.Count, save.FileName);
            }
        }

        // The checks run on background threads. Anything that touches the window goes back to its thread, and
        // does nothing once the window has closed.
        private void OnUi(Action update)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(update);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>A count with its name, clicked to show just those devices. Coloured only while its count isn't zero.</summary>
        private sealed class Tile : Control
        {
            private int count;
            private bool selected;
            private Font numberFont;

            public Tile(string caption, Tone tone, Func<Row, bool> match)
            {
                Caption = caption;
                Tone = tone;
                Match = match;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
                // Two quick clicks are two clicks (show, then show all again), not a double-click.
                SetStyle(ControlStyles.StandardDoubleClick, false);
                TabStop = true;
                Cursor = Cursors.Hand;
                Size = new Size(96, 50);
                Margin = new Padding(0, 0, 6, 6);
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = caption + ": 0";
            }

            public string Caption { get; }
            public Tone Tone { get; }
            public Func<Row, bool> Match { get; }

            /// <summary>What its Fix button runs; null for a tile without one.</summary>
            public TileFix Fix { get; set; }

            /// <summary>Its rule, for the hover text; the window adds the last run's count to it.</summary>
            public string Tip { get; set; }

            public int Count
            {
                get { return count; }
                set
                {
                    if (count == value) return;
                    count = value;
                    UpdateAccessibleName();
                    Invalidate();
                }
            }

            private int? previous;
            /// <summary>Its count in the run the window compares with; null with nothing to compare with.</summary>
            public int? Previous
            {
                get { return previous; }
                set
                {
                    if (previous == value) return;
                    previous = value;
                    UpdateAccessibleName();
                    Invalidate();
                }
            }

            private bool showDelta;
            /// <summary>Show the change from Previous beside the count: once a pass has ended, not while counts climb.</summary>
            public bool ShowDelta
            {
                get { return showDelta; }
                set
                {
                    if (showDelta == value) return;
                    showDelta = value;
                    Invalidate();
                }
            }

            private void UpdateAccessibleName()
            {
                AccessibleName = Caption + ": " + count + (previous.HasValue && previous.Value != count ? ", was " + previous.Value : string.Empty);
            }

            public bool Selected
            {
                get { return selected; }
                set
                {
                    if (selected == value) return;
                    selected = value;
                    Invalidate();
                }
            }

            protected override bool IsInputKey(Keys keyData) => keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);

            protected override void OnKeyUp(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
                {
                    OnClick(EventArgs.Empty);
                    e.Handled = true;
                }
                base.OnKeyUp(e);
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void OnFontChanged(EventArgs e)
            {
                numberFont?.Dispose();
                numberFont = null;
                base.OnFontChanged(e);
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) numberFont?.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (numberFont == null) numberFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.75f, FontStyle.Bold, GraphicsUnit.Point);
                Colors(Tone == Tone.Neutral || count > 0, out Color back, out Color border, out Color number, out Color caption);

                Graphics g = e.Graphics;
                using (SolidBrush brush = new SolidBrush(back)) g.FillRectangle(brush, ClientRectangle);
                int edge = selected ? Math.Max(2, LogicalToDeviceUnits(3)) : 1;
                using (Pen pen = new Pen(selected ? SystemColors.Highlight : border))
                {
                    for (int i = 0; i < edge; i++) g.DrawRectangle(pen, i, i, Width - 1 - 2 * i, Height - 1 - 2 * i);
                }

                int left = LogicalToDeviceUnits(8);
                int top = LogicalToDeviceUnits(4);
                int width = Width - left - edge - 2;
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                string countText = count.ToString("N0");
                TextRenderer.DrawText(g, countText, numberFont, new Rectangle(left, top, width, numberFont.Height), number, flags);
                if (showDelta && previous.HasValue && previous.Value != count)
                {
                    // "+1" or "-2", small, after the count and level with its foot: up or down since the last run.
                    int delta = count - previous.Value;
                    string deltaText = (delta > 0 ? "+" : string.Empty) + delta.ToString("N0");
                    int x = left + TextRenderer.MeasureText(g, countText, numberFont, Size.Empty, flags).Width + LogicalToDeviceUnits(4);
                    int y = top + numberFont.Height - Font.Height - LogicalToDeviceUnits(2);
                    TextRenderer.DrawText(g, deltaText, Font, new Rectangle(x, y, Math.Max(0, Width - x - edge - 2), Font.Height + 2), caption, flags);
                }
                TextRenderer.DrawText(g, Caption, Font, new Rectangle(left, top + numberFont.Height, width, Font.Height + 2), caption, flags);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -edge - 2, -edge - 2));
            }

            /// <summary>Tinted by kind while lit; plain system colours in high contrast, and while there's nothing to count.</summary>
            private void Colors(bool lit, out Color back, out Color border, out Color number, out Color caption)
            {
                back = SystemColors.Window;
                border = SystemColors.ControlDark;
                number = SystemColors.WindowText;
                caption = SystemColors.WindowText;
                if (!lit)
                {
                    if (!SystemInformation.HighContrast) border = SystemColors.ControlLight;
                    number = caption = SystemColors.GrayText;
                    return;
                }
                if (SystemInformation.HighContrast) return;
                switch (Tone)
                {
                    case Tone.Good:
                        back = Color.FromArgb(234, 246, 236);
                        border = Color.FromArgb(46, 139, 87);
                        number = Color.FromArgb(16, 112, 52);
                        break;
                    case Tone.Muted:
                        back = Color.FromArgb(240, 240, 240);
                        border = Color.FromArgb(150, 150, 150);
                        number = Color.FromArgb(80, 80, 80);
                        break;
                    case Tone.Warn:
                        back = Color.FromArgb(255, 244, 229);
                        border = Color.FromArgb(222, 133, 23);
                        number = Color.FromArgb(166, 84, 0);
                        break;
                    case Tone.Bad:
                        back = Color.FromArgb(253, 236, 236);
                        border = Color.FromArgb(206, 58, 58);
                        number = Color.FromArgb(166, 32, 32);
                        break;
                    case Tone.Info:
                        back = Color.FromArgb(232, 241, 251);
                        border = Color.FromArgb(0, 103, 192);
                        number = Color.FromArgb(0, 84, 166);
                        break;
                }
            }
        }
    }
}
