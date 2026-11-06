using Microsoft.ConfigurationManagement.AdminConsole;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Layer8
{
    /// <summary>
    /// The General page of Layer8 Settings: how many devices a run contacts at once, the quick offline check,
    /// and the log level. Saved per Windows user (see Layer8Settings); changes apply to the next action started.
    /// This page replaced Quick Tools' Auto Updates page, whose update check Layer8 turned off.
    /// </summary>
    public partial class OptionsControl : SmsPageControl
    {
        private static readonly Logger log = Layer8Log.GetLogger(typeof(OptionsControl));

        // Set while the page fills its controls, so filling them doesn't count as a change.
        private bool loading;

        public OptionsControl(SmsPageData pageData)
          : base(pageData)
        {
            InitializeComponent();

            Title = "General";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            loading = true;

            numericDevicesAtOnce.Minimum = Layer8Settings.MinDevicesAtOnce;
            numericDevicesAtOnce.Maximum = Layer8Settings.MaxDevicesAtOnce;
            numericDevicesAtOnce.Value = Layer8Settings.DevicesAtOnce;

            numericConfirmAbove.Minimum = Layer8Settings.MinConfirmAbove;
            numericConfirmAbove.Maximum = Layer8Settings.MaxConfirmAbove;
            numericConfirmAbove.Value = Layer8Settings.ConfirmAbove;

            checkReachable.Checked = Layer8Settings.CheckReachable;
            numericReachableSeconds.Minimum = Layer8Settings.MinReachableSeconds;
            numericReachableSeconds.Maximum = Layer8Settings.MaxReachableSeconds;
            numericReachableSeconds.Value = Layer8Settings.ReachableSeconds;

            comboLogLevel.Items.Clear();
            foreach (string level in Enum.GetNames(typeof(LogLevel)))
            {
                comboLogLevel.Items.Add(level);
            }
            comboLogLevel.SelectedItem = Layer8Settings.LogLevel.ToString();

            labelLogFile.Text = Layer8Log.LogFile;

            UpdateEnabled();

            loading = false;

            Dirty = false;

            Initialized = true;
        }

        protected override bool ApplyChanges(out Control errorControl, out bool showError)
        {
            try
            {
                Layer8Settings.DevicesAtOnce = (int)numericDevicesAtOnce.Value;
                Layer8Settings.ConfirmAbove = (int)numericConfirmAbove.Value;
                Layer8Settings.CheckReachable = checkReachable.Checked;
                Layer8Settings.ReachableSeconds = (int)numericReachableSeconds.Value;
                if (comboLogLevel.SelectedItem is string level && Enum.TryParse(level, out LogLevel parsed))
                {
                    Layer8Settings.LogLevel = parsed;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                log.Error("Couldn't save the settings", ex);
                MessageBox.Show(this, "Layer8 couldn't save its settings:\n\n" + ex.Message, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                errorControl = numericDevicesAtOnce;
                showError = false;
                return false;
            }

            log.InfoFormat("Settings saved: {0} devices at once; ask above {1}; offline check {2}, {3} s; log level {4}",
                (int)numericDevicesAtOnce.Value, (int)numericConfirmAbove.Value, checkReachable.Checked ? "on" : "off", (int)numericReachableSeconds.Value, comboLogLevel.SelectedItem);

            Dirty = false;

            return base.ApplyChanges(out errorControl, out showError);
        }

        private void UpdateEnabled()
        {
            labelReachableSeconds.Enabled = checkReachable.Checked;
            numericReachableSeconds.Enabled = checkReachable.Checked;
        }

        private void Setting_Changed(object sender, EventArgs e)
        {
            UpdateEnabled();
            if (!loading)
            {
                Dirty = true;
            }
        }

        private void ButtonOpenLogFolder_Click(object sender, EventArgs e)
        {
            SafeAction.Run("Open Log Folder", () =>
            {
                Directory.CreateDirectory(Layer8Log.LogFolder);
                using (Process.Start("explorer.exe", "\"" + Layer8Log.LogFolder + "\"")) { }
            });
        }
    }
}
