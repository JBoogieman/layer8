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
            List<string> names = new List<string>();
            string scope;
            if (isCollection)
            {
                scope = selected["Name"].StringValue;
                string query = string.Format("SELECT * FROM SMS_FullCollectionMembership WHERE CollectionID='{0}'", selected["CollectionID"].StringValue);
                try
                {
                    foreach (IResultObject member in selected.ConnectionManager.QueryProcessor.ExecuteQuery(query))
                    {
                        names.Add(member["Name"].StringValue);
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
                    names.Add(device["Name"].StringValue);
                }
                scope = names.Count == 1 ? names[0] : names.Count + " selected devices";
            }

            // Each device once, even when two records share its name (a reimaged device, say).
            List<string> devices = names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

            using (HealthDashboardDialog dialog = new HealthDashboardDialog(title, scope, devices))
            {
                dialog.ShowDialog();
            }
        }
    }

    internal sealed class HealthDashboardDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(HealthDashboardDialog));

        private enum RowState { Queued, Checking, Checked, Offline, Failed, Skipped, Canceled }

        private enum Tone { Neutral, Good, Muted, Warn, Bad }

        /// <summary>One device: its row in the grid (kept while a filter hides it), and what the last check found.</summary>
        private sealed class Row
        {
            public Row(string host, int columns)
            {
                Host = host;
                Item = new ListViewItem(host) { Tag = this };
                for (int i = 1; i < columns; i++) Item.SubItems.Add(string.Empty);
            }

            public readonly string Host;
            public readonly ListViewItem Item;
            /// <summary>Changed on the window's thread only, like everything the grid shows.</summary>
            public RowState State;
            public HealthReport Report;

            public bool Has(HealthIssue issue) => Report != null && Report.Issues.Contains(issue);
        }

        private static readonly string[] Columns = { "Device", "Status", "Issues", "Signed in", "Up", "C: free", "Restart", "BitLocker", "Defender", "Updates missing", "Client" };
        private static readonly int[] StartWidths = { 120, 90, 220, 130, 60, 110, 110, 90, 90, 130, 110 };
        private const int StatusColumn = 1;
        private const int IssuesColumn = 2;

        private readonly string title;
        private readonly string scope;
        private readonly List<Row> rows;
        // Read once, so the tiles' rules and the checks agree for the whole window.
        private readonly int lowDiskGB = Layer8Settings.LowDiskGB;
        private readonly int longUptimeDays = Layer8Settings.LongUptimeDays;

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
        // Devices answer up to 32 at a time; the tiles and the status line catch up a few times a second.
        private readonly System.Windows.Forms.Timer summaryTimer = new System.Windows.Forms.Timer { Interval = 300 };

        private CancellationTokenSource cancel;
        private bool running, stopping, dirty, needsSort, fitted;
        private int runTotal, pending;
        private readonly Stopwatch stopwatch = new Stopwatch();
        private DateTime finishedAt;
        private int sortColumn;
        private bool descending;

        public HealthDashboardDialog(string title, string scope, IEnumerable<string> devices)
        {
            this.title = title;
            this.scope = scope;
            rows = devices.Select(device => new Row(device, Columns.Length)).ToList();

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
                row => row.State == RowState.Checked && row.Report.Issues.Count == 0);
            AddTile("Offline", "Didn't answer: ports 135 and 445 closed, or WMI's RPC server unreachable", Tone.Muted,
                row => row.State == RowState.Offline);
            AddIssueTile(HealthIssue.RestartPending, "ConfigMgr, Windows servicing or Windows Update is waiting on a restart");
            AddIssueTile(HealthIssue.LowDisk, string.Format("Less than {0} GB free on C:", lowDiskGB));
            AddIssueTile(HealthIssue.BitLockerOff, "C: isn't protected by BitLocker: off, or protection suspended");
            AddIssueTile(HealthIssue.Defender, string.Format(
                "Microsoft Defender antivirus or real-time protection is off, or its signatures are more than {0} days old. Not counted when Defender is passive behind another antivirus",
                HealthCheck.DefenderSignatureDays));
            AddIssueTile(HealthIssue.MissingUpdates, "Deployed software updates the client reports as missing");
            AddIssueTile(HealthIssue.Client, "No ConfigMgr client, its service (CcmExec) isn't running, or it's in provisioning mode");
            AddIssueTile(HealthIssue.LongUptime, string.Format("Up {0} days or more since the last restart", longUptimeDays));
            AddTile(HealthCheck.Name(HealthIssue.CouldNotCheck),
                "Answered, but couldn't be checked (access denied, a WMI error), or one check couldn't be read: the Issues column says which", Tone.Bad,
                row => row.State == RowState.Failed || row.Has(HealthIssue.CouldNotCheck));

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
            menu.Items.AddRange(new ToolStripItem[] { systemInfo, checkAgain, new ToolStripSeparator(), copySelected });
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
            };
            list.ContextMenuStrip = menu;

            // The bottom bar. Buttons stay off the top right: with the window maximized, Windows 11 takes clicks
            // there as clicks on the title bar's buttons.
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Size = new Size(1160, 44) };
            Button copy = new Button { Text = "Copy", Location = new Point(12, 10), Size = new Size(75, 23) };
            Button export = new Button { Text = "Export...", Location = new Point(93, 10), Size = new Size(75, 23) };
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
            bottom.Controls.AddRange(new Control[] { copy, export, status, progress, runButton, close });

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
            }
            base.Dispose(disposing);
        }

        private Tile AddTile(string caption, string tip, Tone tone, Func<Row, bool> match)
        {
            Tile tile = new Tile(caption, tone, match);
            tile.Click += (s, e) => TileClicked(tile);
            tips.SetToolTip(tile, tip);
            tilePanel.Controls.Add(tile);
            tiles.Add(tile);
            return tile;
        }

        private void AddIssueTile(HealthIssue issue, string tip)
        {
            AddTile(HealthCheck.Name(issue), tip, Tone.Warn, row => row.Has(issue));
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
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            int devicesAtOnce = Layer8Settings.DevicesAtOnce;
            SemaphoreSlim slots = new SemaphoreSlim(devicesAtOnce);
            bool checkReachable = Layer8Settings.CheckReachable;
            int reachableMs = Layer8Settings.ReachableSeconds * 1000;

            runTotal = pending = targets.Count;
            list.BeginUpdate();
            foreach (Row row in targets)
            {
                row.State = RowState.Queued;
                row.Report = null;
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
            log.InfoFormat("{0} on {1}: checking {2} device(s), {3} at a time", title, scope, runTotal, devicesAtOnce);
            UpdateSummary();

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
                    report = HealthCheck.Check(row.Host, lowDiskGB, longUptimeDays);
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

        private static void FillRow(Row row, string text)
        {
            HealthReport report = row.Report;
            ListViewItem item = row.Item;
            SetCell(item, StatusColumn, new InfoCell(text));
            SetCell(item, IssuesColumn, IssuesCell(row));
            SetCell(item, 3, report?.SignedIn);
            SetCell(item, 4, report?.Uptime);
            SetCell(item, 5, report?.DiskFree);
            SetCell(item, 6, report?.Restart);
            SetCell(item, 7, report?.BitLocker);
            SetCell(item, 8, report?.Defender);
            SetCell(item, 9, report?.Updates);
            SetCell(item, 10, report?.Client);
            item.ToolTipText = report != null && report.Issues.Count > 0 ? report.IssueText : text;
            item.ForeColor = row.State == RowState.Offline || row.State == RowState.Skipped || row.State == RowState.Canceled
                ? SystemColors.GrayText
                : SystemColors.WindowText;
        }

        /// <summary>
        /// What's wrong, for the Issues column: the issues found, or that the device didn't answer or couldn't be
        /// checked (one issue each). Sorted by how many, so sorting the column puts every device that needs a look
        /// first, ahead of healthy and skipped ones, which leave it blank.
        /// </summary>
        private static InfoCell IssuesCell(Row row)
        {
            switch (row.State)
            {
                case RowState.Offline:
                    return new InfoCell("Offline", 1);
                case RowState.Failed:
                    return new InfoCell(HealthCheck.Name(HealthIssue.CouldNotCheck), 1);
                case RowState.Checked:
                    return row.Report.Issues.Count == 0 ? null : new InfoCell(row.Report.IssueText, row.Report.Issues.Count);
                default:
                    return null;
            }
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
            }

            int visible = list.Items.Count;
            shown.Text = visible == rows.Count
                ? "Showing all " + Count(rows.Count, "device")
                : string.Format("Showing {0} of {1}{2}", visible, Count(rows.Count, "device"), activeTile != null ? ": " + activeTile.Caption : string.Empty);
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
                int limit = LogicalToDeviceUnits(i == StatusColumn || i == IssuesColumn ? 320 : 240);
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

            public int Count
            {
                get { return count; }
                set
                {
                    if (count == value) return;
                    count = value;
                    AccessibleName = Caption + ": " + count;
                    Invalidate();
                }
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
                TextRenderer.DrawText(g, count.ToString("N0"), numberFont, new Rectangle(left, top, width, numberFont.Height), number, flags);
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
                }
            }
        }
    }
}
