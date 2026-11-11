using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using Microsoft.ConfigurationManagement.AdminConsole.WizardFramework;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace Layer8.DriverManager
{
    /// <summary>
    /// First page of the Lenovo Driver Pack wizard: downloads Lenovo's driver pack catalog (catalogv2.xml, the one
    /// Lenovo publishes for ConfigMgr and MDT) and offers the Windows releases it has packs for.
    /// </summary>
    public partial class LenovoDriverPackGeneralPage : SmsPageControl
    {
        internal static string CatalogFile => Path.Combine(Path.GetTempPath(), "LenovoCatalogV2.xml");

        private BackgroundWorker progressWorker;
        private ProgressInformationDialog progressInformationDialog;
        private readonly ModifyRegistry registry = new ModifyRegistry();
        private bool downloadedCatalog = false;
        private bool processedCatalog = false;
        /// <summary>The catalog once its signature checks out; the next page reads this, not the file again.</summary>
        private XElement catalog;
        /// <summary>Why the last catalog wasn't used, shown on the page until a good one is downloaded.</summary>
        private string catalogProblem;
        /// <summary>Each release in the list, as the catalog names it: its os attribute (win11, win11 IoT) and version.</summary>
        private readonly Dictionary<string, KeyValuePair<string, string>> releaseKeys = new Dictionary<string, KeyValuePair<string, string>>();

        public LenovoDriverPackGeneralPage(SmsPageData pageData)
            : base(pageData)
        {
            FormTitle = "Lenovo Driver Pack";
            Title = "General";
            Headline = "Select the Windows release to narrow down selections";

            InitializeComponent();

            pageData.ProgressBarStyle = ProgressBarStyle.Continuous;
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();
            Initialized = true;

            labelInformation.Text = "Welcome to the Lenovo Driver Pack Download tool.\r\n\r\nThis tool downloads Lenovo's driver packs for ConfigMgr " +
                "(ThinkPad, ThinkCentre and ThinkStation) from Lenovo's catalog.\r\n\r\nSelect the Windows release, and the next page shows the packs " +
                "for it, with the models in your ConfigMgr site already ticked.";

            buttonOptions.Image = new Icon(Properties.Resources.options, new Size(16, 16)).ToBitmap();
            label1.Text = "Windows release";
        }

        public override void OnActivated()
        {
            base.OnActivated();

            bool flag;
            StringBuilder sb = new StringBuilder();
            // Older settings don't have Lenovo's address yet.
            if (string.IsNullOrEmpty(registry.ReadString("LenovoCatalogURI"))) registry.Write("LenovoCatalogURI", OptionsCatalogControl.DefaultLenovoCatalogUri);

            if (processedCatalog)
            {
                flag = true;
                comboBoxOS.Enabled = true;
            }
            // A catalog downloaded in the last hour is used again.
            else if (File.Exists(CatalogFile) && DateTime.TryParse(registry.ReadString("LenovoCatalogDownloadTime"), out DateTime downloaded) && downloaded.AddHours(1) > DateTime.Now)
            {
                downloadedCatalog = true;
                comboBoxOS.Enabled = true;
                flag = processedCatalog = ProcessCatalog();
            }
            else
            {
                flag = false;
                comboBoxOS.Enabled = true;
            }

            if (string.IsNullOrEmpty(registry.ReadString("DriverSourceFolder")))
            {
                flag = false;
                sb.AppendLine("No driver source structure specified!");
            }
            else if (!Utility.CheckFolderPermissions(registry.ReadString("DriverSourceFolder"), FileSystemRights.Modify))
            {
                flag = false;
                sb.AppendLine("Access denied to source folder");
            }

            if (string.IsNullOrEmpty(registry.ReadString("TempDownloadPath")))
            {
                flag = false;
                sb.AppendLine("No temporary download folder specified!");
            }

            if (!processedCatalog && catalogProblem != null) sb.AppendLine(catalogProblem);
            if (!processedCatalog && flag == false && sb.Length == 0) sb.AppendLine("Click the Windows release box to download Lenovo's catalog.");

            ((SmsWizardPage)Parent).WizardForm.EnableButton(ButtonType.Next, flag);
            // Longer messages (a catalog that wasn't used) wrap instead of running off the page.
            labelOptions.MaximumSize = new Size(Math.Max(150, ClientSize.Width - labelOptions.Left - 18), 0);
            labelOptions.Text = sb.ToString();
        }

        public override bool OnDeactivate()
        {
            UserData["OS"] = comboBoxOS.Text;
            UserData["LenovoCatalog"] = catalog;
            if (releaseKeys.TryGetValue(comboBoxOS.Text, out KeyValuePair<string, string> release))
            {
                UserData["LenovoOs"] = release.Key;
                UserData["LenovoVersion"] = release.Value;
            }

            return base.OnDeactivate();
        }

        private void ProgressWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = DownloadCatalog((BackgroundWorker)sender);
        }

        private void ProgressWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressInformationDialog.UpdateProgressText(e.UserState as string);
        }

        private void ProgressWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            bool succeeded = e.Error == null && !e.Cancelled && e.Result is bool workResult && workResult;
            if (e.Error != null)
            {
                Layer8.Layer8Log.GetLogger(GetType()).Error("Downloading the Lenovo driver pack catalog failed", e.Error);
            }
            progressInformationDialog.Result = succeeded;
            progressInformationDialog.UpdateProgressValue(100);
            progressInformationDialog.CloseDialog();

            try
            {
                if (e.Error != null)
                {
                    using (SccmExceptionDialog sccmExceptionDialog = new SccmExceptionDialog(e.Error))
                    {
                        sccmExceptionDialog.ShowDialog();
                    }
                }
            }
            finally
            {
                if (sender as BackgroundWorker == progressWorker)
                {
                    progressWorker.Dispose();
                    progressWorker = null;
                    UseWaitCursor = false;
                }
            }
        }

        private bool DownloadCatalog(BackgroundWorker worker)
        {
            worker.ReportProgress(0, "Downloading Lenovo's catalogv2.xml...");

            if (downloadedCatalog == false)
            {
                Uri source = new Uri(registry.ReadString("LenovoCatalogURI"));
                Layer8.Layer8Log.GetLogger(GetType()).InfoFormat("Downloading the Lenovo catalog from {0}", source);
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (WebClient client = new WebClient())
                {
                    client.DownloadFile(source, CatalogFile);
                }
                Layer8.Layer8Log.GetLogger(GetType()).InfoFormat("Downloaded the Lenovo catalog ({0})", Utility.FormatBytes(new FileInfo(CatalogFile).Length));

                downloadedCatalog = true;
                registry.Write("LenovoCatalogDownloadTime", DateTime.Now.ToString());
            }

            worker.ReportProgress(100, "Done");
            return true;
        }

        private void ButtonOptions_Click(object sender, EventArgs e)
        {
            ShowDialog("Layer8Options", PropertyManager);

            OnActivated();
        }

        private void ComboBoxOS_Click(object sender, EventArgs e)
        {
            if (!(sender as ComboBox).Enabled)
                return;

            if (downloadedCatalog == false)
            {
                (sender as ComboBox).Enabled = false;

                progressInformationDialog = new ProgressInformationDialog
                {
                    Title = "Downloading Catalog",
                };

                progressWorker = new BackgroundWorker();
                progressWorker.DoWork += new DoWorkEventHandler(ProgressWorker_DoWork);
                progressWorker.ProgressChanged += new ProgressChangedEventHandler(ProgressWorker_ProgressChanged);
                progressWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(ProgressWorker_RunWorkerCompleted);
                progressWorker.WorkerReportsProgress = true;
                UseWaitCursor = true;
                progressWorker.RunWorkerAsync();

                progressInformationDialog.ShowDialog(this);
                if (!progressInformationDialog.Result)
                {
                    (sender as ComboBox).Enabled = true;
                    return;
                }
            }

            if (processedCatalog == false)
            {
                processedCatalog = ProcessCatalog();

                OnActivated();
            }
        }

        /// <summary>
        /// The Windows releases Lenovo has packs for, newest first: "Windows 11 24H2", "Windows 10 22H2". A pack
        /// marked for any release of a Windows version ("*") shows under each of that version's releases.
        /// </summary>
        private bool ProcessCatalog()
        {
            comboBoxOS.Items.Clear();
            releaseKeys.Clear();
            try
            {
                catalog = LoadCatalog(CatalogFile);
                catalogProblem = null;
            }
            catch (Exception ex)
            {
                Layer8.Layer8Log.GetLogger(GetType()).Warn("Lenovo's catalog wasn't used", ex);
                catalog = null;
                catalogProblem = (ex is InvalidOperationException ? ex.Message : "Couldn't check Lenovo's catalog, so Layer8 won't use it: " + ex.GetBaseException().Message) +
                    " Click the Windows release box to download it again.";
                // Download it again next time rather than use this copy.
                downloadedCatalog = false;
                registry.Write("LenovoCatalogDownloadTime", string.Empty);
                try { File.Delete(CatalogFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return false;
            }

            foreach (KeyValuePair<string, KeyValuePair<string, string>> release in ReleasesIn(catalog))
            {
                releaseKeys[release.Key] = release.Value;
                comboBoxOS.Items.Add(release.Key);
            }
            if (comboBoxOS.Items.Count > 0) comboBoxOS.SelectedIndex = 0;
            return comboBoxOS.Items.Count > 0;
        }

        /// <summary>
        /// Display name, then the catalog's os and version for it. Windows 11 before 10, and each Windows version's
        /// own packs before its variants (IoT); newest release first within each.
        /// </summary>
        internal static List<KeyValuePair<string, KeyValuePair<string, string>>> ReleasesIn(XElement catalog)
        {
            Dictionary<string, HashSet<string>> versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement pack in catalog.Elements("Model").Elements("SCCM"))
            {
                string os = ((string)pack.Attribute("os") ?? string.Empty).Trim();
                if (!os.StartsWith("win", StringComparison.OrdinalIgnoreCase)) continue;
                if (!versions.TryGetValue(os, out HashSet<string> set)) versions[os] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string version = ((string)pack.Attribute("version") ?? string.Empty).Trim();
                if (version.Length > 0 && version != "*") set.Add(version.ToUpperInvariant());
            }

            List<KeyValuePair<string, KeyValuePair<string, string>>> releases = new List<KeyValuePair<string, KeyValuePair<string, string>>>();
            foreach (string os in versions.Keys.OrderByDescending(o => WindowsNumber(o)).ThenBy(o => o.Length).ThenBy(o => o, StringComparer.OrdinalIgnoreCase))
            {
                // "win11" is Windows 11; "win11iot" or "win11 IoT" is Windows 11 IoT.
                string rest = os.Substring(3).Trim();
                string number = new string(rest.TakeWhile(char.IsDigit).ToArray());
                string variant = rest.Substring(number.Length).Trim();
                string windows = "Windows " + number + (variant.Length > 0 ? " " + (variant.Equals("iot", StringComparison.OrdinalIgnoreCase) ? "IoT" : variant) : string.Empty);
                if (versions[os].Count == 0) releases.Add(new KeyValuePair<string, KeyValuePair<string, string>>(windows, new KeyValuePair<string, string>(os, null)));
                foreach (string version in versions[os].OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    releases.Add(new KeyValuePair<string, KeyValuePair<string, string>>(windows + " " + version, new KeyValuePair<string, string>(os, version)));
                }
            }
            return releases;
        }

        private static int WindowsNumber(string os)
        {
            string digits = new string(os.Substring(3).Trim().TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int number) ? number : 0;
        }

        /// <summary>
        /// Loads Lenovo's catalog once its XML signature checks out: the whole catalog, signed with Lenovo's code
        /// signing certificate, which has to chain to a root Windows trusts. The catalog holds the hashes each pack is
        /// checked against, so a changed catalog (a copy on a mirror, or a file swapped in the temp folder) isn't used.
        /// </summary>
        internal static XElement LoadCatalog(string path)
        {
            // White space is part of what Lenovo signed, so it's kept; no DTDs or outside references are followed.
            XmlDocument document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                document.Load(reader);
            }

            RequireLenovoSignature(document);

            // The pages read the very document that was checked, not the file again.
            using (XmlNodeReader reader = new XmlNodeReader(document))
            {
                return XElement.Load(reader);
            }
        }

        private static void RequireLenovoSignature(XmlDocument document)
        {
            XmlNodeList signatures = document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
            if (signatures.Count != 1 || signatures[0].ParentNode != document.DocumentElement)
                throw new InvalidOperationException("Lenovo's catalog isn't signed, so Layer8 won't use it.");

            SignedXml signedXml = new SignedXml(document);
            signedXml.LoadXml((XmlElement)signatures[0]);

            // One reference, to the whole document: a signature over part of it would say nothing about the rest.
            if (signedXml.SignedInfo.References.Count != 1 || ((System.Security.Cryptography.Xml.Reference)signedXml.SignedInfo.References[0]).Uri != string.Empty)
                throw new InvalidOperationException("Lenovo's catalog isn't signed as a whole, so Layer8 won't use it.");

            X509Certificate2 signer = null;
            foreach (KeyInfoClause clause in signedXml.KeyInfo)
            {
                if (signer == null && clause is KeyInfoX509Data data && data.Certificates != null && data.Certificates.Count > 0)
                    signer = new X509Certificate2((X509Certificate)data.Certificates[0]);
            }
            if (signer == null)
                throw new InvalidOperationException("Lenovo's catalog has no signing certificate, so Layer8 won't use it.");

            using (signer)
            {
                if (!signedXml.CheckSignature(signer, true))
                    throw new InvalidOperationException("Lenovo's catalog has been changed since Lenovo signed it, so Layer8 won't use it.");

                string name = signer.GetNameInfo(X509NameType.SimpleName, false);
                if (!Authenticode.LenovoSigner.IsMatch(name ?? string.Empty))
                    throw new InvalidOperationException(string.Format("Lenovo's catalog is signed by \"{0}\", not Lenovo, so Layer8 won't use it.", name));

                using (X509Chain chain = new X509Chain())
                {
                    // Lenovo signs the catalog with a code signing certificate. The XML signature carries no timestamp,
                    // so the certificate's dates aren't held against it; the same goes for revocation, as with the packs.
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
                    // The issuing CA Lenovo includes, so the chain builds without downloading it. It still has to lead
                    // to a root Windows trusts.
                    foreach (XmlNode issuer in signatures[0].SelectNodes(".//*[local-name()='IssuerCertificate']"))
                    {
                        try { chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(Convert.FromBase64String(issuer.InnerText.Trim()))); }
                        catch (FormatException) { }
                        catch (CryptographicException) { }
                    }

                    if (!chain.Build(signer))
                    {
                        string status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
                        throw new InvalidOperationException(string.Format("Lenovo's catalog is signed with a certificate Windows doesn't trust ({0}), so Layer8 won't use it.", status));
                    }
                }
            }
        }
    }
}
