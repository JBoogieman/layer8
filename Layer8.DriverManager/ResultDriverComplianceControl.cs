using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using Microsoft.ConfigurationManagement.AdminConsole.Common;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Management;
using System.Linq;
using System.Security.AccessControl;

namespace Layer8.DriverManager.PropertiesDialog
{

    public partial class ResultDriverComplianceControl : SmsPageControl
    {
        private BackgroundWorker backgroundWorker;
        // The package picked with Browse: the box shows its display name (name and version), which no query matches.
        private string selectedPackageId;

        public ResultDriverComplianceControl(SmsPageData pageData)
          : base(pageData)
        {
            InitializeComponent();

            buttonRefresh.Image = new Icon(Properties.Resources.reload, new Size(16, 16)).ToBitmap();

            Title = "Driver Compliance";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            if (!PropertyManager["IsClient"].BooleanValue)
            {
                buttonRefresh.Enabled = false;
            }

            Initialized = true;
        }

        private void ButtonRefresh_Click(object sender, EventArgs e)
        {
            listViewDrivers.IsLoading = true;
            listViewDrivers.UpdateColumnWidth(columnHeaderDescription);
            listViewDrivers.Items.Clear();

            backgroundWorker = new BackgroundWorker();
            backgroundWorker.DoWork += new DoWorkEventHandler(InfoWorker_DoWork);
            backgroundWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(InfoWorker_RunWorkerCompleted);
            backgroundWorker.WorkerSupportsCancellation = false;
            backgroundWorker.WorkerReportsProgress = false;
            buttonRefresh.Enabled = false;
            backgroundWorker.RunWorkerAsync(selectedPackageId);
        }

        // What the background read found. The worker only collects; the UI thread fills the tab, because WinForms
        // controls may only be changed from the thread that created them.
        private sealed class ComplianceResult
        {
            public List<ListViewItem> Rows = new List<ListViewItem>();
            public string Manufacturer, Model;
        }

        private void InfoWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            string packageId = e.Argument as string;
            string host = Utility.ComputerName(PropertyManager["Name"].StringValue);
            if (!Utility.CheckFolderPermissions(string.Format(@"\\{0}\admin$", host), FileSystemRights.ReadData))
            {
                throw new InvalidOperationException("Access Denied");
            }

            ManagementScope scope = Utility.GetWMIScope(host, @"cimv2");
            ObjectQuery query = new ObjectQuery("SELECT * FROM Win32_PnPSignedDriver WHERE DriverProviderName IS NOT NULL");
            List<ManagementObject> signedDriver = Utility.SearchWMIToList(scope, query);

            List<IResultObject> driverObjects = new List<IResultObject>();

            if (!string.IsNullOrEmpty(packageId))
            {
                string query2 = string.Format("SELECT * FROM SMS_DriverPackage WHERE PackageID = '{0}'", Utility.WqlText(packageId));
                IResultObject packageObject = Utility.GetFirstWMIInstance(ConnectionManager, query2);
                if (packageObject == null)
                    throw new InvalidOperationException(string.Format("Driver package {0} isn't there any more. Pick it again with Browse.", packageId));
                driverObjects = Utility.SearchWMIToList(ConnectionManager, string.Format("SELECT * FROM SMS_Driver WHERE CI_ID IN (SELECT DC.CI_ID FROM SMS_DriverContainer AS DC WHERE DC.PackageID = '{0}') ORDER BY LocalizedDisplayName", Utility.WqlText(packageObject["PackageID"].StringValue)));
            }

            ComplianceResult result = new ComplianceResult();
            foreach (ManagementObject item in signedDriver)
            {
                try
                {
                    string provider = item["DriverProviderName"].ToString();
                    string description = item["DeviceName"].ToString();
                    string oeminf = item["InfName"].ToString();

                    if (provider.Length > 0 && description.Length > 0)
                    {
                        if (provider == "Microsoft" || DriverExists(result.Rows, description, oeminf))
                            continue;

                        if (!string.IsNullOrEmpty(packageId))
                        {
                            // The device names its INF file; anything but a plain file name would read another file.
                            if (!Utility.IsPlainFileName(oeminf)) continue;
                            string remoteINFFile = string.Format(@"\\{0}\admin$\inf\{1}", host, oeminf);
                            Driver iniDriver = new Driver(remoteINFFile);

                            IResultObject driver = driverObjects?.Where(x => x.Properties["LocalizedDisplayName"].StringValue.Equals(iniDriver.Model)).FirstOrDefault();
      
                            string driverVersion = driver?["DriverVersion"].StringValue;
                            Color textColor = driverVersion == null ? Color.Black : driverVersion == item["DriverVersion"].ToString() ? Color.Green : Color.Red;

                            result.Rows.Add(new ListViewItem()
                            {
                                Text = provider,
                                SubItems = {
                                    description,
                                    item["DriverVersion"].ToString(),
                                    driverVersion
                                },
                                Tag = oeminf,
                                ForeColor = textColor
                            });
                        }
                        else
                        {
                            result.Rows.Add(new ListViewItem()
                            {
                                Text = provider,
                                SubItems = {
                                    description,
                                    item["DriverVersion"].ToString(),
                                 },
                                Tag = oeminf
                            });
                        }
                    }
                }
                catch { }
            }

            query = new ObjectQuery("SELECT * FROM Win32_ComputerSystem");
            ManagementObject computerSystem = Utility.GetFirstWMIInstance(scope, query);

            result.Manufacturer = computerSystem?["Manufacturer"] as string;
            result.Model = computerSystem?["Model"] as string;
            e.Result = result;
        }

        private void InfoWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                    SccmExceptionDialog.ShowDialog(this, e.Error, "Error");
                else if (e.Result is ComplianceResult result)
                {
                    listViewDrivers.Items.AddRange(result.Rows.ToArray());
                    labelManufacturer.Text = result.Manufacturer;
                    labelProductName.Text = result.Model;
                }
            }
            finally
            {
                if (sender as BackgroundWorker == backgroundWorker)
                {
                    backgroundWorker.Dispose();
                    backgroundWorker = null;
                    UseWaitCursor = false;
                    listViewDrivers.IsLoading = false;
                    listViewDrivers.UpdateColumnWidth(columnHeaderDescription);
                    buttonRefresh.Enabled = true;
                }
            }
        }

        /// <summary>True when a row for the same device (its description) or the same INF file is already there.</summary>
        private static bool DriverExists(List<ListViewItem> rows, string desc, string oeminf)
        {
            foreach (ListViewItem item in rows)
            {
                // SubItems[0] is the provider; the description is the second column.
                if (item.SubItems[1].Text == desc || (string)item.Tag == oeminf)
                    return true;
            }

            return false;
        }

        private void ButtonBrowse_Click(object sender, EventArgs e)
        {
            BrowsePackageExtend browsePackageExtend = null;
            try
            {
                browsePackageExtend = new BrowsePackageExtend(ConnectionManager)
                {
                    QueryClass = "SMS_DriverPackage",
                    Text = "Select Driver Package"
                };

                if (browsePackageExtend.ShowDialog(this) != DialogResult.OK)
                    return;

                selectedPackageId = browsePackageExtend.BrowsePackageObject["PackageID"].StringValue;
                textBoxDriverPackage.Text = BrowsePackageExtend.GetPackageDisplayName(browsePackageExtend.BrowsePackageObject, string.Empty);
            }
            finally
            {
                if (browsePackageExtend != null)
                    browsePackageExtend.Dispose();
            }
        }

        private void ListView_CopyKeyEvent(object sender, EventArgs e)
        {
            Utility.CopyToClipboard((ListView)sender);
        }
    }
}
    
