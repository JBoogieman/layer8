using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Layer8.Device
{
    public partial class DeviceProgressDialog : SmsDialogBase
    {
        internal CancellationTokenSource CancellationTokenSource { get; set; } = new CancellationTokenSource();
        internal bool IsLoading { get { return listViewHosts.IsLoading; } set { listViewHosts.IsLoading = value; } }
        internal int Total {
            get { return clientActionProgressBar.Maximum; }
            set {
                clientActionProgressBar.Maximum = value;
                labelTotal.Text = value.ToString();
            }
        }

        // Device records: a query result (IResultObject) or a list built elsewhere, such as Retry Failed Devices.
        private readonly System.Collections.IEnumerable resultObjects;
        // What runs on each device, and its name for the log. Callers pass the values a run needs (a schedule ID,
        // a search) into this function, so a second action started while one is still running can't change them.
        private readonly Func<IResultObject, object> run;
        private readonly string actionName;
        // At most this many devices are contacted at once (Layer8 Settings, 32 unless changed). Without a limit,
        // a 5,000-device collection opened 5,000 connections together; this keeps the console and the network calm.
        private readonly SemaphoreSlim slots = new SemaphoreSlim(Layer8Settings.DevicesAtOnce);
        // The quick offline check (Layer8 Settings), read once per run. Online Status does its own, fuller check.
        private readonly bool checkReachable;
        private readonly int reachableMs = Layer8Settings.ReachableSeconds * 1000;

        private int completed;
        private int offline;
        private int failed;
        private int other;
        // Placeholder records (Unknown Computer, Provisioning Device): counted apart, so they aren't mistaken for offline devices.
        private int skipped;
        private Label labelSkipped;

        // Run Again on Failed: the rows that ended offline or in error, and what's needed to run them again.
        private readonly ActionDescription action;
        private readonly HashSet<ListViewItem> retryable = new HashSet<ListViewItem>();
        private Button runAgain;

        // One summary line in the log per run, for change tickets.
        private readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        private bool summaryLogged;
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(DeviceProgressDialog));

        // HRESULT for "The RPC server is unavailable", what WMI throws when nothing answers.
        private const int RpcServerUnavailable = unchecked((int)0x800706BA);

        // Set once every device has answered, or Stop was pressed: the button then closes the window.
        private bool runEnded;

        // Tools that answer in columns (Quick Status): the dialog sorts those itself, by each cell's real value.
        private bool hasDeviceColumns;
        private int sortColumn = -1;
        private SortOrder sortOrder = SortOrder.None;

        public DeviceProgressDialog()
        {
            InitializeComponent();

            // Esc does what the button says: Stop while devices are still being contacted, Close after. Setting
            // CancelButton gives the button DialogResult.Cancel, which would close the window on Stop too.
            CancelButton = buttonCancel;
            buttonCancel.DialogResult = DialogResult.None;

            IsLoading = true;
            listViewHosts.Items.Clear();

            clientActionProgressBar.Minimum = 0;
            clientActionProgressBar.Offline = 0;
            clientActionProgressBar.Complete = 0;
            clientActionProgressBar.Failed = 0;
            clientActionProgressBar.Style = ProgressBarStyle.Blocks;

            UpdateProgress();
        }

        /// <summary>For the console's selection (devices, or collection members).</summary>
        public DeviceProgressDialog(ActionDescription action, IResultObject selectedResultObjects, string actionName, Func<IResultObject, object> run, string title = null)
            : this(action, Each(selectedResultObjects), actionName, run, title)
        {
        }

        /// <summary>For a list of device records and a public static ClientActions method that takes one device.</summary>
        public DeviceProgressDialog(ActionDescription action, System.Collections.IEnumerable selectedResultObjects, MethodInfo method, string title = null)
            : this(action, selectedResultObjects, method.Name, device => method.Invoke(null, new object[] { device }), title)
        {
        }

        // IResultObject supports foreach but isn't an IEnumerable, so this adapts it.
        private static System.Collections.IEnumerable Each(IResultObject resultObjects)
        {
            foreach (IResultObject resultObject in resultObjects)
            {
                yield return resultObject;
            }
        }

        /// <summary>For a list of device records built elsewhere, such as Retry Failed Devices.</summary>
        public DeviceProgressDialog(ActionDescription action, System.Collections.IEnumerable selectedResultObjects, string actionName, Func<IResultObject, object> run, string title = null) : this()
        {
            resultObjects = selectedResultObjects;
            this.action = action;
            this.actionName = actionName;
            this.run = run;
            checkReachable = Layer8Settings.CheckReachable && actionName != nameof(ClientActions.OnlineStatus);

            Title = title ?? action.DisplayName;

            // Copy Results: every row as tab-separated text, so a run pastes straight into Excel.
            Button copyResults = new Button
            {
                Text = "Copy Results",
                Size = new System.Drawing.Size(90, buttonOK.Height),
                Location = new System.Drawing.Point(listViewHosts.Left, buttonOK.Top),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            copyResults.Click += (s, e) => CopyResults();
            Controls.Add(copyResults);

            // Export: the same rows as a CSV file, for attaching to a ticket or keeping a record of the run.
            Button exportResults = new Button
            {
                Text = "Export...",
                Size = new System.Drawing.Size(75, buttonOK.Height),
                Location = new System.Drawing.Point(copyResults.Right + 6, buttonOK.Top),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            exportResults.Click += (s, e) => ExportResults();
            Controls.Add(exportResults);

            // Run Again on Failed: a new run on just the devices that ended offline or in error.
            runAgain = new Button
            {
                Text = "Run Again on Failed",
                Size = new System.Drawing.Size(125, buttonOK.Height),
                Location = new System.Drawing.Point(exportResults.Right + 6, buttonOK.Top),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Enabled = false,
            };
            runAgain.Click += (s, e) => RunAgainOnFailed();
            Controls.Add(runAgain);

            labelSkipped = new Label
            {
                AutoSize = true,
                Anchor = labelOther.Anchor,
                Location = new System.Drawing.Point(labelOther.Left + LogicalToDeviceUnits(40), labelOther.Top),
                Visible = false,
            };
            Controls.Add(labelSkipped);
        }

        private void RunAgainOnFailed()
        {
            List<IResultObject> again;
            lock (retryable)
            {
                again = retryable.Select(row => (IResultObject)row.Tag).ToList();
            }
            if (again.Count == 0) return;
            log.InfoFormat("{0}: running again on {1} device(s) that failed", Title, again.Count);
            using (DeviceProgressDialog dialog = new DeviceProgressDialog(action, again, actionName, run, Title + " (again)"))
            {
                dialog.Total = again.Count;
                dialog.ShowDialog();
            }
        }

        private void ExportResults()
        {
            string name = Title;
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');
            using (SaveFileDialog save = new SaveFileDialog
            {
                Title = "Export Results",
                Filter = "CSV files (*.csv)|*.csv",
                FileName = string.Format("{0} {1:yyyy-MM-dd HHmm}.csv", name, DateTime.Now),
            })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;

                StringBuilder csv = new StringBuilder();
                foreach (string[] row in Rows())
                {
                    csv.AppendLine(string.Join(",", row.Select(CsvField)));
                }
                // With a byte order mark, Excel reads the file as UTF-8 instead of guessing.
                System.IO.File.WriteAllText(save.FileName, csv.ToString(), new UTF8Encoding(true));
                log.InfoFormat("{0}: exported {1} result row(s) to {2}", Title, listViewHosts.Items.Count, save.FileName);
            }
        }

        /// <summary>One CSV field: quoted when it holds a comma, quote or line break, with quotes doubled.</summary>
        internal static string CsvField(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// The header row, then one row per device with every column, in the order the list is sorted. Every
        /// device is included even while the search box filters the list.
        /// </summary>
        private IEnumerable<string[]> Rows()
        {
            int count = listViewHosts.Columns.Count;
            string[] header = new string[count];
            header[0] = "Device";
            for (int i = 1; i < count; i++) header[i] = listViewHosts.Columns[i].Text;
            yield return header;

            List<ListViewItem> hosts = listViewHosts.Items.Cast<ListViewItem>().ToList();
            if (listViewHosts.ListViewItemSorter is ListViewSorter sorter) hosts.Sort((a, b) => sorter.Compare(a, b));
            foreach (ListViewItem host in hosts)
            {
                string[] row = new string[count];
                for (int i = 0; i < count; i++) row[i] = i < host.SubItems.Count ? host.SubItems[i].Text : string.Empty;
                yield return row;
            }
        }

        private void CopyResults()
        {
            StringBuilder text = new StringBuilder();
            foreach (string[] row in Rows())
            {
                text.AppendLine(string.Join("\t", row));
            }
            Clipboard.SetDataObject(text.ToString(), true, 10, 100);
            log.InfoFormat("{0}: copied {1} result row(s)", Title, listViewHosts.Items.Count);
        }

        private void DeviceProgressDialog_Shown(object sender, EventArgs e)
        {
            stopwatch.Start();

            // Widen here, not in the constructor: the console's dialog base sizes the window while it loads, so
            // an earlier size doesn't stick. 600 (at 100% scaling) fits answers like Quick Status's; the controls
            // are anchored, so they stretch with it.
            int wanted = LogicalToDeviceUnits(600);
            if (ClientSize.Width < wanted)
            {
                int grow = wanted - ClientSize.Width;
                ClientSize = new System.Drawing.Size(wanted, ClientSize.Height);
                Left = Math.Max(0, Left - grow / 2);   // stay centred where the console put it
            }

            foreach (IResultObject resultObject in resultObjects)
            {
                // speed up the form by not searching for the item later, just include it into the run action
                ListViewItem item = new ListViewItem()
                {
                    Text = resultObject["Name"].StringValue,
                    SubItems = { "Queued" },
                    Tag = resultObject,   // for Run Again on Failed
                };

                listViewHosts.Items.Add(item);

                // Waits for a free slot without holding a thread, then runs on the thread pool.
                Task.Run(async () =>
                {
                    await slots.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        RunAction(resultObject, item);
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
            }

            listViewHosts.UpdateColumnWidth(columnHeaderStatus);
        }

        private void RunAction(IResultObject resultObject, ListViewItem item)
        {
            if (CancellationTokenSource.IsCancellationRequested)
            {
                SetStatus(item, "Canceled");
                return;
            }

            // Placeholder records (the Unknown Computer and Provisioning Device entries in All Systems) aren't
            // machines: answer at once instead of waiting for a connection that can't happen.
            if (!RemoteTools.IsComputerName(item.Text))
            {
                Interlocked.Increment(ref skipped);
                SetStatus(item, "Skipped: not a computer name");
                OnUiThread(UpdateProgress);
                return;
            }

            string status;
            try
            {
                if (checkReachable)
                {
                    SetStatus(item, "Checking it answers");
                    ClientActions.EnsureReachable(item.Text, reachableMs);
                }

                SetStatus(item, "Connecting");

                object result = run(resultObject);
                // Waits for async actions (their errors land in the catch below), and picks up the short
                // status some actions return, like "No missing updates".
                DeviceColumns columns = result as DeviceColumns;
                string outcome = columns != null ? columns.Status : ClientActions.WaitForResult(result);

                Interlocked.Increment(ref completed);
                status = string.IsNullOrEmpty(outcome) ? "Completed" : outcome;
                log.InfoFormat("{0} on {1}: {2}", actionName, item.Text, columns?.Summary ?? status);
                if (columns != null) OnUiThread(() => ShowColumns(item, columns));
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                if (root is DeviceOfflineException)
                {
                    // An expected answer (no reply, not in DNS), not a fault.
                    status = root.Message;
                    Interlocked.Increment(ref offline);
                }
                else if (root is ManagementException wmiError && ClientActions.IsNoClient(wmiError))
                {
                    // The ConfigMgr WMI namespace or class isn't there: no client, or a broken one.
                    status = "No ConfigMgr client found";
                    Interlocked.Increment(ref failed);
                }
                else if (root is ManagementException)
                {
                    status = "WMI error: " + root.Message;
                    Interlocked.Increment(ref failed);
                }
                else if (root is COMException rpc && rpc.ErrorCode == RpcServerUnavailable)
                {
                    // Nothing answered on WMI's ports: the device is off, or its firewall blocks WMI. Windows
                    // blocks ping by default, so a firewalled device can look just like an off one; one that
                    // answers anything at all is on.
                    if (ClientActions.AnswersAtAll(item.Text))
                    {
                        status = "Online, but WMI is blocked (RPC server unavailable): check the device's firewall";
                        Interlocked.Increment(ref failed);
                    }
                    else
                    {
                        status = "Offline, or its firewall blocks WMI (RPC server unavailable)";
                        Interlocked.Increment(ref offline);
                    }
                }
                else if (root is COMException)
                {
                    status = "Error: " + root.Message;
                    Interlocked.Increment(ref failed);
                }
                else
                {
                    status = "Error: " + root.Message;
                    Interlocked.Increment(ref failed);
                }
                Interlocked.Increment(ref other);
                lock (retryable) retryable.Add(item);
                log.Warn(string.Format("{0} on {1}: {2}", actionName, item.Text, status), root);
            }

            SetStatus(item, status);
            OnUiThread(UpdateProgress);
        }

        // The work runs on background threads. Anything that touches the window goes back to the
        // UI thread, and does nothing once the window has been closed.
        private void SetStatus(ListViewItem item, string text)
        {
            OnUiThread(() => item.SubItems[1].Text = text);
        }

        private void OnUiThread(Action update)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(update); else update();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Puts a device's columns in its row, adding a column the first time a device reports it. From then on
        /// the dialog sorts the list itself (the console's own sort compares text, so 9 GB would land after 10 GB).
        /// </summary>
        private void ShowColumns(ListViewItem item, DeviceColumns columns)
        {
            if (!hasDeviceColumns)
            {
                hasDeviceColumns = true;
                listViewHosts.AutoSort = false;
                listViewHosts.ColumnClick += (s, e) => SortBy(e.Column);
                // Room for the columns, as far as the screen allows, still centred where it was.
                int wanted = Math.Min(LogicalToDeviceUnits(940), Screen.FromControl(this).WorkingArea.Width - LogicalToDeviceUnits(40));
                if (ClientSize.Width < wanted)
                {
                    int grow = wanted - ClientSize.Width;
                    ClientSize = new System.Drawing.Size(wanted, ClientSize.Height);
                    Left = Math.Max(0, Left - grow / 2);
                }
            }

            foreach (DeviceColumns.Cell cell in columns.Cells)
            {
                int index = ColumnIndex(cell.Column);
                while (item.SubItems.Count <= index) item.SubItems.Add(string.Empty);
                item.SubItems[index].Text = cell.Text;
                item.SubItems[index].Tag = cell.SortKey;
            }
        }

        private int ColumnIndex(string name)
        {
            for (int i = 2; i < listViewHosts.Columns.Count; i++)
            {
                if (listViewHosts.Columns[i].Text == name) return i;
            }
            listViewHosts.Columns.Add(name, TextRenderer.MeasureText(name, listViewHosts.Font).Width + LogicalToDeviceUnits(24));
            return listViewHosts.Columns.Count - 1;
        }

        private void SortBy(int column)
        {
            sortOrder = column == sortColumn && sortOrder == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            // The arrow is drawn per column, so the last sorted column's has to be taken off.
            if (sortColumn >= 0 && sortColumn != column) listViewHosts.ShowArrow(sortColumn, SortOrder.None);
            sortColumn = column;
            listViewHosts.ListViewItemSorter = new ListViewSorter(column, sortOrder == SortOrder.Descending);
            listViewHosts.Sort();
            listViewHosts.ShowArrow(column, sortOrder);
        }

        internal void UpdateProgress()
        {
            // The bar has to reach its total, so skipped records ride with offline there; the labels keep them apart.
            clientActionProgressBar.UpdateValues(completed, offline + skipped, failed);
            labelCompleted.Text = completed.ToString();
            labelOther.Text = other.ToString();
            if (labelSkipped != null)
            {
                labelSkipped.Visible = skipped > 0;
                labelSkipped.Text = "Skipped: " + skipped;
            }

            if (clientActionProgressBar.Value == clientActionProgressBar.Maximum)
            {
                EndRun();
                LogSummary();

                if (hasDeviceColumns)
                {
                    FitDeviceColumns();
                    return;
                }

                // Fit the Status column to the longest answer, never narrower than the space it has. The console's
                // list wraps a real ListView, so a header's Width doesn't reach it; its own AutoResizeColumn does.
                // Header size on the last column fills the rest of the list.
                int longest = 0;
                foreach (ListViewItem host in listViewHosts.Items)
                {
                    longest = Math.Max(longest, TextRenderer.MeasureText(host.SubItems[1].Text, listViewHosts.Font).Width);
                }
                int available = listViewHosts.ClientSize.Width - columnHeaderName.Width - SystemInformation.VerticalScrollBarWidth;
                listViewHosts.AutoResizeColumn(columnHeaderStatus.Index,
                    longest + LogicalToDeviceUnits(16) > available ? ColumnHeaderAutoResizeStyle.ColumnContent : ColumnHeaderAutoResizeStyle.HeaderSize);
            }
        }

        /// <summary>
        /// Every column fits its longest text and its header. Status stops at a width that keeps the other columns
        /// in view: an offline device's reason can be long, and Copy Results and Export carry it in full.
        /// </summary>
        private void FitDeviceColumns()
        {
            int statusLimit = LogicalToDeviceUnits(240);
            for (int i = 1; i < listViewHosts.Columns.Count; i++)
            {
                int header = TextRenderer.MeasureText(listViewHosts.Columns[i].Text, listViewHosts.Font).Width;
                int widest = 0;
                foreach (ListViewItem host in listViewHosts.Items)
                {
                    if (i < host.SubItems.Count) widest = Math.Max(widest, TextRenderer.MeasureText(host.SubItems[i].Text, listViewHosts.Font).Width);
                }
                // The list's own AutoResizeColumn is what reaches the real list (see the Status column below).
                listViewHosts.AutoResizeColumn(i, widest > header ? ColumnHeaderAutoResizeStyle.ColumnContent : ColumnHeaderAutoResizeStyle.HeaderSize);
                if (i == columnHeaderStatus.Index && listViewHosts.Columns[i].Width > statusLimit) listViewHosts.Columns[i].Width = statusLimit;
            }
        }

        private void LogSummary()
        {
            if (summaryLogged || Total == 0) return;
            summaryLogged = true;
            stopwatch.Stop();
            log.InfoFormat("{0} finished: {1} devices, {2} completed, {3} offline, {4} failed, {5} skipped, in {6:mm\\:ss}",
                Title, Total, completed, offline, failed, skipped, stopwatch.Elapsed);
        }

        private void EnableRunAgain()
        {
            if (runAgain == null) return;
            lock (retryable) runAgain.Enabled = retryable.Count > 0;
        }

        private void EndRun()
        {
            runEnded = true;
            IsLoading = false;
            buttonOK.Enabled = true;
            buttonCancel.Text = "Close";
            EnableRunAgain();
        }

        private void ButtonCancel_Click(object sender, EventArgs e)
        {
            if (runEnded)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            // Stop: devices still waiting for their turn are marked Canceled. Ones already being contacted
            // finish and show their answer, so what was done stays on screen to copy or export.
            log.InfoFormat("{0}: stopped", Title);
            CancellationTokenSource.Cancel();
            EndRun();
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DeviceProgressDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            CancellationTokenSource.Cancel();
        }
    }
}

