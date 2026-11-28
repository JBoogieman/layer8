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
    /// The AD Cleanup Report's window: tiles across the top count each finding, clicking one shows just those
    /// computers, and the grid below sorts, filters, copies and exports. Only the Fix button changes anything: on the
    /// Stale and Disabled tiles, for the selected computer objects, in AD, after a question.
    /// </summary>
    internal sealed class ADCleanupDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ADCleanupDialog));

        /// <summary>One computer object or site record: its row in the grid, kept while a filter hides it.</summary>
        private sealed class Row
        {
            public Row(CleanupEntry entry, ListViewItem item)
            {
                Entry = entry;
                Item = item;
                item.Tag = this;
            }

            public readonly CleanupEntry Entry;
            public readonly ListViewItem Item;
        }

        private static readonly string[] Columns = { "Computer", "Finding", "Notes", "Enabled", "Last seen in AD", "Last logon", "Password set", "Created", "Operating system", "Location in AD", "Resource ID", "Client", "Client last heard from", "Found by" };
        private static readonly int[] StartWidths = { 120, 90, 260, 60, 130, 130, 130, 130, 180, 260, 80, 50, 140, 200 };

        private readonly string title;
        private readonly ConnectionManagerBase connection;
        // Read once, so the tiles' rules and their hover text agree for the whole window.
        private readonly int days = Layer8Settings.ADNotSeenDays;
        private List<Row> rows = new List<Row>();

        private readonly ToolTip tips = new ToolTip();
        private readonly FlowLayoutPanel tilePanel;
        private readonly List<Tile<Row>> tiles = new List<Tile<Row>>();
        private readonly Tile<Row> allTile;
        private Tile<Row> activeTile;
        private readonly TextBox filter;
        private readonly CheckBox workstationsOnly;
        private readonly Label shown;
        private readonly ListView list;
        private readonly Label status;
        private readonly ProgressBar progress;
        private readonly Button refresh;
        private readonly Button fixButton;
        private readonly ContextMenuStrip fixMenu = new ContextMenuStrip();
        // The finding behind each tile that has one, so the Fix button knows which fixes the active tile offers.
        private readonly Dictionary<Tile<Row>, CleanupFinding> tileFindings = new Dictionary<Tile<Row>, CleanupFinding>();

        private CancellationTokenSource cancel;
        private CancellationTokenSource fixCancel;
        private bool running;
        // A fix is reading or changing AD: no second one, and no refresh, until it's done.
        private bool fixing;
        private bool fixButtonPending;
        // The last read: the domain controller every later read and fix goes to, the domain, the site's servers.
        private CleanupRead lastRead;
        private int sortColumn;
        private bool descending;

        public ADCleanupDialog(string title, ConnectionManagerBase connection)
        {
            this.title = title;
            this.connection = connection;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = title;
            ClientSize = new Size(1160, 640);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;

            tilePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(9, 10, 9, 2),
            };
            allTile = AddTile("Computers", "Every computer object in the domain and every device record on the site, matched where they're the same computer. Click to show them all", Tone.Neutral, row => true);
            allTile.Selected = true;
            string inDays = ADCleanup.Days(days);
            AddTile(CleanupFinding.OK, Tone.Good, string.Format("In AD and on the site, seen by both in the last {0}", inDays));
            AddTile(CleanupFinding.NotInAD, Tone.Warn,
                "A device record from this domain whose computer object is gone from AD. The site keeps a record until its Delete Aged Discovery Data task removes it");
            AddTile(CleanupFinding.Disabled, Tone.Warn, "The computer object is disabled in AD");
            AddTile(CleanupFinding.Stale, Tone.Warn, string.Format(
                "Enabled, but AD hasn't seen the computer in {0} or more (its last logon or password change), and no client has been heard from in that time either", inDays));
            AddTile(CleanupFinding.ADUnused, Tone.Warn, string.Format(
                "Its client still reports, but AD hasn't seen the computer's account in {0} or more: a broken trust relationship, or a computer renamed or joined again under another account", inDays));
            AddTile(CleanupFinding.ClientSilent, Tone.Warn, string.Format(
                "Seen by AD in the last {0}, but the site hasn't heard from its client in {0} or more", inDays));
            AddTile(CleanupFinding.NoClient, Tone.Warn, string.Format(
                "Seen by AD in the last {0}, with no ConfigMgr client: the site has no record of it, or its record has no client", inDays));
            AddTile(CleanupFinding.NotChecked, Tone.Muted,
                "Site records from another domain or a workgroup (only this domain is read), and records two computer objects could be: the Notes column says which");

            Panel filterBar = new Panel { Dock = DockStyle.Top, Height = 32 };
            Label filterLabel = new Label { Text = "Filter:", Location = new Point(12, 9), AutoSize = true };
            filter = new TextBox { Location = new Point(52, 6), Size = new Size(260, 20) };
            workstationsOnly = new CheckBox { Text = "Workstations only", Location = new Point(326, 7), AutoSize = true };
            tips.SetToolTip(workstationsOnly, "Leave out servers and domain controllers, by the operating system AD or the site has for them. Many have no client on purpose");
            shown = new Label { Location = new Point(460, 9), AutoSize = true };
            filterBar.Controls.AddRange(new Control[] { filterLabel, filter, workstationsOnly, shown });

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                ShowItemToolTips = true,
            };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(list, true, null);
            for (int i = 0; i < Columns.Length; i++) list.Columns.Add(Columns[i] + (i == 0 ? " ▲" : string.Empty), LogicalToDeviceUnits(StartWidths[i]));
            list.ListViewItemSorter = new ListViewSorter(0, false);
            Panel listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            listPanel.Controls.Add(list);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Copy", null, (s, e) => CopyRows(true)));
            menu.Opening += (s, e) => e.Cancel = list.SelectedItems.Count == 0;
            list.ContextMenuStrip = menu;

            // The bottom bar, as the dashboard's: buttons stay off the top right, which a maximized window on
            // Windows 11 gives to the title bar's buttons.
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Size = new Size(1160, 44) };
            Button copy = new Button { Text = "Copy", Location = new Point(12, 10), Size = new Size(75, 23) };
            Button export = new Button { Text = "Export...", Location = new Point(93, 10), Size = new Size(75, 23) };
            // Shown on a tile with fixes, as the dashboard's is; sized to its text ("Fix (1,234)...").
            fixButton = new Button { Text = "Fix...", Location = new Point(174, 10), Size = new Size(75, 23), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Visible = false };
            status = new Label
            {
                Location = new Point(180, 10),
                Size = new Size(640, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            progress = new ProgressBar { Location = new Point(832, 13), Size = new Size(150, 17), Anchor = AnchorStyles.Top | AnchorStyles.Right, Style = ProgressBarStyle.Marquee, Visible = false };
            refresh = new Button { Text = "Refresh", Location = new Point(992, 10), Size = new Size(75, 23), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(1073, 10), Size = new Size(75, 23), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bottom.Controls.AddRange(new Control[] { copy, export, fixButton, status, progress, refresh, close });

            filter.TextChanged += (s, e) => ApplyFilter();
            workstationsOnly.CheckedChanged += (s, e) => { CountTiles(); ApplyFilter(); };
            // Ctrl+A raises this once a row: the button is worked out once, after the burst.
            list.SelectedIndexChanged += (s, e) => QueueFixButton();
            fixButton.Click += (s, e) => SafeAction.Run(title + ": Fix", FixClicked);
            fixButton.SizeChanged += (s, e) => PlaceStatus();
            fixButton.VisibleChanged += (s, e) => PlaceStatus();
            list.ColumnClick += (s, e) => SortBy(e.Column);
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyRows(true); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in list.Items) item.Selected = true; e.Handled = true; }
            };
            copy.Click += (s, e) => CopyRows(false);
            export.Click += (s, e) => SafeAction.Run("Export", Export);
            refresh.Click += (s, e) => Run();
            Shown += (s, e) => Run();
            // A fix that's running stops before its next computer object.
            FormClosing += (s, e) => { cancel?.Cancel(); fixCancel?.Cancel(); };

            // Docked controls are laid out last-added first: the bottom bar, the tiles, the filter under them, the list.
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
                fixCancel?.Cancel();
                tips.Dispose();
                fixMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        private void AddTile(CleanupFinding finding, Tone tone, string tip)
        {
            tileFindings[AddTile(ADCleanup.Name(finding), tip, tone, row => row.Entry.Finding == finding)] = finding;
        }

        private Tile<Row> AddTile(string caption, string tip, Tone tone, Func<Row, bool> match)
        {
            Tile<Row> tile = new Tile<Row>(caption, tone, match) { Tip = tip };
            tile.Click += (s, e) => TileClicked(tile);
            tips.SetToolTip(tile, tip);
            tilePanel.Controls.Add(tile);
            tiles.Add(tile);
            return tile;
        }

        /// <summary>A tile shows just its computers; clicking it again, or Computers, shows them all.</summary>
        private void TileClicked(Tile<Row> tile)
        {
            activeTile = tile == activeTile || tile == allTile ? null : tile;
            foreach (Tile<Row> each in tiles) each.Selected = each == (activeTile ?? allTile);
            ApplyFilter();
            UpdateFixButton();
        }

        private bool Counted(Row row) => !workstationsOnly.Checked || !row.Entry.IsServer;

        private void CountTiles()
        {
            foreach (Tile<Row> tile in tiles) tile.Count = rows.Where(Counted).Count(tile.Match);
        }

        private bool Matches(Row row)
        {
            if (!Counted(row)) return false;
            if (activeTile != null && !activeTile.Match(row)) return false;
            string text = filter.Text.Trim();
            if (text.Length == 0) return true;
            foreach (ListViewItem.ListViewSubItem cell in row.Item.SubItems)
            {
                if (cell.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Shows the rows the box, the active tile and the filter let through, in the current sort.</summary>
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
            shown.Text = items.Length == rows.Count ? string.Empty : string.Format("Showing {0:N0} of {1:N0}", items.Length, rows.Count);
            QueueFixButton();
        }

        /// <summary>Reads AD and the site on a background thread, then fills the tiles and the grid.</summary>
        private void Run()
        {
            if (running || fixing) return;
            running = true;
            refresh.Enabled = false;
            progress.Visible = true;
            status.Text = "Reading Active Directory and the site...";
            UpdateFixButton();
            cancel?.Dispose();
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            Stopwatch watch = Stopwatch.StartNew();
            int notSeenDays = days;
            // A refresh asks the same domain controller, so a fix just made shows.
            string server = lastRead?.Server;
            Task.Run(() =>
            {
                CleanupRead read = ADCleanup.Read(connection, server, token);
                List<CleanupEntry> entries = ADCleanup.Compare(read.Computers, read.Records, read.Domain, notSeenDays, DateTime.UtcNow);
                return new KeyValuePair<CleanupRead, List<CleanupEntry>>(read, entries);
            }, token).ContinueWith(task => OnUi(() =>
            {
                running = false;
                refresh.Enabled = true;
                progress.Visible = false;
                if (task.IsCanceled) return;
                if (task.IsFaulted)
                {
                    Exception root = SafeAction.Unwrap(task.Exception);
                    log.Warn(title + ": couldn't read Active Directory or the site", root);
                    status.Text = "Couldn't read Active Directory or the site: " + root.Message.Trim();
                    tips.SetToolTip(status, status.Text);
                    UpdateFixButton();
                    return;
                }
                ShowResults(task.Result.Key, task.Result.Value, watch.Elapsed);
            }), TaskScheduler.Default);
        }

        private void ShowResults(CleanupRead read, List<CleanupEntry> entries, TimeSpan took)
        {
            lastRead = read;
            rows = entries.Select(entry => new Row(entry, Item(entry))).ToList();
            CountTiles();
            ApplyFilter();
            string elapsed = took.ToString(took.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            status.Text = string.Format("{0:N0} computer objects in {1} and {2:N0} device records on the site, read in {3}. Not seen in {4} counts (Layer8 Settings).{5}",
                read.Computers.Count, read.Domain, read.Records.Count, elapsed, ADCleanup.Days(days),
                read.Obsolete > 0 ? string.Format(" {0:N0} obsolete records left out.", read.Obsolete) : string.Empty);
            tips.SetToolTip(status, status.Text + "\n\nSearched " + read.DomainPath + " on " + read.Server);
            log.InfoFormat("{0}: {1} computer objects in {2}, {3} device records ({4} obsolete left out), not seen in {5}; {6}",
                title, read.Computers.Count, read.Domain, read.Records.Count, read.Obsolete, ADCleanup.Days(days),
                string.Join(", ", tiles.Where(tile => tile != allTile).Select(tile => tile.Caption + " " + rows.Count(tile.Match))));
        }

        /// <summary>One row of the grid. Dates show in local time and sort by their value.</summary>
        private static ListViewItem Item(CleanupEntry entry)
        {
            ADComputer computer = entry.Computer;
            SiteRecord record = entry.Record;
            ListViewItem item = new ListViewItem(entry.Name ?? string.Empty) { ToolTipText = entry.Note ?? string.Empty };
            item.SubItems.Add(ADCleanup.Name(entry.Finding));
            item.SubItems.Add(entry.Note ?? string.Empty);
            item.SubItems.Add(computer == null ? string.Empty : computer.Disabled ? "No" : "Yes");
            AddTime(item, computer?.LastSeen);
            AddTime(item, computer?.LastLogon);
            AddTime(item, computer?.PasswordSet);
            AddTime(item, computer?.Created);
            item.SubItems.Add(computer?.OperatingSystem ?? record?.OperatingSystem ?? string.Empty);
            item.SubItems.Add(computer?.DistinguishedName ?? string.Empty);
            ListViewItem.ListViewSubItem id = item.SubItems.Add(record == null ? string.Empty : record.ResourceId.ToString());
            if (record != null) id.Tag = record.ResourceId;
            item.SubItems.Add(record == null ? string.Empty : record.Client ? "Yes" : "No");
            AddTime(item, record?.LastContact);
            item.SubItems.Add(record?.FoundBy ?? string.Empty);
            return item;
        }

        private static void AddTime(ListViewItem item, DateTime? utc)
        {
            ListViewItem.ListViewSubItem cell = item.SubItems.Add(utc.HasValue ? utc.Value.ToLocalTime().ToString("g") + " (" + HealthCheck.FormatAge(HealthCheck.Age(utc.Value)) + ")" : string.Empty);
            if (utc.HasValue) cell.Tag = utc.Value;
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
            string name = title;
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
                // Names and paths come from AD and the site; CsvField keeps one starting with = from running as a formula.
                foreach (string[] row in TableRows(false)) csv.AppendLine(string.Join(",", row.Select(DeviceProgressDialog.CsvField)));
                // With a byte order mark, Excel reads the file as UTF-8 instead of guessing.
                File.WriteAllText(save.FileName, csv.ToString(), new UTF8Encoding(true));
                log.InfoFormat("{0}: exported {1} row(s) to {2}", title, list.Items.Count, save.FileName);
            }
        }

        // --- Fix --------------------------------------------------------------------------------------------------------

        /// <summary>The finding behind the active tile, or null on Computers.</summary>
        private CleanupFinding? ActiveFinding()
        {
            return activeTile != null && tileFindings.TryGetValue(activeTile, out CleanupFinding finding) ? finding : (CleanupFinding?)null;
        }

        private void QueueFixButton()
        {
            if (fixButtonPending || IsDisposed || !IsHandleCreated) return;
            fixButtonPending = true;
            OnUi(() =>
            {
                fixButtonPending = false;
                UpdateFixButton();
            });
        }

        /// <summary>
        /// The Fix button follows the active tile, as the dashboard's does: shown on a tile with fixes (Stale and
        /// Disabled), with how many of the selected computer objects a fix could run on, and off while the report reads
        /// or a fix runs. With none to run on it stays clickable, to say why: a disabled button shows no hover text.
        /// The selected rows only, not every row shown as on the dashboard: a delete here can't be taken back.
        /// </summary>
        private void UpdateFixButton()
        {
            CleanupFinding? finding = ActiveFinding();
            CleanupFix[] fixes = finding.HasValue ? ADCleanup.FixesFor(finding.Value) : new CleanupFix[0];
            bool show = fixes.Length > 0 && lastRead != null;
            if (fixButton.Visible != show) fixButton.Visible = show;
            if (!show) return;
            Dictionary<CleanupSkip, int> skipped = new Dictionary<CleanupSkip, int>();
            List<Row> targets = FixTargets(skipped);
            string leftOut = ADCleanup.LeftOut(skipped);
            string text = string.Format("Fix ({0:N0})...", targets.Count);
            string tip = running || fixing
                ? "Wait for the report to finish reading, or the fix to finish"
                : string.Format("{0}, on the computer objects selected in the grid (Ctrl+A selects all it shows). Each asks first{1}",
                                string.Join(", ", fixes.Select(ADCleanup.Name)), leftOut != null ? ". Left out: " + leftOut : string.Empty);
            bool enabled = !running && !fixing;
            if (fixButton.Text != text) fixButton.Text = text;
            if (fixButton.Enabled != enabled) fixButton.Enabled = enabled;
            if (tips.GetToolTip(fixButton) != tip) tips.SetToolTip(fixButton, tip);
        }

        /// <summary>The status line starts after the Fix button when it's there, and where it was before when it isn't.</summary>
        private void PlaceStatus()
        {
            int left = fixButton.Visible ? fixButton.Right + LogicalToDeviceUnits(6) : fixButton.Left + LogicalToDeviceUnits(6);
            status.SetBounds(left, status.Top, Math.Max(0, progress.Left - LogicalToDeviceUnits(12) - left), status.Height);
        }

        /// <summary>
        /// The computer objects a fix would run on: the ones selected in the grid, less those no fix touches (domain
        /// controllers, this computer, the site's servers), each counted in <paramref name="skipped"/> by why.
        /// </summary>
        private List<Row> FixTargets(Dictionary<CleanupSkip, int> skipped)
        {
            List<Row> targets = new List<Row>();
            if (lastRead == null || activeTile == null) return targets;
            foreach (ListViewItem item in list.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row?.Entry.Computer == null || !activeTile.Match(row)) continue;
                CleanupSkip? skip = ADCleanup.Guard(row.Entry.Computer, lastRead.DomainPath, lastRead.SiteSystems);
                if (skip.HasValue) Count(skipped, skip.Value);
                else targets.Add(row);
            }
            return targets;
        }

        private static void Count(Dictionary<CleanupSkip, int> counts, CleanupSkip skip) => counts[skip] = counts.TryGetValue(skip, out int count) ? count + 1 : 1;

        private void FixClicked()
        {
            CleanupFinding? finding = ActiveFinding();
            if (!finding.HasValue || lastRead == null || running || fixing) return;
            Dictionary<CleanupSkip, int> skipped = new Dictionary<CleanupSkip, int>();
            if (FixTargets(skipped).Count == 0)
            {
                string why = list.SelectedItems.Count == 0
                    ? "Select the computer objects to fix in the grid first. Ctrl+A selects all it shows."
                    : "None of the selected computer objects can take a fix: " + (ADCleanup.LeftOut(skipped) ?? "they have no computer object") + ".";
                MessageBox.Show(this, why, "Fix", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            fixMenu.Items.Clear();
            foreach (CleanupFix each in ADCleanup.FixesFor(finding.Value))
            {
                CleanupFix chosen = each;
                fixMenu.Items.Add(new ToolStripMenuItem(ADCleanup.Name(chosen) + "...", null, (s, e) => SafeAction.Run(title + ": " + ADCleanup.Name(chosen), () => StartFix(chosen))));
            }
            fixMenu.Show(fixButton, new Point(0, fixButton.Height));
        }

        /// <summary>What the second read found: the computer objects a fix goes ahead on, and why the rest are left out.</summary>
        private sealed class FixPlan
        {
            public readonly List<KeyValuePair<Row, ADObjectState>> Ready = new List<KeyValuePair<Row, ADObjectState>>();
            public readonly Dictionary<CleanupSkip, int> Skipped = new Dictionary<CleanupSkip, int>();
            public DeletedObjects Deleted;
        }

        /// <summary>What a fix did, computer object by computer object.</summary>
        private sealed class FixResult
        {
            public readonly List<string> Done = new List<string>();
            public readonly Dictionary<CleanupSkip, int> Skipped = new Dictionary<CleanupSkip, int>();
            public readonly List<string> SkippedNames = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public string Stopped;
        }

        /// <summary>
        /// A fix from start to end: for a move the OU first, then each computer object read again, the question (and
        /// the count typed), the changes one at a time, a summary, and the report read again. Nothing changes in AD
        /// before the question is answered Yes.
        /// </summary>
        private void StartFix(CleanupFix fix)
        {
            CleanupFinding? finding = ActiveFinding();
            if (!finding.HasValue || lastRead == null || running || fixing) return;
            CleanupRead read = lastRead;
            string tile = activeTile.Caption;
            string action = ADCleanup.Name(fix);
            Dictionary<CleanupSkip, int> skipped = new Dictionary<CleanupSkip, int>();
            List<Row> targets = FixTargets(skipped);
            if (targets.Count == 0) return;
            if (fix == CleanupFix.Delete && targets.Count > ADCleanup.DeleteCap)
            {
                MessageBox.Show(this, string.Format("Delete runs on {0} computer objects at most, and {1:N0} are selected.\n\nSelect fewer first.", ADCleanup.DeleteCap, targets.Count),
                                action, MessageBoxButtons.OK, MessageBoxIcon.Information);
                log.InfoFormat("{0}: more than {1}, not run", RunLine(tile, fix, targets.Select(row => row.Entry.Name).ToList(), null, ADCleanup.LeftOut(skipped)), ADCleanup.DeleteCap);
                return;
            }

            string target = null;
            if (fix == CleanupFix.Move)
            {
                target = ADCleanupOUDialog.Pick(this, read.Server, read.DomainPath, Layer8Settings.ADMoveTo);
                if (target == null) return;
                if (!ADCleanup.MayCreateComputers(read.Server, target))
                {
                    MessageBox.Show(this, string.Format("This account isn't allowed to create computer objects in {0}, which moving them there needs.\n\nNothing was moved.", target),
                                    action, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    log.InfoFormat("{0}: this account can't create computer objects there, not run", RunLine(tile, fix, targets.Select(row => row.Entry.Name).ToList(), target, ADCleanup.LeftOut(skipped)));
                    return;
                }
            }

            string before = status.Text;
            fixing = true;
            refresh.Enabled = false;
            progress.Visible = true;
            UpdateFixButton();
            status.Text = string.Format("Reading {0} again from {1}...", targets.Count == 1 ? targets[0].Entry.Name : targets.Count.ToString("N0") + " computer objects", read.Server);
            fixCancel?.Dispose();
            fixCancel = new CancellationTokenSource();
            CancellationToken token = fixCancel.Token;
            int notSeenDays = days;
            Task.Run(() => Prepare(fix, targets, skipped, target, read, notSeenDays, token), token).ContinueWith(task => OnUi(() =>
            {
                if (task.IsCanceled) return;
                if (task.IsFaulted)
                {
                    Exception root = SafeAction.Unwrap(task.Exception);
                    EndFix(before);
                    log.Warn(title + ": " + action + ": couldn't read the computer objects again", root);
                    MessageBox.Show(this, "Couldn't read the computer objects again, so nothing was changed: " + root.Message.Trim(), action, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Ask(fix, task.Result, read, tile, target, before, notSeenDays, token);
            }), TaskScheduler.Default);
        }

        /// <summary>Each selected computer object read again, on a background thread, and checked: still on the tile, still allowed.</summary>
        private static FixPlan Prepare(CleanupFix fix, List<Row> targets, Dictionary<CleanupSkip, int> skipped, string target, CleanupRead read, int notSeenDays, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            FixPlan plan = new FixPlan();
            foreach (KeyValuePair<CleanupSkip, int> pair in skipped) plan.Skipped[pair.Key] = pair.Value;
            foreach (Row row in targets)
            {
                token.ThrowIfCancellationRequested();
                ADObjectState state = ADCleanup.ReadState(read.Server, row.Entry.Computer.Sid, fix == CleanupFix.Delete);
                CleanupSkip? skip = ADCleanup.Check(fix, row.Entry, state, target, read.DomainPath, read.SiteSystems, notSeenDays, DateTime.UtcNow);
                if (skip.HasValue) Count(plan.Skipped, skip.Value);
                else plan.Ready.Add(new KeyValuePair<Row, ADObjectState>(row, state));
            }
            if (fix == CleanupFix.Delete) plan.Deleted = ADCleanup.ReadDeletedObjects(read.Server);
            if (log.IsDebugEnabled) log.DebugFormat("AD Cleanup Report: {0}: read {1} computer objects again in {2} ms", ADCleanup.Name(fix), targets.Count, (long)watch.Elapsed.TotalMilliseconds);
            return plan;
        }

        /// <summary>The question, and the count typed where it's needed; then the changes.</summary>
        private void Ask(CleanupFix fix, FixPlan plan, CleanupRead read, string tile, string target, string before, int notSeenDays, CancellationToken token)
        {
            string action = ADCleanup.Name(fix);
            string leftOut = ADCleanup.LeftOut(plan.Skipped);
            List<ADComputer> computers = plan.Ready.Select(pair => pair.Value.Computer).ToList();
            string line = RunLine(tile, fix, computers.Select(computer => computer.Name).ToList(), target, leftOut);
            if (computers.Count == 0)
            {
                EndFix(before);
                MessageBox.Show(this, string.Format("None of them can be {0}: {1}.", ADCleanup.Done(fix), leftOut), action, MessageBoxButtons.OK, MessageBoxIcon.Information);
                log.InfoFormat("{0}: none could take it, not run", line);
                return;
            }

            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string question = ADCleanup.Question(fix, tile, read.Domain, read.DomainPath, computers, leftOut, target, plan.Deleted, read.Server, user);
            MessageBoxIcon icon = fix == CleanupFix.Delete ? MessageBoxIcon.Warning : MessageBoxIcon.Question;
            if (MessageBox.Show(this, question, action, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                EndFix(before);
                log.InfoFormat("{0}: canceled at the question", line);
                return;
            }
            int count = computers.Count;
            if (ADCleanup.CountTyped(fix, count))
            {
                string these = count == 1 ? computers[0].Name : "these " + count.ToString("N0");
                string typed = InputPrompt.Ask(action, string.Format("To {0} {1}, type {2}.", ADCleanup.Verb(fix), these, count), null);
                if (typed == null || typed.Trim() != count.ToString())
                {
                    EndFix(before);
                    if (typed != null) MessageBox.Show(this, string.Format("That wasn't {0}: nothing was changed.", count), action, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    log.InfoFormat("{0}: the count wasn't typed, not run", line);
                    return;
                }
            }
            if (fix == CleanupFix.Move)
            {
                // Offered first next time; on the settings page too.
                try { Layer8Settings.ADMoveTo = target; }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException) { log.Warn("Couldn't save the OU to move to", ex); }
            }

            log.Info(line);
            Task.Run(() => Execute(fix, plan.Ready, target, read, notSeenDays, user, line, token), token).ContinueWith(task => OnUi(() =>
            {
                fixing = false;
                progress.Visible = false;
                refresh.Enabled = true;
                if (task.IsCanceled) return;
                if (task.IsFaulted)
                {
                    Exception root = SafeAction.Unwrap(task.Exception);
                    log.Warn(title + ": " + action + " stopped", root);
                    MessageBox.Show(this, action + " stopped: " + root.Message.Trim() + "\n\nLayer8's log lists each change it made.", action, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    ShowSummary(fix, count, task.Result);
                }
                Run();
            }), TaskScheduler.Default);
        }

        /// <summary>
        /// The changes, one computer object at a time on a background thread, each read once more first, since the
        /// question may have stayed open a while. Stops when the window closes, or when the domain controller stops
        /// answering (the rest would fail the same way). Every change, skip and failure goes to the log.
        /// </summary>
        private FixResult Execute(CleanupFix fix, List<KeyValuePair<Row, ADObjectState>> ready, string target, CleanupRead read, int notSeenDays, string user, string line, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            FixResult result = new FixResult();
            for (int i = 0; i < ready.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    result.Stopped = "the window was closed";
                    break;
                }
                Row row = ready[i].Key;
                string name = row.Entry.Name;
                string progressText = string.Format("{0}: {1:N0} of {2:N0}, {3}...", ADCleanup.Name(fix), i + 1, ready.Count, name);
                OnUi(() => status.Text = progressText);
                ADObjectState state;
                try
                {
                    state = ADCleanup.ReadState(read.Server, row.Entry.Computer.Sid, fix == CleanupFix.Delete);
                    CleanupSkip? skip = ADCleanup.Check(fix, row.Entry, state, target, read.DomainPath, read.SiteSystems, notSeenDays, DateTime.UtcNow);
                    if (skip.HasValue)
                    {
                        Count(result.Skipped, skip.Value);
                        result.SkippedNames.Add(name);
                        log.InfoFormat("{0}: left {1} alone: {2}", title, name, ADCleanup.SkipText(skip.Value, 1).Substring(2));
                        continue;
                    }
                    ADCleanup.Apply(fix, read.Server, state, target);
                }
                catch (Exception ex)
                {
                    string why = ADCleanup.Explain(ex, fix);
                    result.Failed.Add(name + ": " + why);
                    log.WarnFormat("{0}: couldn't {1} {2}: {3}", title, ADCleanup.Verb(fix), row.Entry.Computer.DistinguishedName, why);
                    if (log.IsDebugEnabled) log.Debug(title + ": " + ADCleanup.Name(fix) + " on " + name, ex);
                    if (ADCleanup.IsUnreachable(ex))
                    {
                        result.Stopped = read.Server + " didn't answer";
                        break;
                    }
                    continue;
                }
                result.Done.Add(name);
                log.Info(ChangeLine(fix, state, target, read.Server, user));
            }
            // Logged here, not in the window: it's written even when the window closed mid-run.
            log.InfoFormat("{0}: {1} {2}, {3} left alone, {4} failed{5}, in {6:0.0} s", line, result.Done.Count, ADCleanup.Done(fix),
                           result.SkippedNames.Count, result.Failed.Count, result.Stopped != null ? ", stopped: " + result.Stopped : string.Empty, watch.Elapsed.TotalSeconds);
            return result;
        }

        /// <summary>
        /// The log's line for one change: what, on which object (by distinguished name and SID), where to, on which
        /// domain controller, by whom. A moved object's line is the record of where it came from. The regression
        /// script reads these lines back and checks AD agrees.
        /// </summary>
        private string ChangeLine(CleanupFix fix, ADObjectState state, string target, string server, string user)
        {
            ADComputer computer = state.Computer;
            switch (fix)
            {
                case CleanupFix.Move:
                    return string.Format("{0}: moved {1} ({2}) to {3} on {4} by {5}", title, computer.DistinguishedName, computer.Sid, target, server, user);
                case CleanupFix.Delete:
                    return string.Format("{0}: deleted {1} ({2}), with {3} under it, on {4} by {5}", title, computer.DistinguishedName, computer.Sid,
                                         state.Children == 1 ? "1 object" : state.Children + " objects", server, user);
                default:
                    return string.Format("{0}: {1} {2} ({3}) on {4} by {5}", title, ADCleanup.Done(fix), computer.DistinguishedName, computer.Sid, server, user);
            }
        }

        /// <summary>A fix's line in the log, as the dashboard writes a tile's: "AD Cleanup Report: tile Stale: Disable on 3 computer objects (left out: ...)".</summary>
        private string RunLine(string tile, CleanupFix fix, IList<string> names, string target, string leftOut)
        {
            return string.Format("{0}: tile {1}: {2} on {3}{4}{5}", title, tile, ADCleanup.Name(fix),
                                 names.Count == 1 ? names[0] : names.Count.ToString("N0") + " computer objects",
                                 target != null ? " to " + target : string.Empty, leftOut != null ? " (left out: " + leftOut + ")" : string.Empty);
        }

        /// <summary>What happened, in one box: Ctrl+C copies a message box's text.</summary>
        private void ShowSummary(CleanupFix fix, int count, FixResult result)
        {
            string done = ADCleanup.Done(fix);
            List<string> paragraphs = new List<string> { string.Format("{0}{1} {2:N0} of {3:N0}.", char.ToUpperInvariant(done[0]), done.Substring(1), result.Done.Count, count) };
            if (result.SkippedNames.Count > 0)
            {
                paragraphs.Add(string.Format("Left alone: {0}: {1}.", ADCleanup.LeftOut(result.Skipped), ShortList(result.SkippedNames, ", ")));
            }
            if (result.Failed.Count > 0)
            {
                paragraphs.Add("Couldn't " + ADCleanup.Verb(fix) + ":\r\n" + ShortList(result.Failed, "\r\n"));
            }
            if (result.Stopped != null) paragraphs.Add("Stopped: " + result.Stopped + ". The rest weren't tried.");
            bool trouble = result.Failed.Count > 0 || result.Stopped != null;
            MessageBox.Show(this, string.Join("\r\n\r\n", paragraphs), ADCleanup.Name(fix), MessageBoxButtons.OK, trouble ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private static string ShortList(List<string> items, string separator)
        {
            string shown = string.Join(separator, items.Take(ADCleanup.NamesShown));
            return items.Count > ADCleanup.NamesShown ? string.Format("{0}{1}and {2:N0} more (Layer8's log has them all)", shown, separator, items.Count - ADCleanup.NamesShown) : shown;
        }

        /// <summary>A fix that stopped before changing anything: the window as it was.</summary>
        private void EndFix(string before)
        {
            fixing = false;
            progress.Visible = false;
            refresh.Enabled = true;
            status.Text = before;
            UpdateFixButton();
        }

        // The reads run on a background thread. Anything that touches the window goes back to its thread, and
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
    }
}
