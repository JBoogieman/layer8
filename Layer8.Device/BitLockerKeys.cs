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
            List<RecoveryKey> keys = FindKeys(name, out string computerPath, out string problem);
            Cursor.Current = Cursors.Default;
            if (problem != null)
            {
                log.Warn("BitLocker keys for " + name + ": " + problem);
                MessageBox.Show(problem, "BitLocker Recovery Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

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

        /// <summary>
        /// The device's recovery keys, newest first; null when the computer isn't in Active Directory, or when
        /// <paramref name="problem"/> says why Layer8 won't pick one.
        /// </summary>
        internal static List<RecoveryKey> FindKeys(string computerName, out string computerPath, out string problem)
        {
            computerPath = null;
            using (DirectorySearcher computerSearch = new DirectorySearcher())
            {
                computerSearch.PropertiesToLoad.Add("distinguishedName");
                SearchResult computer = FindComputer(computerSearch, computerName, out problem);
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

        /// <summary>
        /// The computer object for a device name: the one whose account name (sAMAccountName, the name and $) is
        /// the name, which is unique in a domain, or failing that the one named that (cn). A cn is only unique in
        /// its OU, so two computers in different OUs can share one; then Layer8 says so rather than pick one, which
        /// would show another computer's keys or password. Null when there's none, or with
        /// <paramref name="problem"/> set when there's more than one.
        /// </summary>
        internal static SearchResult FindComputer(DirectorySearcher search, string computerName, out string problem)
        {
            problem = null;
            string name = LdapEscape(computerName);
            search.Filter = string.Format("(&(objectCategory=computer)(objectClass=computer)(|(sAMAccountName={0}$)(cn={0})))", name);
            if (!search.PropertiesToLoad.Contains("distinguishedName")) search.PropertiesToLoad.Add("distinguishedName");
            if (!search.PropertiesToLoad.Contains("sAMAccountName")) search.PropertiesToLoad.Add("sAMAccountName");

            List<SearchResult> all = new List<SearchResult>();
            using (SearchResultCollection results = search.FindAll())
            {
                foreach (SearchResult result in results) all.Add(result);
            }
            List<int> matches = Matching(all.ConvertAll(result => First(result, "sAMAccountName") as string), computerName);
            if (matches.Count <= 1) return matches.Count == 1 ? all[matches[0]] : null;

            List<string> names = matches.ConvertAll(index => First(all[index], "distinguishedName") as string ?? all[index].Path);
            problem = string.Format("{0} computer objects in Active Directory match {1}: {2}. Layer8 won't guess which one is this device.",
                                    matches.Count, computerName, string.Join("; ", names));
            return null;
        }

        /// <summary>
        /// Which of the objects found (by their account names) are the device: the ones whose account name is the
        /// name and $, or all of them when none is. More than one means Layer8 can't tell. Internal so it can be tested.
        /// </summary>
        internal static List<int> Matching(IList<string> accountNames, string computerName)
        {
            List<int> byAccount = new List<int>();
            for (int i = 0; i < accountNames.Count; i++)
            {
                if (string.Equals(accountNames[i], computerName + "$", StringComparison.OrdinalIgnoreCase)) byAccount.Add(i);
            }
            if (byAccount.Count > 0) return byAccount;
            List<int> all = new List<int>();
            for (int i = 0; i < accountNames.Count; i++) all.Add(i);
            return all;
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
            private readonly CheckBox show;
            private readonly Label info;
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

                info = new Label
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
                // Hidden until asked for, as LAPS Password does: the dialog used to open with every recovery password on screen.
                show = new CheckBox { Text = "Show passwords", Location = new Point(422, 39), AutoSize = true };
                show.CheckedChanged += (s, e) => Fill();

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

                Controls.AddRange(new Control[] { info, filterLabel, filter, show, list, copy, close });
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
                    item.SubItems.Add(key.Password == null ? "(you can't read this key)" : show.Checked ? key.Password : Mask(key.Password));
                    list.Items.Add(item);
                }
                // One match is the usual case when a Key ID is typed: select it, ready to copy.
                if (list.Items.Count == 1) list.Items[0].Selected = true;
                // The 48-digit password is the one thing that must never be cut off.
                if (list.Items.Count > 0) list.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.ColumnContent);
                list.EndUpdate();
                copy.Enabled = SelectedPassword() != null;
            }

            /// <summary>The password with every digit as a dot, so its shape (eight groups of six) still shows.</summary>
            internal static string Mask(string password)
            {
                StringBuilder masked = new StringBuilder(password.Length);
                foreach (char c in password) masked.Append(char.IsDigit(c) ? '\u25CF' : c);
                return masked.ToString();
            }

            private string SelectedPassword()
            {
                return list.SelectedItems.Count == 1 ? (list.SelectedItems[0].Tag as RecoveryKey)?.Password : null;
            }

            private void CopySelected()
            {
                string password = SelectedPassword();
                if (password == null) return;
                // Kept out of clipboard history and the cloud clipboard, and cleared after a while.
                Utility.CopySecret(password);
                info.Text = string.Format("Copied. Layer8 clears it from the clipboard in {0} seconds, and it stays out of clipboard history.", Utility.SecretClipboardSeconds);
            }
        }
    }
}
