using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// BitLocker Recovery Key: lists the recovery keys Active Directory holds for a device (the
    /// msFVE-RecoveryInformation objects under its computer object). Type the Key ID the recovery screen shows
    /// to find the right key; its first 8 characters are enough. Active Directory decides who may read the
    /// passwords, and the passwords are never written to Layer8's log.
    /// </summary>
    public static class BitLockerKeys
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(BitLockerKeys));

        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "BitLocker Recovery Key", () => ShowCore(selectedObject));
        }

        private static void ShowCore(IResultObject selectedObject)
        {
            string name = selectedObject["Name"].StringValue;
            if (!RemoteTools.IsComputerName(name))
            {
                MessageBox.Show(name + " isn't a computer name, so it has no BitLocker keys in Active Directory.", "BitLocker Recovery Key", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Cursor.Current = Cursors.WaitCursor;
            List<RecoveryKey> keys = FindKeys(name, out string computerPath);
            Cursor.Current = Cursors.Default;

            // Counts only: the passwords themselves never go to the log.
            log.InfoFormat("BitLocker keys for {0}: {1}", name, keys == null ? "computer not found in AD" : keys.Count + " found");

            using (KeyDialog dialog = new KeyDialog(name, computerPath, keys))
            {
                dialog.ShowDialog();
            }
        }

        internal sealed class RecoveryKey
        {
            public DateTime? Stored;
            public string KeyId;
            public string Password;
        }

        /// <summary>The device's recovery keys, newest first; null when the computer isn't in Active Directory.</summary>
        internal static List<RecoveryKey> FindKeys(string computerName, out string computerPath)
        {
            computerPath = null;
            using (DirectorySearcher computerSearch = new DirectorySearcher(string.Format("(&(objectCategory=computer)(cn={0}))", LdapEscape(computerName))))
            {
                computerSearch.PropertiesToLoad.Add("distinguishedName");
                SearchResult computer = computerSearch.FindOne();
                if (computer == null) return null;
                computerPath = computer.Properties["distinguishedName"].Count > 0 ? computer.Properties["distinguishedName"][0] as string : computer.Path;

                List<RecoveryKey> keys = new List<RecoveryKey>();
                using (DirectoryEntry computerEntry = computer.GetDirectoryEntry())
                using (DirectorySearcher keySearch = new DirectorySearcher(computerEntry, "(objectClass=msFVE-RecoveryInformation)",
                                                                          new[] { "whenCreated", "msFVE-RecoveryGuid", "msFVE-RecoveryPassword" }, SearchScope.OneLevel))
                using (SearchResultCollection results = keySearch.FindAll())
                {
                    foreach (SearchResult result in results)
                    {
                        keys.Add(new RecoveryKey
                        {
                            Stored = First(result, "whenCreated") is DateTime created ? created.ToLocalTime() : (DateTime?)null,
                            KeyId = First(result, "msFVE-RecoveryGuid") is byte[] guid && guid.Length == 16 ? new Guid(guid).ToString("D").ToUpperInvariant() : "(unknown)",
                            // Missing when the user may see that keys exist but not read them.
                            Password = First(result, "msFVE-RecoveryPassword") as string,
                        });
                    }
                }
                keys.Sort((a, b) => Nullable.Compare(b.Stored, a.Stored));
                return keys;
            }
        }

        private static object First(SearchResult result, string property)
        {
            return result.Properties.Contains(property) && result.Properties[property].Count > 0 ? result.Properties[property][0] : null;
        }

        /// <summary>Escapes a value for an LDAP filter (RFC 4515), so a name can't change the query.</summary>
        internal static string LdapEscape(string value)
        {
            StringBuilder escaped = new StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': escaped.Append(@"\5c"); break;
                    case '*': escaped.Append(@"\2a"); break;
                    case '(': escaped.Append(@"\28"); break;
                    case ')': escaped.Append(@"\29"); break;
                    case '\0': escaped.Append(@"\00"); break;
                    default: escaped.Append(c); break;
                }
            }
            return escaped.ToString();
        }

        /// <summary>Upper-case hex digits only, so "1a2b3c4d", "{1A2B3C4D-" and "1A2B-3C4D" all match the same key.</summary>
        internal static string HexOnly(string text)
        {
            StringBuilder hex = new StringBuilder();
            foreach (char c in text ?? string.Empty)
            {
                if (Uri.IsHexDigit(c)) hex.Append(char.ToUpperInvariant(c));
            }
            return hex.ToString();
        }

        /// <summary>The key list with a Key ID filter and a copy button, built in code (no designer file).</summary>
        private sealed class KeyDialog : Form
        {
            private readonly List<RecoveryKey> keys;
            private readonly ListView list;
            private readonly TextBox filter;
            private readonly Button copy;

            public KeyDialog(string computerName, string computerPath, List<RecoveryKey> keys)
            {
                this.keys = keys ?? new List<RecoveryKey>();

                SuspendLayout();
                AutoScaleDimensions = new SizeF(6F, 13F);
                AutoScaleMode = AutoScaleMode.Font;
                Text = "BitLocker Recovery Key: " + computerName;
                ClientSize = new Size(720, 312);
                MinimumSize = new Size(480, 260);
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false;

                Label info = new Label
                {
                    AutoSize = false,
                    Location = new Point(12, 10),
                    Size = new Size(696, 18),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Text = keys == null ? computerName + " isn't in Active Directory."
                         : keys.Count == 0 ? "Active Directory has no BitLocker recovery keys for " + computerName + "."
                         : string.Format("{0} recovery key{1} stored for {2}, newest first.", keys.Count, keys.Count == 1 ? "" : "s", computerPath),
                };
                Label filterLabel = new Label { AutoSize = true, Location = new Point(12, 40), Text = "Key ID from the recovery screen:" };
                filter = new TextBox { Location = new Point(190, 37), Size = new Size(220, 20) };
                filter.TextChanged += (s, e) => Fill();

                list = new ListView
                {
                    Location = new Point(12, 66),
                    Size = new Size(696, 204),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    View = View.Details,
                    FullRowSelect = true,
                    MultiSelect = false,
                    HideSelection = false,
                };
                list.Columns.Add("Stored", 130);
                list.Columns.Add("Key ID", 260);
                list.Columns.Add("Recovery password", 290);
                list.SelectedIndexChanged += (s, e) => copy.Enabled = SelectedPassword() != null;
                list.DoubleClick += (s, e) => CopySelected();

                copy = new Button { Text = "Copy Password", Location = new Point(522, 280), Size = new Size(105, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
                copy.Click += (s, e) => CopySelected();
                Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(633, 280), Size = new Size(75, 23), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

                Controls.AddRange(new Control[] { info, filterLabel, filter, list, copy, close });
                CancelButton = close;
                ResumeLayout(false);
                PerformLayout();

                Fill();
            }

            private void Fill()
            {
                string wanted = HexOnly(filter.Text);
                list.BeginUpdate();
                list.Items.Clear();
                foreach (RecoveryKey key in keys)
                {
                    if (wanted.Length > 0 && !HexOnly(key.KeyId).StartsWith(wanted, StringComparison.Ordinal)) continue;
                    ListViewItem item = new ListViewItem(key.Stored.HasValue ? key.Stored.Value.ToString("g") : "(unknown)") { Tag = key };
                    item.SubItems.Add(key.KeyId);
                    item.SubItems.Add(key.Password ?? "(you can't read this key)");
                    list.Items.Add(item);
                }
                // One match is the usual case when a Key ID is typed: select it, ready to copy.
                if (list.Items.Count == 1) list.Items[0].Selected = true;
                // The 48-digit password is the one thing that must never be cut off.
                if (list.Items.Count > 0) list.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.ColumnContent);
                list.EndUpdate();
                copy.Enabled = SelectedPassword() != null;
            }

            private string SelectedPassword()
            {
                return list.SelectedItems.Count == 1 ? (list.SelectedItems[0].Tag as RecoveryKey)?.Password : null;
            }

            private void CopySelected()
            {
                string password = SelectedPassword();
                if (password == null) return;
                Clipboard.SetDataObject(password, true, 10, 100);
            }
        }
    }
}
