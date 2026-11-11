using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Layer8.DriverManager
{
    /// <summary>
    /// Second page of the Lenovo Driver Pack wizard: the packs for the chosen Windows release, with the models in
    /// this ConfigMgr site ticked (matched by machine type, the first four characters of a Lenovo model number).
    /// Finishing downloads each pack, checks it against the catalog's hash and Lenovo's signature, extracts it and
    /// copies the drivers to the driver source folder, ready for the Driver Package Importer.
    /// </summary>
    public partial class LenovoDriverPackCatalogPage : SmsPageControl
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(LenovoDriverPackCatalogPage));

        private BackgroundWorker backgroundWorker;
        private readonly ModifyRegistry registry = new ModifyRegistry();
        private Process extractProcess;
        private WebClient webClient;
        private readonly Queue<KeyValuePair<LenovoDriverPackage, Uri>> _downloadUrls = new Queue<KeyValuePair<LenovoDriverPackage, Uri>>();
        private bool downloadQueueFinished = false;
        private readonly Queue<KeyValuePair<LenovoDriverPackage, string>> _extractFiles = new Queue<KeyValuePair<LenovoDriverPackage, string>>();
        private bool extractQueueFinished = false;
        private readonly Dictionary<string, string> error = new Dictionary<string, string>();
        private readonly List<string> successful = new List<string>();
        private int totalPacks;
        private int downloadedPacks = 0;
        private readonly int extracted = 1;
        private string currentDownloadFileName;
        private string currentModel;
        private LenovoDriverPackage currentPackage;
        private string osKey;
        private string osVersion;
        private string osFolder;
        private string structure;
        private string tempFolderPath;
        private string sourceFolderPath;

        public LenovoDriverPackCatalogPage(SmsPageData pageData)
            : base(pageData)
        {
            Title = "Select Driver Pack";
            Headline = "Select driver pack to download";

            InitializeComponent();

            checkBoxOverwrite.Visible = false;
            // Lenovo's catalog gives no sizes and no release notes.
            ColumnSize.Visible = false;
            ColumnVersion.Width = 90;
            releaseNotesToolStripMenuItem.Text = "Copy Download Link";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            ControlsInspector.AddControl(dataGridViewDriverPackages, new ControlDataStateEvaluator(ValidateSelectedPack), "Select driver pack to download");
            dataGridViewDriverPackages.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            Initialized = false;
        }

        public override void PostApply(BackgroundWorker worker, DoWorkEventArgs e)
        {
            try
            {
                backgroundWorker = worker;

                worker.ReportProgress(0, "Queuing up Driver Pack downloads ...");

                List<LenovoDriverPackage> packages = (List<LenovoDriverPackage>)UserData["DriverPackItems"];
                totalPacks = Math.Max(1, packages.Count);

                foreach (LenovoDriverPackage package in packages)
                {
                    _downloadUrls.Enqueue(new KeyValuePair<LenovoDriverPackage, Uri>(package, new Uri(package.Url)));
                }

                if (_downloadUrls.Count == 0) downloadQueueFinished = true; else DownloadFiles();

                do // wait for all the download to finish
                {
                    Thread.Sleep(1000);
                } while (downloadQueueFinished == false);

                worker.ReportProgress(50, "Extracting Driver Packs ...");

                if (_extractFiles.Count == 0) extractQueueFinished = true; else ExtractFiles();

                do // wait for all the extractions to finish
                {
                    Thread.Sleep(500);
                } while (extractQueueFinished == false);

                PrepareCompletion(successful, error);
                base.PostApply(worker, e);
            }
            catch (Exception ex)
            {
                PrepareError(ex.Message);
                throw;
            }
        }

        public override void OnAddSummary(SummaryRequestHandler handler)
        {
            base.OnAddSummary(handler);

            List<LenovoDriverPackage> list = new List<LenovoDriverPackage>();
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
            {
                if (Convert.ToBoolean(dataGridViewRow.Cells[columnSelected.Name].Value) && dataGridViewRow.Tag is LenovoDriverPackage pack)
                {
                    list.Add(pack);
                }
            }
            UserData["DriverPackItems"] = list;

            AddAction("GeneralDescription", string.Format("The following driver pack(s) will be imported ({0}):", list.Count));
            AddAction("DriverPackInformation", string.Empty);

            foreach (LenovoDriverPackage package in list)
            {
                AddActionDetailMessage("DriverPackInformation", package.DisplayName);
            }
        }

        private void PrepareCompletion(List<string> successful, Dictionary<string, string> error)
        {
            RemoveAllSummary();

            if (successful.Count > 0)
            {
                AddAction("GeneralDescription", string.Format("The following driver pack(s) was imported ({0}):", successful.Count));
                UpdateActionStatus("GeneralDescription", SmsSummaryAction.ActionStatus.CompleteWithSuccess);
                AddAction("DriverPackInformation", string.Empty);
                foreach (string message in successful)
                    AddActionDetailMessage("DriverPackInformation", message);

                AddAction("EmptyLine", string.Empty);
            }

            if (error.Count > 0)
            {
                AddAction("ErrorDescription", string.Format("The following driver pack(s) failed to import ({0}):", error.Count));
                UpdateActionStatus("ErrorDescription", SmsSummaryAction.ActionStatus.CompleteWithErrors);
                AddAction("ErrorInformation", string.Empty);
                foreach (KeyValuePair<string, string> item in error)
                {
                    AddActionDetailMessage("ErrorInformation", string.Format("{0}:\n - {1}", item.Key, item.Value));
                }
            }
        }

        private void PrepareError(string errorMessage)
        {
            RemoveAllSummary();
            AddAction("ErrorInfo", errorMessage);
            UpdateActionStatus("ErrorInfo", SmsSummaryAction.ActionStatus.CompleteWithErrors);
        }

        private void RemoveAllSummary()
        {
            foreach (string id in Enumerable.ToList(Enumerable.Select(GetSummaryItems(), i => i.Id)))
                RemoveItem(id);
        }

        public override void OnActivated()
        {
            base.OnActivated();

            // The catalog's own os and version for the release picked on the first page (version null: any).
            osKey = UserData["LenovoOs"] as string ?? "win11";
            osVersion = UserData["LenovoVersion"] as string;
            // The same folder naming as the Dell and HP packs (Lenovo's are all 64-bit): Win11-64, or Win11IoT-64.
            osFolder = string.Format("Win{0}-64", Regex.Replace(osKey.Substring(3), @"\s+", string.Empty));
            structure = registry.ReadString("LegacyFolderStructure");
            sourceFolderPath = registry.ReadString("DriverSourceFolder");
            tempFolderPath = registry.ReadString("TempDownloadPath");

            ProcessCatalog();

            Utility.UpdateDataGridViewColumnsSize(dataGridViewDriverPackages, columnPack);
        }

        public override bool OnDeactivate()
        {
            dataGridViewDriverPackages.EndEdit();
            return base.OnDeactivate();
        }

        private void ProcessCatalog()
        {
            dataGridViewDriverPackages.Rows.Clear();

            bool known = false;
            string prefix = string.IsNullOrEmpty(registry.ReadString("LenovoFolderPrefix")) ? "Lenovo" : registry.ReadString("LenovoFolderPrefix");

            // The catalog the first page checked against Lenovo's signature.
            XElement catalog = UserData["LenovoCatalog"] as XElement ?? LenovoDriverPackGeneralPage.LoadCatalog(LenovoDriverPackGeneralPage.CatalogFile);
            foreach (XElement model in catalog.Elements("Model"))
            {
                XElement pack = PackFor(model);
                if (pack == null || !Authenticode.IsWebLink(pack.Value.Trim())) continue;

                LenovoDriverPackage package = new LenovoDriverPackage(model, pack) { Vendor = prefix };
                package.GenerateModelFolderName(osFolder, structure);

                DataGridViewRow dataGridViewRow = new DataGridViewRow();
                dataGridViewRow.CreateCells(dataGridViewDriverPackages);
                dataGridViewRow.Cells[0].Value = false;
                dataGridViewRow.Cells[1].Value = package.DisplayName;
                dataGridViewRow.Cells[2].Value = package.Version;
                dataGridViewRow.Cells[3].Value = string.Empty;
                dataGridViewRow.Tag = package;
                dataGridViewDriverPackages.Rows.Add(dataGridViewRow);
            }

            // Tick the models this site has: ConfigMgr's hardware inventory reports a Lenovo's model number
            // (20XWCTO1WW), whose first four characters are the machine type the catalog lists.
            try
            {
                HashSet<string> machineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string query = "SELECT DISTINCT Model FROM SMS_G_System_COMPUTER_SYSTEM WHERE Manufacturer = 'LENOVO'";
                foreach (IResultObject model in Utility.SearchWMIToList(ConnectionManager, query))
                {
                    string number = model["Model"].StringValue?.Trim() ?? string.Empty;
                    if (number.Length >= 4) machineTypes.Add(number.Substring(0, 4));
                }
                foreach (DataGridViewRow row in dataGridViewDriverPackages.Rows)
                {
                    if (row.Tag is LenovoDriverPackage package && package.MachineTypes.Overlaps(machineTypes))
                    {
                        row.Cells[0].Value = true;
                        row.Cells[4].Value = "Model detected";
                        known = true;
                    }
                }
                log.InfoFormat("Lenovo driver packs: {0} pack(s) for {1} {2}; {3} Lenovo machine type(s) in this site", dataGridViewDriverPackages.Rows.Count, osKey, osVersion ?? "(any)", machineTypes.Count);
            }
            catch (Exception ex)
            {
                log.Warn("Couldn't read the Lenovo models in this site: " + ex.GetBaseException().Message);
            }

            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
            {
                LenovoDriverPackage package = (LenovoDriverPackage)dataGridViewRow.Tag;
                string path = Path.Combine(sourceFolderPath, package.FolderName);

                if (Directory.Exists(path))
                {
                    string[] fileList = Directory.GetFiles(path, "*.version");
                    if (File.Exists(Path.Combine(path, package.VersionFile)))
                    {
                        dataGridViewRow.Cells[0].Value = false;
                        dataGridViewRow.Cells[4].Value = "Downloaded";
                    }
                    else if (fileList.Length > 0)
                    {
                        dataGridViewRow.Cells[4].Value = "New version";
                    }
                }
            }

            dataGridViewDriverPackages.Sort(known ? columnStatus : columnPack, known ? ListSortDirection.Descending : ListSortDirection.Ascending);

            Initialized = true;
        }

        /// <summary>
        /// The model's pack for the chosen release: the one made for it, else one Lenovo marks for any release of
        /// that Windows version ("*"). With no release chosen, the newest pack for the Windows version.
        /// </summary>
        private XElement PackFor(XElement model)
        {
            List<XElement> packs = model.Elements("SCCM").Where(p => string.Equals((string)p.Attribute("os"), osKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (packs.Count == 0) return null;
            if (osVersion != null)
            {
                return packs.FirstOrDefault(p => string.Equals((string)p.Attribute("version"), osVersion, StringComparison.OrdinalIgnoreCase))
                    ?? packs.FirstOrDefault(p => (string)p.Attribute("version") == "*");
            }
            return packs.OrderByDescending(p => (string)p.Attribute("version") ?? string.Empty, StringComparer.OrdinalIgnoreCase).First();
        }

        private void DownloadFiles()
        {
            if (_downloadUrls.Any())
            {
                KeyValuePair<LenovoDriverPackage, Uri> data = _downloadUrls.Dequeue();

                LenovoDriverPackage package = data.Key;
                Uri url = data.Value;
                string filename = Path.GetFileName(url.LocalPath);

                currentDownloadFileName = Path.Combine(tempFolderPath, filename);
                currentModel = package.DisplayName;
                currentPackage = package;

                // A pack already downloaded in full is used again.
                if (File.Exists(currentDownloadFileName) && new FileInfo(currentDownloadFileName).Length == Utility.GetFileSize(url))
                {
                    _extractFiles.Enqueue(new KeyValuePair<LenovoDriverPackage, string>(package, currentDownloadFileName));
                    CheckDownloadQueueCompleted();
                    return;
                }

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                webClient = new WebClient();
                webClient.DownloadProgressChanged += Client_DownloadProgressChanged;
                webClient.DownloadFileCompleted += Client_DownloadFileCompleted;
                webClient.DownloadFileAsync(url, currentDownloadFileName);
            }
        }

        private void Client_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            double bytesIn = e.BytesReceived;
            double totalBytes = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : 1;
            double percentage = bytesIn / totalBytes * 100;
            double start = 100.0 / totalPacks * downloadedPacks;
            backgroundWorker.ReportProgress(Convert.ToInt32((start + (percentage / totalPacks)) * 0.5),
                string.Format("Downloading: {0} - {1} of {2}", currentModel, Utility.FormatBytes(bytesIn, "0"), Utility.FormatBytes(totalBytes, "0")));
        }

        private void Client_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                {
                    error[currentModel] = "Couldn't download the driver pack: " + e.Error.Message;
                }
                else
                {
                    _extractFiles.Enqueue(new KeyValuePair<LenovoDriverPackage, string>(currentPackage, currentDownloadFileName));
                }
            }
            finally
            {
                if (sender as WebClient == webClient)
                {
                    webClient.Dispose();
                    webClient = null;

                    CheckDownloadQueueCompleted();
                }
            }
        }

        private void CheckDownloadQueueCompleted()
        {
            if (_downloadUrls.Count > 0)
            {
                ++downloadedPacks;
                DownloadFiles();
            }
            else
            {
                downloadQueueFinished = true;
            }
        }

        private void ExtractFiles()
        {
            if (!_extractFiles.Any()) return;

            KeyValuePair<LenovoDriverPackage, string> data = _extractFiles.Dequeue();
            LenovoDriverPackage package = data.Key;
            currentModel = package.DisplayName;

            backgroundWorker.ReportProgress(Convert.ToInt32(50 + ((50.0 / totalPacks * extracted) * 0.5)), string.Format("Processing {0} : extracting to temp folder", currentModel));

            // Extract to the local temp folder first, then copy to the (often network) source folder.
            string tempFolder = Path.Combine(tempFolderPath, "extract", package.FolderName);
            string destinationFolder = Path.Combine(sourceFolderPath, package.FolderName);

            using (extractProcess = new Process())
            {
                try
                {
                    // The pack runs elevated, so it has to be the file Lenovo's catalog describes and signed by Lenovo:
                    // not a file tampered with on the way, or one left in the download folder under the same name.
                    CheckHash(data.Value, package.Crc);
                    Authenticode.RequireSignedBy(data.Value, Authenticode.LenovoSigner, "Lenovo");

                    if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);

                    // Lenovo's packs are Inno Setup self-extractors: /EXTRACT=YES unpacks the drivers into /DIR.
                    extractProcess.StartInfo.FileName = data.Value;
                    extractProcess.StartInfo.Arguments = string.Format("/VERYSILENT /DIR=\"{0}\" /EXTRACT=YES", tempFolder);
                    extractProcess.StartInfo.CreateNoWindow = true;
                    extractProcess.StartInfo.UseShellExecute = true;
                    extractProcess.StartInfo.Verb = "RunAs";
                    extractProcess.Start();
                    extractProcess.WaitForExit();
                    int exitCode = extractProcess.ExitCode;
                    extractProcess.Close();

                    backgroundWorker.ReportProgress(Convert.ToInt32(50 + ((100.0 / totalPacks * extracted) * 0.5) - 1), string.Format("Processing {0} : moving to destination folder", currentModel));

                    if (!Directory.Exists(tempFolder) || !Directory.EnumerateFileSystemEntries(tempFolder).Any())
                        throw new DirectoryNotFoundException(string.Format("The pack extracted nothing into {0} (exit code {1})", tempFolder, exitCode));

                    // A version file in the folder shows later whether Lenovo has a newer pack.
                    File.Create(Path.Combine(tempFolder, package.VersionFile)).Close();

                    if (registry.ReadBool("WipeSource") && Directory.Exists(destinationFolder))
                        Directory.Delete(destinationFolder, true);

                    if (Directory.Exists(destinationFolder))
                    {
                        foreach (string file in Directory.GetFiles(destinationFolder, "*.version"))
                        {
                            File.Delete(file);
                        }
                    }

                    Utility.Copy(tempFolder, destinationFolder, true);
                    Directory.Delete(tempFolder, true);
                    log.InfoFormat("Lenovo driver pack {0} extracted to {1}", currentModel, destinationFolder);
                }
                catch (Exception ex)
                {
                    error[currentModel] = "Couldn't extract the driver pack: " + ex.Message;
                    log.Warn("Lenovo driver pack " + currentModel + ": " + ex.Message);
                    CheckExtractQueueCompleted();
                    return;
                }
            }

            successful.Add(currentModel);
            CheckExtractQueueCompleted();
        }

        /// <summary>
        /// Compares the download with the hash in Lenovo's catalog: SHA-256 (64 hex digits) or MD5 (32). A catalog
        /// entry without a hash is left to the signature check.
        /// </summary>
        private static void CheckHash(string path, string expected)
        {
            expected = (expected ?? string.Empty).Trim();
            HashAlgorithm algorithm = expected.Length == 64 ? (HashAlgorithm)SHA256.Create() : expected.Length == 32 ? MD5.Create() : null;
            if (algorithm == null) return;
            using (algorithm)
            using (FileStream file = File.OpenRead(path))
            {
                string actual = BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", string.Empty);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("{0} doesn't match the hash in Lenovo's catalog, so Layer8 won't run it. Delete it and download it again.", path));
                }
            }
        }

        private void CheckExtractQueueCompleted()
        {
            if (_extractFiles.Count > 0)
            {
                ExtractFiles();
            }
            else
            {
                extractQueueFinished = true;
            }
        }

        private ControlDataState ValidateSelectedPack()
        {
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
            {
                if (Convert.ToBoolean(dataGridViewRow.Cells[0].Value, CultureInfo.InvariantCulture))
                {
                    return ControlDataState.Valid;
                }
            }
            return ControlDataState.Invalid;
        }

        private void DataGridViewDriverPackages_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (!dataGridViewDriverPackages.IsCurrentCellDirty)
                return;
            dataGridViewDriverPackages.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void DataGridViewDriverPackages_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            ControlsInspector.InspectAll();
            Dirty = !ReadOnly;
        }

        private void DataGridViewDriverPackages_RowsAdded(object sender, DataGridViewRowsAddedEventArgs e)
        {
            Utility.UpdateDataGridViewColumnsSize(dataGridViewDriverPackages, columnPack);
        }

        private void ButtonDeselectAll_Click(object sender, EventArgs e)
        {
            dataGridViewDriverPackages.Focus();
            dataGridViewDriverPackages.BeginEdit(true);
            bool flag = sender == buttonDeselectAll;
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
                dataGridViewRow.Cells[0].Value = flag ? false : true;
            dataGridViewDriverPackages.EndEdit();
        }

        private void ContextMenuStrip1_Opening(object sender, CancelEventArgs e)
        {
            if (dataGridViewDriverPackages.SelectedRows.Count != 1)
                e.Cancel = true;
        }

        private void DataGridViewDriverPackages_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != -1 && e.RowIndex != -1 && e.Button == MouseButtons.Right)
            {
                dataGridViewDriverPackages.ClearSelection();
                dataGridViewDriverPackages.Rows[e.RowIndex].Selected = true;
            }
        }

        private void DataGridViewDriverPackages_KeyUp(object sender, KeyEventArgs e)
        {
            Utility.SelectDataGridViewWithSpace(e, (DataGridView)sender, columnSelected);
        }

        /// <summary>Lenovo's catalog has no release notes link, so the menu copies the pack's download link instead.</summary>
        private void ReleaseNotesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in dataGridViewDriverPackages.SelectedRows)
            {
                if (row.Tag is LenovoDriverPackage package) Clipboard.SetText(package.Url);
            }
        }
    }
}
