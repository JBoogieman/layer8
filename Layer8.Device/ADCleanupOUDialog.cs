using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Where the AD Cleanup Report's Move to OU puts computer objects: every OU in the domain and its computers
    /// container, read from the report's domain controller, with a filter box. Picking one changes nothing; the move
    /// asks its own question after.
    /// </summary>
    internal sealed class ADCleanupOUDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ADCleanupOUDialog));

        private readonly string server;
        private readonly string domainPath;
        private readonly string initial;
        private readonly TextBox filter;
        private readonly ListView list;
        private readonly Label status;
        private readonly Button ok;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private List<ADContainer> containers = new List<ADContainer>();

        /// <summary>The OU picked, or null when the window was canceled.</summary>
        public string Picked { get; private set; }

        /// <summary>Asks where to move to, with <paramref name="initial"/> picked to start with when it's still there.</summary>
        public static string Pick(IWin32Window owner, string server, string domainPath, string initial)
        {
            using (ADCleanupOUDialog dialog = new ADCleanupOUDialog(server, domainPath, initial))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Picked : null;
            }
        }

        private ADCleanupOUDialog(string server, string domainPath, string initial)
        {
            this.server = server;
            this.domainPath = domainPath;
            this.initial = initial;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = "Move to OU";
            ClientSize = new Size(620, 420);
            MinimumSize = new Size(420, 300);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;

            Label question = new Label { Text = "Move the computer objects to:", Location = new Point(12, 12), AutoSize = true };
            Label filterLabel = new Label { Text = "Filter:", Location = new Point(12, 39), AutoSize = true };
            filter = new TextBox { Location = new Point(52, 36), Size = new Size(260, 20) };
            list = new ListView
            {
                Location = new Point(12, 64),
                Size = new Size(596, 306),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                ShowItemToolTips = true,
            };
            list.Columns.Add("OU", LogicalToDeviceUnits(220));
            list.Columns.Add("Distinguished name", LogicalToDeviceUnits(350));
            status = new Label
            {
                Location = new Point(12, 386),
                Size = new Size(430, 23),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Reading the OUs from " + server + "...",
            };
            ok = new Button { Text = "OK", Location = new Point(452, 386), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
            Button cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(533, 386), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Controls.AddRange(new Control[] { question, filterLabel, filter, list, status, ok, cancelButton });
            AcceptButton = ok;
            CancelButton = cancelButton;

            filter.TextChanged += (s, e) => Fill();
            list.SelectedIndexChanged += (s, e) => ok.Enabled = list.SelectedItems.Count == 1;
            list.DoubleClick += (s, e) => Accept();
            ok.Click += (s, e) => Accept();
            Shown += (s, e) => ReadContainers();
            FormClosing += (s, e) => cancel.Cancel();
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cancel.Cancel();
                cancel.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ReadContainers()
        {
            CancellationToken token = cancel.Token;
            Task.Run(() => ADCleanup.ReadContainers(server, domainPath, token), token).ContinueWith(task =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (task.IsCanceled) return;
                        if (task.IsFaulted)
                        {
                            Exception root = SafeAction.Unwrap(task.Exception);
                            log.Warn("Move to OU: couldn't read the OUs", root);
                            status.Text = "Couldn't read the OUs: " + root.Message.Trim();
                            return;
                        }
                        containers = task.Result;
                        Fill();
                        status.Text = string.Format("{0:N0} OUs and containers from {1}. The Domain Controllers OU isn't offered.", containers.Count, server);
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
        }

        /// <summary>The OUs the filter lets through, keeping the one picked (or the last one used, to start with).</summary>
        private void Fill()
        {
            string keep = list.SelectedItems.Count == 1 ? (string)list.SelectedItems[0].Tag : initial;
            string text = filter.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ADContainer container in containers.Where(each => text.Length == 0 ||
                     each.Path.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || each.DistinguishedName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ListViewItem item = new ListViewItem(new[] { container.Path, container.DistinguishedName }) { Tag = container.DistinguishedName, ToolTipText = container.DistinguishedName };
                list.Items.Add(item);
                if (keep != null && string.Equals(keep, container.DistinguishedName, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }
            list.EndUpdate();
            ok.Enabled = list.SelectedItems.Count == 1;
        }

        private void Accept()
        {
            if (list.SelectedItems.Count != 1) return;
            Picked = (string)list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
