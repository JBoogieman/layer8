using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System.DirectoryServices;
using System.Windows.Forms;
using System;

namespace Layer8.Device
{
    public partial class LAPSDialog : SmsCustomDialog
    {
        const AuthenticationTypes authenticationTypes = AuthenticationTypes.Secure | AuthenticationTypes.Sealing;
        private readonly IResultObject resultObjects;

        public LAPSDialog(IResultObject selectedResultObjects, ActionDescription action)
        {
            InitializeComponent();

            resultObjects = selectedResultObjects;

            Title = action.DisplayName + ": " + resultObjects["Name"].StringValue;
        }

        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            SafeAction.Run(action?.DisplayName ?? "Show", () => ShowCore(sender, scopeNode, action, selectedObject, dataUpdatedDelegate, Status));
        }

        private static void ShowCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            using (LAPSDialog dialog = new LAPSDialog(selectedObject, action))
            {
                dialog.ShowDialog();
                return;
            }
        }

        private void LAPSDialog_Shown(object sender, EventArgs e)
        {
            try
            {
                // Records such as "x64 Unknown Computer (x64 Unknown Computer)" aren't machines, and their brackets
                // would break the LDAP filter; real names are escaped anyway, the same way BitLocker lookups are.
                string name = resultObjects["Name"].StringValue;
                if (!RemoteTools.IsComputerName(name))
                {
                    textBoxPassword.Text = "Not a computer name";
                    return;
                }

                using (DirectorySearcher search = new DirectorySearcher
                {
                    Filter = string.Format("(&(objectCategory=computer)(objectClass=computer)(cn={0}))", BitLockerKeys.LdapEscape(name))
                })
                {
                    search.PropertiesToLoad.Add("distinguishedName");
                    search.PropertiesToLoad.Add("ms-mcs-admpwdexpirationtime");
                    search.PropertiesToLoad.Add("ms-mcs-admpwd");

                    SearchResult result = search.FindOne();

                    if (result == null)
                    {
                        textBoxPassword.Text = "Not found in Active Directory";
                    }
                    else
                    {
                        if (result.Properties.Contains("ms-mcs-admpwdexpirationtime"))
                        {
                            long value = (long)result.Properties["ms-mcs-admpwdexpirationtime"][0];
                            // A FILETIME is UTC; FromFileTime converts it to this machine's local time.
                            labelExpire.Text = DateTime.FromFileTime(value).ToString("g");
                        }
                        if (result.Properties.Contains("ms-mcs-admpwd"))
                        {
                            textBoxPassword.Text = result.Properties["ms-mcs-admpwd"][0].ToString();
                        }
                        else
                        {
                            textBoxPassword.Text = "No LAPS data";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionUtilities.TraceException(ex);
                SccmExceptionDialog.ShowDialog(ex);
            }
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
