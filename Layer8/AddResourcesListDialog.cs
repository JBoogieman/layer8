
using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using Microsoft.ConfigurationManagement.AdminConsole.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows;

namespace Layer8.CollectionManagement
{
    public partial class AddResourcesListDialog : SmsCustomDialog
    {
        private long searchCompleteTime = -1L;
        private readonly ControlsInspector controlsInspector;
        private SmsBackgroundWorker backgroundWorkerQueryMachine;
        private List<string> searchedNames = new List<string>();

        // The rule Layer8.Device.RemoteTools.IsComputerName uses: letters, digits, dots, hyphens, underscores.
        // A line with anything else (an apostrophe, a second pasted column) isn't a computer name, and in the
        // query it would break the WQL or change what it matches.
        private static readonly Regex ComputerName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{0,254}$");

        public AddResourcesListDialog()
        {
            InitializeComponent();

            Title = "Add Resources to Collection";

            // Esc closes without adding, like Cancel.
            CancelButton = buttonCancel;

            controlsInspector = new ControlsInspector();
            controlsInspector.AddControl(listViewSelectedResources, new ControlDataStateEvaluator(ValidateResourceValue), "Search for resources");
            controlsInspector.InspectAll();
        }

        private ControlDataState ValidateResourceValue()
        {
            return listViewSelectedResources.Items.Count > 0 ? ControlDataState.Valid : ControlDataState.Invalid;
        }

        private void ButtonSearch_Click(object sender, EventArgs e)
        {
            if (searchCompleteTime > 0L && DateTime.Now.Ticks - searchCompleteTime < SystemInformation.DoubleClickTime * 5000L)
                return;

            if (backgroundWorkerQueryMachine != null && backgroundWorkerQueryMachine.IsBusy)
            {
                backgroundWorkerQueryMachine.CancelAsync();
                UseWaitCursor = false;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(textBoxSeachList.Text))
                    return;
                
                // A column pasted from Excel brings blank lines and stray spaces, and the same name twice would
                // add two membership rules for one device.
                List<string> resourceList = new List<string>();
                List<string> notNames = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in textBoxSeachList.Lines)
                {
                    string name = line.Trim();
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    if (ComputerName.IsMatch(name)) resourceList.Add(name); else notNames.Add(name);
                }

                if (notNames.Count > 0)
                {
                    System.Windows.MessageBox.Show(string.Format("Left out {0} line{1} that can't be a computer name: {2}", notNames.Count, notNames.Count == 1 ? "" : "s", Shorten(notNames)),
                        "Add Resources", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                if (resourceList.Count == 0)
                    return;
                searchedNames = resourceList;

                string query = string.Format("SELECT * From SMS_R_System WHERE Name IN ('{0}') ORDER by Name", string.Join("','", resourceList));

                backgroundWorkerQueryMachine = new SmsBackgroundWorker(20);
                backgroundWorkerQueryMachine.QueryProcessorObjectsReady += new EventHandler<QueryProcessorObjectsEventArgs>(BackgroundWorkerQueryMachine_QueryProcessorObjectsEvent);
                backgroundWorkerQueryMachine.QueryProcessorCompleted += new EventHandler<RunWorkerCompletedEventArgs>(BackgroundWorkerQueryMachine_QueryProcessorCompleted);
                buttonSearch.Text = "Stop";
                UseWaitCursor = true;
                listViewSelectedResources.Items.Clear();
                ConnectionManager.QueryProcessor.ProcessQuery(backgroundWorkerQueryMachine, query);
            }
        }

        private void BackgroundWorkerQueryMachine_QueryProcessorObjectsEvent(object sender, QueryProcessorObjectsEventArgs e)
        {
            List<ListViewItem> list = new List<ListViewItem>();
            foreach (IResultObject resultObject in e.ResultObjects)
            {
                string name = !resultObject.PropertyList.ContainsKey("Name") ? resultObject["ResourceID"].LongValue.ToString() : resultObject["Name"].StringValue;
                string domain = resultObject["ResourceDomainORWorkgroup"].ObjectValue != null ? resultObject["ResourceDomainORWorkgroup"].StringValue : string.Empty;
                string siteCode = (resultObject["SMSAssignedSites"].ObjectValue != null && resultObject["SMSAssignedSites"].StringArrayValue.Length > 0) ? resultObject["SMSAssignedSites"].StringArrayValue[0] : string.Empty;

                ListViewItem listViewItem = new ListViewItem()
                {
                    Text = name,
                    SubItems = {
                        domain,
                        siteCode
                    },
                    Tag = resultObject,
                    Selected = true
                };

                list.Add(listViewItem);
            }
            listViewSelectedResources.BeginUpdate();
            listViewSelectedResources.Items.AddRange(list.ToArray());
            listViewSelectedResources.EndUpdate();
        }

        private void BackgroundWorkerQueryMachine_QueryProcessorCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            bool finished = false;
            try
            {
                if (e.Error != null)
                {
                    SccmExceptionDialog.ShowDialog(this, e.Error, "Error");
                }
                else if (e.Cancelled)
                {
                    if (listViewSelectedResources.Items.Count > 0)
                    {
                        listViewSelectedResources.Items[0].Selected = true;
                        listViewSelectedResources.Items[0].Focused = true;
                    }
                }
                else
                {
                    if (listViewSelectedResources.Items.Count > 0)
                    {
                        listViewSelectedResources.Items[0].Selected = true;
                        listViewSelectedResources.Items[0].Focused = true;
                    }
                    finished = true;
                }
            }
            finally
            {
                backgroundWorkerQueryMachine.Dispose();
                backgroundWorkerQueryMachine = null;
                UseWaitCursor = false;
                buttonSearch.Focus();
                buttonSearch.Text = "Search";
                searchCompleteTime = DateTime.Now.Ticks;
                controlsInspector.InspectAll();
                UtilitiesClass.UpdateListViewColumnsSize(listViewSelectedResources, columnMachineName);
            }

            if (finished)
                ReportNotFound();
        }

        /// <summary>Names that matched no device, so a pasted list that's partly wrong doesn't pass unnoticed.</summary>
        private void ReportNotFound()
        {
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in listViewSelectedResources.Items)
                found.Add(item.Text);

            List<string> missing = searchedNames.Where(name => !found.Contains(name)).ToList();
            if (missing.Count == 0)
                return;
            System.Windows.MessageBox.Show(string.Format("Found {0} of the {1} names. Not in ConfigMgr: {2}", searchedNames.Count - missing.Count, searchedNames.Count, Shorten(missing)),
                "Add Resources", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string Shorten(List<string> names)
        {
            const int Shown = 10;
            return names.Count <= Shown ? string.Join(", ", names) : string.Join(", ", names.Take(Shown)) + string.Format(" (+{0} more)", names.Count - Shown);
        }

        private void AddToCollectionAndClose()
        {
            List<IResultObject> resources = listViewSelectedResources.Items.Cast<ListViewItem>().Select(item => (IResultObject)item.Tag).ToList();
            if (!ApplyToCollection(resources)) return;

            DataUpdatedDelegate(this, new List<PropertyDataUpdateItem>()
            {
                new PropertyDataUpdateItem(SelectedObject, PropertyDataUpdateAction.Update)
            });

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Adds a direct rule per device. Remove Resources List overrides this. False keeps the window open.</summary>
        protected virtual bool ApplyToCollection(List<IResultObject> resources)
        {
            List<IResultObject> list = new List<IResultObject>();
            foreach (IResultObject resource in resources)
            {
                IResultObject instance = ConnectionManager.CreateEmbeddedObjectInstance("SMS_CollectionRuleDirect");
                instance["ResourceClassName"].StringValue = "SMS_R_System";
                instance["RuleName"].StringValue = resource["Name"].StringValue;
                instance["ResourceID"].IntegerValue = resource["ResourceID"].IntegerValue;
                list.Add(instance);
            }

            SelectedObject.ExecuteMethod("AddMembershipRules", new Dictionary<string, object>()
            {
                {
                    "collectionRules",
                    list
                }
            });
            return true;
        }

        private void ListViewSelectedResources_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (listViewSelectedResources.SelectedItems.Count == 0)
                return;

            listViewSelectedResources.BeginUpdate();
            while (listViewSelectedResources.SelectedItems.Count > 0)
            {
                listViewSelectedResources.Items.Remove(listViewSelectedResources.SelectedItems[0]);
            }
            listViewSelectedResources.EndUpdate();

            UtilitiesClass.UpdateListViewColumnsSize(listViewSelectedResources, columnMachineName);

            controlsInspector.InspectAll();
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            if (listViewSelectedResources.Items.Count == 0)
            {
                System.Windows.MessageBox.Show("Search for one or more resources first.", "Configuration Manager", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                listViewSelectedResources.Focus();
            }
            else
            {
                AddToCollectionAndClose();
            }
        }

        private void ButtonCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void ButtonClear_Click(object sender, EventArgs e)
        {
            listViewSelectedResources.Items.Clear();
        }
    }
}
