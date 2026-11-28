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
    /// computers, and the grid below sorts, filters, copies and exports. Nothing here changes anything.
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

        private CancellationTokenSource cancel;
        private bool running;
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
            bottom.Controls.AddRange(new Control[] { copy, export, status, progress, refresh, close });

            filter.TextChanged += (s, e) => ApplyFilter();
            workstationsOnly.CheckedChanged += (s, e) => { CountTiles(); ApplyFilter(); };
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
            FormClosing += (s, e) => cancel?.Cancel();

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
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        private void AddTile(CleanupFinding finding, Tone tone, string tip)
        {
            AddTile(ADCleanup.Name(finding), tip, tone, row => row.Entry.Finding == finding);
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
        }

        /// <summary>Reads AD and the site on a background thread, then fills the tiles and the grid.</summary>
        private void Run()
        {
            if (running) return;
            running = true;
            refresh.Enabled = false;
            progress.Visible = true;
            status.Text = "Reading Active Directory and the site...";
            cancel?.Dispose();
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            Stopwatch watch = Stopwatch.StartNew();
            int notSeenDays = days;
            Task.Run(() =>
            {
                CleanupRead read = ADCleanup.Read(connection, token);
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
                    return;
                }
                ShowResults(task.Result.Key, task.Result.Value, watch.Elapsed);
            }), TaskScheduler.Default);
        }

        private void ShowResults(CleanupRead read, List<CleanupEntry> entries, TimeSpan took)
        {
            rows = entries.Select(entry => new Row(entry, Item(entry))).ToList();
            CountTiles();
            ApplyFilter();
            string elapsed = took.ToString(took.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            status.Text = string.Format("{0:N0} computer objects in {1} and {2:N0} device records on the site, read in {3}. Not seen in {4} counts (Layer8 Settings).{5}",
                read.Computers.Count, read.Domain, read.Records.Count, elapsed, ADCleanup.Days(days),
                read.Obsolete > 0 ? string.Format(" {0:N0} obsolete records left out.", read.Obsolete) : string.Empty);
            tips.SetToolTip(status, status.Text + "\n\nSearched " + read.DomainPath);
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
