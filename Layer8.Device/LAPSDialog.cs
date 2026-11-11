using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// LAPS Password on a device's Layer8 menu: the device's local administrator password from Active Directory.
    /// Reads Windows LAPS first (encrypted or not), then legacy Microsoft LAPS, so it works during a migration
    /// from one to the other. Expire Now asks the device to change the password at its next LAPS check.
    /// The password itself is never logged.
    /// </summary>
    public sealed class LAPSDialog : Form
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(LAPSDialog));

        private readonly string computer;
        private readonly Label account, changed, expires, source, message;
        private readonly TextBox password;
        private readonly CheckBox show;
        private readonly Button copy, expireNow;
        private LapsPassword found;

        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "LAPS Password", () =>
            {
                using (LAPSDialog dialog = new LAPSDialog(selectedObject["Name"].StringValue))
                {
                    dialog.ShowDialog();
                }
            });
        }

        private LAPSDialog(string computer)
        {
            this.computer = computer;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Text = "LAPS Password: " + computer;
            ClientSize = new Size(460, 238);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            int y = 14;
            account = AddRow("Account:", ref y);
            Controls.Add(new Label { Text = "Password:", Location = new Point(12, y + 3), AutoSize = true });
            password = new TextBox { Location = new Point(100, y), Size = new Size(286, 20), ReadOnly = true, UseSystemPasswordChar = true, Font = new Font(FontFamily.GenericMonospace, 9.75F) };
            show = new CheckBox { Text = "Show", Location = new Point(394, y + 2), AutoSize = true };
            Controls.Add(password);
            Controls.Add(show);
            y += 28;
            changed = AddRow("Changed:", ref y);
            expires = AddRow("Expires:", ref y);
            source = AddRow("Stored as:", ref y);
            message = new Label { Location = new Point(12, y + 2), Size = new Size(436, 44) };
            Controls.Add(message);

            copy = new Button { Text = "Copy", Location = new Point(196, 203), Size = new Size(75, 23), Enabled = false };
            expireNow = new Button { Text = "Expire Now...", Location = new Point(277, 203), Size = new Size(90, 23), Enabled = false };
            Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(373, 203), Size = new Size(75, 23) };
            Controls.AddRange(new Control[] { copy, expireNow, close });
            CancelButton = close;

            show.CheckedChanged += (s, e) => password.UseSystemPasswordChar = !show.Checked;
            copy.Click += (s, e) => { if (!string.IsNullOrEmpty(found?.Password)) Clipboard.SetText(found.Password); };
            expireNow.Click += (s, e) => ExpireNow();
            Shown += (s, e) => Lookup();
            ResumeLayout(false);
        }

        private Label AddRow(string caption, ref int y)
        {
            Controls.Add(new Label { Text = caption, Location = new Point(12, y + 3), AutoSize = true });
            Label value = new Label { Location = new Point(100, y + 3), Size = new Size(348, 16), AutoEllipsis = true };
            Controls.Add(value);
            y += 28;
            return value;
        }

        private void Lookup()
        {
            if (!RemoteTools.IsComputerName(computer))
            {
                message.Text = computer + " isn't a computer name.";
                return;
            }

            UseWaitCursor = true;
            try
            {
                found = LapsReader.Read(computer);
                account.Text = found.Account;
                password.Text = found.Password;
                changed.Text = found.Changed?.ToString("g") ?? string.Empty;
                bool expired = found.Expires.HasValue && found.Expires.Value < DateTime.Now;
                expires.Text = found.Expires.HasValue ? found.Expires.Value.ToString("g") + (expired ? " (expired)" : string.Empty) : string.Empty;
                source.Text = found.Source;
                message.Text = found.Message;
                if (expired) message.Text = (string.IsNullOrEmpty(message.Text) ? string.Empty : message.Text + "\r\n") + computer + " changes the password at its next LAPS check: within the hour, or right after a gpupdate on it.";
                copy.Enabled = !string.IsNullOrEmpty(found.Password);
                expireNow.Enabled = found.ExpirationAttribute != null;
                log.InfoFormat("LAPS Password for {0}: {1}", computer, found.Source ?? found.Message);
            }
            catch (Exception ex)
            {
                message.Text = "Couldn't read Active Directory: " + ex.GetBaseException().Message;
                log.Error("LAPS Password for " + computer, ex);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        /// <summary>
        /// Sets the expiration time to now, the same as Set-LapsADPasswordExpirationTime. The device changes its
        /// password at its next LAPS check: within the hour, or straight away after gpupdate.
        /// </summary>
        private void ExpireNow()
        {
            string question = string.Format("Make {0} change its local administrator password at its next LAPS check (within the hour)?\r\n\r\nThe current password keeps working until it does.", computer);
            if (MessageBox.Show(this, question, "Expire Now", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            try
            {
                using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + found.DistinguishedName))
                {
                    entry.Properties[found.ExpirationAttribute].Value = DateTime.UtcNow.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
                    entry.CommitChanges();
                }
                log.InfoFormat("LAPS Password for {0}: expired by {1}", computer, Environment.UserName);
                Lookup();
            }
            catch (Exception ex)
            {
                string reason = ex is UnauthorizedAccessException || (ex is COMException com && com.ErrorCode == unchecked((int)0x80070005))
                    ? "this account isn't allowed to change " + found.ExpirationAttribute + " on " + computer
                    : ex.GetBaseException().Message;
                MessageBox.Show(this, "Couldn't expire the password: " + reason, "Expire Now", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                log.Warn("LAPS Expire Now for " + computer + " failed: " + ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>What Active Directory holds for one device's LAPS password.</summary>
    internal sealed class LapsPassword
    {
        public string DistinguishedName, Account, Password, Source, Message;
        /// <summary>The attribute Expire Now writes, or null when there's no password to expire.</summary>
        public string ExpirationAttribute;
        public DateTime? Changed, Expires;
    }

    internal static class LapsReader
    {
        // Windows LAPS (2023 onwards) and legacy Microsoft LAPS attributes.
        private const string WindowsEncrypted = "msLAPS-EncryptedPassword";
        private const string WindowsClear = "msLAPS-Password";
        private const string WindowsExpiration = "msLAPS-PasswordExpirationTime";
        private const string WindowsHistory = "msLAPS-EncryptedPasswordHistory";
        private const string LegacyPassword = "ms-Mcs-AdmPwd";
        private const string LegacyExpiration = "ms-Mcs-AdmPwdExpirationTime";

        public static LapsPassword Read(string computer)
        {
            using (DirectorySearcher search = new DirectorySearcher
            {
                // Records such as "x64 Unknown Computer (x64 Unknown Computer)" never get here; real names are
                // escaped anyway, the same way BitLocker lookups are.
                Filter = string.Format("(&(objectCategory=computer)(objectClass=computer)(cn={0}))", BitLockerKeys.LdapEscape(computer)),
            })
            {
                foreach (string attribute in new[] { "distinguishedName", WindowsEncrypted, WindowsClear, WindowsExpiration, WindowsHistory, LegacyPassword, LegacyExpiration })
                {
                    search.PropertiesToLoad.Add(attribute);
                }

                SearchResult result = search.FindOne();
                if (result == null) return new LapsPassword { Message = computer + " isn't in Active Directory (searched " + DomainName() + ")." };

                string distinguishedName = First(result, "distinguishedName") as string;
                LapsPassword encryptedLaps = null, clearLaps = null;
                if (First(result, WindowsEncrypted) is byte[] encrypted)
                {
                    encryptedLaps = new LapsPassword { Source = "Windows LAPS, encrypted" };
                    int history = result.Properties[WindowsHistory]?.Count ?? 0;
                    if (history > 0) encryptedLaps.Source += string.Format(" ({0} older password{1} kept)", history, history == 1 ? string.Empty : "s");
                    Decrypt(encrypted, encryptedLaps);
                }
                if (First(result, WindowsClear) is string clear)
                {
                    clearLaps = new LapsPassword { Source = "Windows LAPS, not encrypted" };
                    FromJson(clear, clearLaps);
                }

                // Both can be there once a policy turns encryption on or off: the one written last is the password in use.
                LapsPassword windowsLaps = encryptedLaps == null ? clearLaps
                    : clearLaps == null ? encryptedLaps
                    : clearLaps.Changed > encryptedLaps.Changed ? clearLaps : encryptedLaps;
                LapsPassword laps = windowsLaps ?? new LapsPassword();
                laps.DistinguishedName = distinguishedName;
                if (windowsLaps != null)
                {
                    laps.ExpirationAttribute = WindowsExpiration;
                    laps.Expires = FileTime(First(result, WindowsExpiration));
                }
                else if (First(result, LegacyPassword) is string legacy)
                {
                    laps.Source = "Legacy Microsoft LAPS";
                    laps.ExpirationAttribute = LegacyExpiration;
                    laps.Expires = FileTime(First(result, LegacyExpiration));
                    laps.Password = legacy;
                    laps.Message = "Legacy LAPS doesn't record the account name or when the password changed.";
                }
                else
                {
                    laps.Message = "No LAPS password in Active Directory. Either LAPS doesn't manage this device, this account can't read the password, " +
                        "or the device backs it up to Microsoft Entra ID (look in the Intune or Entra admin center).";
                }
                return laps;
            }
        }

        /// <summary>
        /// msLAPS-EncryptedPassword: 16 bytes of header (when it was encrypted, as a FILETIME split high then low,
        /// the encrypted length, and reserved flags), then the password encrypted with DPAPI-NG for the group the
        /// LAPS policy names as decryptors. Windows decrypts it for anyone in that group, as Get-LapsADPassword does.
        /// </summary>
        private static void Decrypt(byte[] blob, LapsPassword laps)
        {
            if (blob.Length < 16)
            {
                laps.Message = "The encrypted password in Active Directory is too short to read.";
                return;
            }
            long stamp = ((long)BitConverter.ToUInt32(blob, 0) << 32) | BitConverter.ToUInt32(blob, 4);
            laps.Changed = FileTime(stamp);
            int length = (int)Math.Min(BitConverter.ToUInt32(blob, 8), (uint)(blob.Length - 16));
            byte[] secret = new byte[length];
            Buffer.BlockCopy(blob, 16, secret, 0, length);

            int status = NativeMethods.NCryptUnprotectSecret(out IntPtr descriptor, NativeMethods.NCRYPT_SILENT_FLAG, secret, (uint)secret.Length, IntPtr.Zero, IntPtr.Zero, out IntPtr data, out uint size);
            try
            {
                if (status != 0)
                {
                    laps.Message = string.Format("Windows LAPS encrypted this password, and this account can't decrypt it (0x{0:X8}). " +
                        "Only members of the group the LAPS policy names as authorized decryptors can: Domain Admins unless the policy says otherwise.", status);
                    return;
                }
                byte[] plain = new byte[size];
                Marshal.Copy(data, plain, 0, plain.Length);
                // Don't leave the password lying in freed memory.
                Marshal.Copy(new byte[size], 0, data, (int)size);
                FromJson(Encoding.Unicode.GetString(plain).TrimEnd('\0'), laps);
            }
            finally
            {
                if (data != IntPtr.Zero) NativeMethods.LocalFree(data);
                if (descriptor != IntPtr.Zero) NativeMethods.NCryptCloseProtectionDescriptor(descriptor);
            }
        }

        /// <summary>Windows LAPS stores {"n":"account","t":"FILETIME in hex","p":"password"}.</summary>
        private static void FromJson(string json, LapsPassword laps)
        {
            Dictionary<string, object> values;
            try
            {
                values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                // The message never includes the text itself: it's the password.
                laps.Message = "The password in Active Directory isn't in the format Windows LAPS writes, so Layer8 can't read it.";
                return;
            }
            if (values == null) return;
            laps.Account = values.TryGetValue("n", out object name) ? name as string : null;
            laps.Password = values.TryGetValue("p", out object secret) ? secret as string : null;
            if (values.TryGetValue("t", out object time) && long.TryParse(time as string, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long stamp))
            {
                laps.Changed = FileTime(stamp);
            }
        }

        private static object First(SearchResult result, string attribute)
        {
            ResultPropertyValueCollection values = result.Properties[attribute];
            return values != null && values.Count > 0 ? values[0] : null;
        }

        /// <summary>A FILETIME (UTC) as this machine's local time; null for 0 or nothing.</summary>
        private static DateTime? FileTime(object value)
        {
            long stamp = value is long number ? number : 0;
            if (stamp <= 0 || stamp == long.MaxValue) return null;
            try
            {
                return DateTime.FromFileTime(stamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static string DomainName()
        {
            try
            {
                return System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain().Name;
            }
            catch (Exception)
            {
                return "this domain";
            }
        }

        private static class NativeMethods
        {
            public const uint NCRYPT_SILENT_FLAG = 0x40;

            [DllImport("ncrypt.dll")]
            public static extern int NCryptUnprotectSecret(out IntPtr phDescriptor, uint dwFlags, byte[] pbProtectedBlob, uint cbProtectedBlob, IntPtr pMemPara, IntPtr hWnd, out IntPtr ppbData, out uint pcbData);

            [DllImport("ncrypt.dll")]
            public static extern int NCryptCloseProtectionDescriptor(IntPtr hDescriptor);

            [DllImport("kernel32.dll")]
            public static extern IntPtr LocalFree(IntPtr hMem);
        }
    }
}
