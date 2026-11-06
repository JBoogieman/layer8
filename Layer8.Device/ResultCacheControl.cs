using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System;
using System.Management;
using System.Windows.Forms;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.IO;
using System.Security.AccessControl;

namespace Layer8.Device
{
    public partial class ResultCacheControl : SmsPageControl
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private BackgroundWorker backgroundWorker;
        private ManagementObject cacheConfig;

        public ResultCacheControl(SmsPageData pageData)
          : base(pageData)
        {
            InitializeComponent();

            Title = "Client Cache";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            backgroundWorker = new BackgroundWorker();
            backgroundWorker.DoWork += new DoWorkEventHandler(InfoWorker_DoWork);
            backgroundWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(InfoWorker_RunWorkerCompleted);
            backgroundWorker.WorkerSupportsCancellation = false;
            backgroundWorker.WorkerReportsProgress = false;
            UseWaitCursor = true;
            listViewContent.IsLoading = true;
            backgroundWorker.RunWorkerAsync();

            Dirty = false;
        }

        // What the background read found; RunWorkerCompleted sets up the controls on the UI thread.
        private sealed class CacheData
        {
            public string Location;
            public double CacheSizeMB;
            public double FreeSpaceMB;
            public List<ManagementObject> Content;
        }

        private void InfoWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                ManagementScope scope = Utility.GetWMIScope(PropertyManager["Name"].StringValue, @"ccm\softmgmtagent");
                // get cache config instace so we can change it on apply
                cacheConfig = Utility.GetFirstWMIInstance(scope, "SELECT * FROM CacheConfig WHERE ConfigKey='Cache'");
                List<ManagementObject> cacheContent = Utility.SearchWMIToList(scope, "SELECT * FROM CacheInfoEx");

                string location = cacheConfig["Location"].ToString();

                ManagementObject disk = Utility.GetFirstWMIInstance(PropertyManager["Name"].StringValue, @"cimv2", string.Format("Win32_LogicalDisk WHERE DeviceID='{0}:'", location[0]));

                e.Result = new CacheData
                {
                    Location = location,
                    CacheSizeMB = double.Parse(cacheConfig["Size"].ToString()),
                    FreeSpaceMB = double.Parse(disk["FreeSpace"].ToString()) / Utility.BytesPerMegabyte,
                    Content = cacheContent,
                };
            }
            catch (Exception ex)
            {
                string msg = string.Format("{0}: {1}", ex.GetType().Name, ex.Message);
                log.Error(msg);
                throw new InvalidOperationException(msg);
            }
        }

        private void InfoWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                    SccmExceptionDialog.ShowDialog(this, e.Error, "Error");
                else if (e.Result is CacheData data)
                    ShowCache(data);
            }
            finally
            {
                if (sender as BackgroundWorker == backgroundWorker)
                {
                    backgroundWorker.Dispose();
                    backgroundWorker = null;
                    UseWaitCursor = false;
                    listViewContent.IsLoading = false;
                    listViewContent.UpdateColumnWidth(columnHeaderLocation);
                    listViewContent.Sorting = SortOrder.Ascending;
                    Initialized = true;
                }
            }
        }

        protected override bool ApplyChanges(out Control errorControl, out bool showError)
        {
            try
            {
                PutOptions options = new PutOptions
                {
                    Type = PutType.UpdateOnly
                };

                cacheConfig["Size"] = numericUpDown1.Value;
                cacheConfig.Put(options);
            }
            catch (Exception ex)
            {
                string msg = string.Format("{0}: {1}", ex.GetType().Name, ex.Message);
                log.Error(msg);
                throw new InvalidOperationException(msg);
            }

            // need to be false or sccm throws an put error on the orignal property window
            Dirty = false;

            return base.ApplyChanges(out errorControl, out showError);
        }

        private void ShowCache(CacheData data)
        {
            // The slider can't go past the free space, but a cache already set larger than the free space
            // would put its value past the maximum and throw; the maximum covers both.
            trackBarWithoutFocus1.Maximum = Convert.ToInt32(Math.Max(data.FreeSpaceMB, data.CacheSizeMB));
            trackBarWithoutFocus1.TickFrequency = Math.Max(1, trackBarWithoutFocus1.Maximum / 10);
            trackBarWithoutFocus1.LargeChange = trackBarWithoutFocus1.TickFrequency;
            numericUpDown1.Maximum = trackBarWithoutFocus1.Maximum;

            trackBarWithoutFocus1.Value = Convert.ToInt32(data.CacheSizeMB);

            labelLocation.Text = data.Location;
            labelCacheSize.Text = Utility.FormatBytes(data.CacheSizeMB * Utility.BytesPerMegabyte);
            labelSpaceToUse.Text = labelCacheSize.Text;

            trackBarWithoutFocus1.Enabled = true;
            numericUpDown1.Enabled = true;

            UpdateContentListView(data.Content);
        }

        private void UpdateContentListView(List<ManagementObject> items)
        {
            listViewContent.Items.Clear();

            foreach (ManagementObject item in items)
            {
                string size = Utility.FormatBytes(double.Parse(item["ContentSize"].ToString()) * Utility.BytesPerKilobyte);
                DateTime date = ManagementDateTimeConverter.ToDateTime(item["LastReferenced"].ToString());

                listViewContent.Items.Add(new ListViewItem()
                {
                    Text = item["Location"].ToString(),
                    SubItems = {
                            size,
                            date.ToShortDateString(),
                            item["CacheId"].ToString()
                        },
                    Tag = item
                });
            }

            double contentSize = items.Sum(item => double.Parse(item["ContentSize"].ToString()));
            labelUsedSize.Text = Utility.FormatBytes(contentSize * Utility.BytesPerKilobyte);
        }

        private void RemoveCachedItems(IEnumerable<ListViewItem> items)
        {
            foreach (ListViewItem item in items)
            {
                try
                {
                    // get the wmi object
                    ManagementObject managementObject = (ManagementObject)item.Tag;
                    // create location string
                    string str = (managementObject["Location"] as string).Replace(':', '$');
                    string path = string.Format(@"\\{0}\{1}", PropertyManager["Name"].StringValue, str);
                    // check if cached content exists and we have persmissions to remove it
                    if (Directory.Exists(path) && Utility.CheckFolderPermissions(path, FileSystemRights.Delete))
                    {
                        // delete folder
                        Directory.Delete(path, true);
                        // remove wmi object
                        managementObject.Delete();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("Cannot remove " + path + ": it's missing or access is denied.", "Configuration Manager", MessageBoxButton.OK, MessageBoxImage.Hand);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(string.Format("{0}: {1}", ex.GetType().Name, ex.Message));
                    SccmExceptionDialog.ShowDialog(SnapIn.Console, ex);
                }
            }
        }

        private void SetDirty()
        {
            if (!Initialized)
                return;
            Dirty = true;
        }

        private void ContextMenuStrip1_Opening(object sender, CancelEventArgs e)
        {
            if (listViewContent.SelectedItems.Count == 0)
                e.Cancel = true;
        }

        private void ToolStripMenuItemDelete_Click(object sender, EventArgs e)
        {
            ListView.SelectedListViewItemCollection items = listViewContent.SelectedItems;

            MessageBoxResult result = System.Windows.MessageBox.Show(string.Format("Do you really want to remove {0} items from the cache?", items.Count), "Configuration Manager", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);

            if (result == MessageBoxResult.Yes)
            {
                RemoveCachedItems(items.Cast<ListViewItem>().ToList());
                // update the list view
                ManagementScope scope = Utility.GetWMIScope(PropertyManager["Name"].StringValue, @"ccm\softmgmtagent");
                List<ManagementObject> cacheContent = Utility.SearchWMIToList(scope, "SELECT * FROM CacheInfoEx");
                UpdateContentListView(cacheContent);
            }
        }

        private void TrackBar1_ValueChanged(object sender, EventArgs e)
        {
            numericUpDown1.Value = trackBarWithoutFocus1.Value;
            labelSpaceToUse.Text = Utility.FormatBytes(trackBarWithoutFocus1.Value * Utility.BytesPerMegabyte);
            SetDirty();
        }

        private void NumericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            trackBarWithoutFocus1.Value = Convert.ToInt32(numericUpDown1.Value);
            labelSpaceToUse.Text = Utility.FormatBytes(trackBarWithoutFocus1.Value * Utility.BytesPerMegabyte);
            SetDirty();
        }

        private void ButtonClearCache_Click(object sender, EventArgs e)
        {
            // Every item, not the selection: this used to tick everything and then delete only what was selected.
            List<ListViewItem> items = listViewContent.Items.OfType<ListViewItem>().ToList();

            MessageBoxResult result = System.Windows.MessageBox.Show("Are you sure you want to clear the cache?", "Configuration Manager", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);

            if (result == MessageBoxResult.Yes)
            {
                RemoveCachedItems(items);
                // update the list view
                ManagementScope scope = Utility.GetWMIScope(PropertyManager["Name"].StringValue, @"ccm\softmgmtagent");
                List<ManagementObject> cacheContent = Utility.SearchWMIToList(scope, "SELECT * FROM CacheInfoEx");
                UpdateContentListView(cacheContent);
            }
        }
    }
}
