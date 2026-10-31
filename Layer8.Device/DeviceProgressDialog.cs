using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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

        private readonly IResultObject resultObjects;
        private readonly MethodInfo method;
        private int completed;
        private int offline;
        private int failed;
        private int other;
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(DeviceProgressDialog));

        public DeviceProgressDialog()
        {
            InitializeComponent();

            IsLoading = true;
            listViewHosts.Items.Clear();

            clientActionProgressBar.Minimum = 0;
            clientActionProgressBar.Offline = 0;
            clientActionProgressBar.Complete = 0;
            clientActionProgressBar.Failed = 0;
            clientActionProgressBar.Style = ProgressBarStyle.Blocks;

            UpdateProgress();

            Updater.CheckUpdates();
        }

        public DeviceProgressDialog(ActionDescription action, IResultObject selectedResultObjects, MethodInfo method) : this()
        {
            resultObjects = selectedResultObjects;
            this.method = method;

            Title = action.DisplayName;
        }

        private void DeviceProgressDialog_Shown(object sender, EventArgs e)
        {
            foreach (IResultObject resultObject in resultObjects)
            {
                // speed up the form by not searching for the item later, just include it into the run action
                ListViewItem item = new ListViewItem()
                {
                    Text = resultObject["Name"].StringValue,
                    SubItems = { "Queued" }
                };

                listViewHosts.Items.Add(item);

                ThreadPool.QueueUserWorkItem(arg => { RunAction(resultObject, item); });
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

            string status;
            try
            {
                SetStatus(item, "Connecting");

                object result = method.Invoke(null, new object[] { resultObject });
                // Waits for async actions (their errors land in the catch below), and picks up the short
                // status some actions return, like "No missing updates".
                string outcome = ClientActions.WaitForResult(result);

                Interlocked.Increment(ref completed);
                status = string.IsNullOrEmpty(outcome) ? "Completed" : outcome;
                log.InfoFormat("{0} on {1}: {2}", method.Name, item.Text, status);
            }
            catch (Exception ex)
            {
                Exception root = SafeAction.Unwrap(ex);
                if (root is ManagementException wmiError && wmiError.ErrorCode == ManagementStatus.InvalidNamespace)
                {
                    // The ConfigMgr WMI namespace (root\ccm) isn't there: no client, or a broken one.
                    status = "No ConfigMgr client found";
                    Interlocked.Increment(ref failed);
                }
                else if (root is ManagementException)
                {
                    status = "WMI error: " + root.Message;
                    Interlocked.Increment(ref failed);
                }
                else if (root is COMException)
                {
                    status = "Offline";
                    Interlocked.Increment(ref offline);
                }
                else
                {
                    status = "Error: " + root.Message;
                    Interlocked.Increment(ref failed);
                }
                Interlocked.Increment(ref other);
                log.Warn(string.Format("{0} on {1}: {2}", method.Name, item.Text, status), root);
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

        internal void UpdateProgress()
        {
            clientActionProgressBar.UpdateValues(completed, offline, failed);
            labelCompleted.Text = completed.ToString();
            labelOther.Text = other.ToString();

            if (clientActionProgressBar.Value == clientActionProgressBar.Maximum)
            {
                IsLoading = false;
                buttonOK.Enabled = true;
            }
        }

        private void ButtonCancel_Click(object sender, EventArgs e)
        {
            CancellationTokenSource.Cancel();
            IsLoading = false;
            buttonOK.Enabled = true;
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

