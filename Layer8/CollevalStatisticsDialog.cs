using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System.Windows.Forms;
using System;
using System.Management;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

namespace Layer8
{
    public partial class CollevalStatisticsDialog : SmsCustomDialog
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private BackgroundWorker backgroundWorker;
        private readonly ConnectionManagerBase connectionManager;

        public CollevalStatisticsDialog(ActionDescription action, ScopeNode scopeNode)
        {
            InitializeComponent();

            Title = action.DisplayName;

            Updater.CheckUpdates();

            connectionManager = Microsoft.ConfigurationManagement.AdminConsole.UtilityClass.ConnectionManagerFromScope(scopeNode, "WQL");

            progressBar.Style = ProgressBarStyle.Marquee;

            listViewListCollections.UpdateColumnWidth(columnHeaderName);
            listViewListCollections.Items.Clear();
            listViewListCollections.IsLoading = true;
        }

        /// <summary>Console entry point: runs ShowCore behind Layer8's crash guard.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            SafeAction.Run(action?.DisplayName ?? "Show", () => ShowCore(sender, scopeNode, action, selectedObject, dataUpdatedDelegate, Status));
        }

        private static void ShowCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            using (CollevalStatisticsDialog dialog = new CollevalStatisticsDialog(action, scopeNode))
            {
                dialog.ShowDialog();
                return;
            }
        }

        private void CollevalStatisticsDialog_Shown(object sender, EventArgs e)
        {
            backgroundWorker = new BackgroundWorker();
            backgroundWorker.DoWork += new DoWorkEventHandler(InfoWorker_DoWork);
            backgroundWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(InfoWorker_RunWorkerCompleted);
            backgroundWorker.WorkerSupportsCancellation = false;
            backgroundWorker.WorkerReportsProgress = false;
            UseWaitCursor = true;
            backgroundWorker.RunWorkerAsync();
        }

        // What the background read found. The worker only collects; the UI thread fills the window, because
        // WinForms controls may only be changed from the thread that created them.
        private sealed class EvaluationStats
        {
            public DateTime StartTime;
            public DateTime EndTime;
            public int CollectionCount;
            public readonly List<ListViewItem> Rows = new List<ListViewItem>();
            public readonly List<double> Seconds = new List<double>();
        }

        private void InfoWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                string server = connectionManager.NamedValueDictionary["ServerName"].ToString();
                DateTime currentTime = DateTime.Now.AddMinutes(-5);

                ManagementObject env = Utility.GetFirstWMIInstance(server, @"cimv2", "Win32_Environment WHERE Name = 'SMS_LOG_PATH'");

                string logPath = env["VariableValue"].ToString();
                string source = string.Format(@"\\{0}\{1}\colleval.log", server, logPath.Replace(":", "$"));

                string input = ReadInput(source);

                Regex regex = new Regex(@"\[Express Evaluator\] Starting.*(\d.-\d.-\d{4} \d.:\d.:\d.)", RegexOptions.RightToLeft);
                Match startMatch = regex.Match(input);
                regex = new Regex(@"\[Express Evaluator\] Exiting.*(\d.-\d.-\d{4} \d.:\d.:\d.)", RegexOptions.RightToLeft);
                Match endMatch = regex.Match(input);

                if (startMatch.Success && endMatch.Success)
                {
                    CultureInfo provider = CultureInfo.InvariantCulture;
                    DateTime startTime = DateTime.ParseExact(startMatch.Groups[1].Value, "MM-dd-yyyy HH:mm:ss", provider);
                    DateTime endTime = DateTime.ParseExact(endMatch.Groups[1].Value, "MM-dd-yyyy HH:mm:ss", provider);
                    if (endTime.CompareTo(startTime) > 0)
                    {
                        // get collection count
                        regex = new Regex(@"PF: Found\s(\d.*)\scandidate collections for incremental evaluation.*(\d.-\d.-\d{4}\s\d.:\d.:\d.)", RegexOptions.RightToLeft);
                        Match match = regex.Match(input);                      

                        int collectionCount = match.Success ? Convert.ToInt32(match.Groups[1].Value) : 0;
                        // get evaluated collections
                        regex = new Regex(@"\[Express Evaluator] successfully evaluated collection.*\[(.*)\].*used\s(.*)\sseconds.*(\d.-\d.-\d{4} \d.:\d.:\d.)");
                        MatchCollection matches = regex.Matches(input);

                        List<IResultObject> collections = Utility.SearchWMIToList(connectionManager, "SELECT CollectionID,Name FROM SMS_Collection");
                        EvaluationStats stats = new EvaluationStats { StartTime = startTime, EndTime = endTime, CollectionCount = collectionCount };

                        foreach (Match obj in matches)
                        {
                            DateTime collectionTime = DateTime.ParseExact(obj.Groups[3].Value, "MM-dd-yyyy HH:mm:ss", provider);

                            if (Utility.InclusiveBetween(collectionTime, startTime, endTime))
                            {
                                string collectionId = obj.Groups[1].Value;
                                IResultObject collection = collections.FirstOrDefault(x => x.Properties["CollectionID"].StringValue.Equals(collectionId));
                                stats.Rows.Add(new ListViewItem()
                                {
                                    // A collection deleted since the log was written has no name any more: show its ID.
                                    Text = collection != null ? collection["Name"].StringValue : collectionId,
                                    SubItems = {
                                        obj.Groups[2].Value,
                                        collectionId
                                    }
                                });
                                if (double.TryParse(obj.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                                    stats.Seconds.Add(seconds);
                            }
                        }

                        e.Result = stats;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(string.Format("{0}: {1}", ex.GetType().Name, ex.Message));
            }
        }

        private void InfoWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                    SccmExceptionDialog.ShowDialog(this, e.Error, "Error");
                else if (e.Result is EvaluationStats stats)
                    ShowStats(stats);
            }
            finally
            {
                if (sender as BackgroundWorker == backgroundWorker)
                {
                    backgroundWorker.Dispose();
                    backgroundWorker = null;
                    UseWaitCursor = false;
                    progressBar.Visible = false;
                    listViewListCollections.IsLoading = false;
                    listViewListCollections.UpdateColumnWidth(columnHeaderName);
                }
            }
        }

        private void ShowStats(EvaluationStats stats)
        {
            listViewListCollections.Items.AddRange(stats.Rows.ToArray());

            labelStartTime.Text = stats.StartTime.ToString();
            labelEndTime.Text = stats.EndTime.ToString();
            labelTimeTaken.Text = (stats.EndTime - stats.StartTime).ToString(@"mm\:ss");
            labelCollectionCount.Text = stats.CollectionCount.ToString();

            // A run that evaluated nothing has no average, longest or shortest.
            if (stats.Seconds.Count == 0)
            {
                labelCollAverageTime.Text = labelCollLongTime.Text = labelCollShortTime.Text = "-";
                labelCollTime.Text = TimeSpan.Zero.ToString(@"mm\:ss\.fff");
                return;
            }
            labelCollAverageTime.Text = Math.Round(stats.Seconds.Average(), 3).ToString() + " sec";
            labelCollTime.Text = TimeSpan.FromSeconds(stats.Seconds.Sum()).ToString(@"mm\:ss\.fff");
            labelCollLongTime.Text = Math.Round(stats.Seconds.Max(), 3).ToString() + " sec";
            labelCollShortTime.Text = Math.Round(stats.Seconds.Min(), 3).ToString() + " sec";
        }

        private static string ReadInput(string fileName)
        {
            string output;
            using (StreamReader reader = new StreamReader(fileName))
            {
                output = reader.ReadToEnd();
            }
            return output;
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void ListView_CopyKeyEvent(object sender, EventArgs e)
        {
            Utility.CopyToClipboard((ListView)sender);
        }
    }
}
