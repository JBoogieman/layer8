using Microsoft.ConfigurationManagement.AdminConsole;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Layer8
{
    /// <summary>
    /// The Health Dashboard page of Layer8 Settings: when a device counts as low on disk, as up too long, as not
    /// reporting to the site, as having had a recent blue screen, as not scanning for updates, and as behind on
    /// Windows. Saved
    /// per Windows user like the General page (see Layer8Settings); a dashboard opened after a change uses the new
    /// values. Laid out in code to match the General page: the same column for the number boxes, the same note at
    /// the bottom.
    /// </summary>
    public class HealthDashboardOptionsControl : SmsPageControl
    {
        private static readonly Logger log = Layer8Log.GetLogger(typeof(HealthDashboardOptionsControl));

        private readonly NumericUpDown numericLowDiskGB;
        private readonly NumericUpDown numericLongUptimeDays;
        private readonly NumericUpDown numericNotReportingDays;
        private readonly NumericUpDown numericBlueScreenDays;
        private readonly NumericUpDown numericNotScanningDays;
        private readonly TextBox textWindowsBuild;

        // Set while the page fills its controls, so filling them doesn't count as a change.
        private bool loading;

        public HealthDashboardOptionsControl(SmsPageData pageData)
          : base(pageData)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            Size = new Size(380, 450);

            GroupBox tiles = new GroupBox
            {
                Text = "Tiles",
                Location = new Point(10, 10),
                Size = new Size(360, 256),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 0,
            };
            Label labelLowDisk = new Label { Text = "Low disk: less free space on C: than (GB):", AutoSize = true, Location = new Point(12, 26), TabIndex = 0 };
            numericLowDiskGB = new NumericUpDown { Location = new Point(270, 24), Size = new Size(70, 20), TabIndex = 1 };
            Label labelLongUptime = new Label { Text = "Long uptime: up this many days or more:", AutoSize = true, Location = new Point(12, 56), TabIndex = 2 };
            numericLongUptimeDays = new NumericUpDown { Location = new Point(270, 54), Size = new Size(70, 20), TabIndex = 3 };
            Label labelNotReporting = new Label { Text = "Not reporting: no policy request in (days):", AutoSize = true, Location = new Point(12, 86), TabIndex = 4 };
            numericNotReportingDays = new NumericUpDown { Location = new Point(270, 84), Size = new Size(70, 20), TabIndex = 5 };
            Label labelBlueScreen = new Label { Text = "Blue screens: a crash dump in (days):", AutoSize = true, Location = new Point(12, 116), TabIndex = 6 };
            numericBlueScreenDays = new NumericUpDown { Location = new Point(270, 114), Size = new Size(70, 20), TabIndex = 7 };
            Label labelNotScanning = new Label { Text = "Not scanning: no update scan in (days):", AutoSize = true, Location = new Point(12, 146), TabIndex = 8 };
            numericNotScanningDays = new NumericUpDown { Location = new Point(270, 144), Size = new Size(70, 20), TabIndex = 9 };
            Label labelWindowsBuild = new Label { Text = "Windows behind: older than build:", AutoSize = true, Location = new Point(12, 176), TabIndex = 10 };
            textWindowsBuild = new TextBox { Location = new Point(270, 174), Size = new Size(70, 20), MaxLength = 12, TabIndex = 11 };
            Label labelTilesHelp = new Label
            {
                Text = "A build as winver shows it: 26100, or 26100.4652 to count the monthly update too. Servers aren't counted. The other tiles follow Windows and ConfigMgr themselves. Hover over a tile in the dashboard for its rule.",
                Location = new Point(12, 204),
                Size = new Size(336, 44),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 12,
            };
            tiles.Controls.AddRange(new Control[] { labelLowDisk, numericLowDiskGB, labelLongUptime, numericLongUptimeDays, labelNotReporting, numericNotReportingDays, labelBlueScreen, numericBlueScreenDays, labelNotScanning, numericNotScanningDays, labelWindowsBuild, textWindowsBuild, labelTilesHelp });

            Label labelSavedFor = new Label
            {
                Text = "Saved for your Windows account. A dashboard opened after a change uses the new values.",
                Location = new Point(10, 278),
                Size = new Size(360, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 1,
            };

            Controls.Add(tiles);
            Controls.Add(labelSavedFor);
            numericLowDiskGB.ValueChanged += Setting_Changed;
            numericLongUptimeDays.ValueChanged += Setting_Changed;
            numericNotReportingDays.ValueChanged += Setting_Changed;
            numericBlueScreenDays.ValueChanged += Setting_Changed;
            numericNotScanningDays.ValueChanged += Setting_Changed;
            textWindowsBuild.TextChanged += Setting_Changed;
            textWindowsBuild.TextChanged += WindowsBuild_Changed;
            ResumeLayout(false);

            Title = "Health Dashboard";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            loading = true;

            numericLowDiskGB.Minimum = Layer8Settings.MinLowDiskGB;
            numericLowDiskGB.Maximum = Layer8Settings.MaxLowDiskGB;
            numericLowDiskGB.Value = Layer8Settings.LowDiskGB;

            numericLongUptimeDays.Minimum = Layer8Settings.MinLongUptimeDays;
            numericLongUptimeDays.Maximum = Layer8Settings.MaxLongUptimeDays;
            numericLongUptimeDays.Value = Layer8Settings.LongUptimeDays;

            numericNotReportingDays.Minimum = Layer8Settings.MinNotReportingDays;
            numericNotReportingDays.Maximum = Layer8Settings.MaxNotReportingDays;
            numericNotReportingDays.Value = Layer8Settings.NotReportingDays;

            numericBlueScreenDays.Minimum = Layer8Settings.MinBlueScreenDays;
            numericBlueScreenDays.Maximum = Layer8Settings.MaxBlueScreenDays;
            numericBlueScreenDays.Value = Layer8Settings.BlueScreenDays;

            numericNotScanningDays.Minimum = Layer8Settings.MinNotScanningDays;
            numericNotScanningDays.Maximum = Layer8Settings.MaxNotScanningDays;
            numericNotScanningDays.Value = Layer8Settings.NotScanningDays;

            textWindowsBuild.Text = Layer8Settings.WindowsBuild;

            // When a save fails, the console's property sheet marks the control ApplyChanges names, and it only knows
            // controls added here: naming any other one threw "The given key was not present in the dictionary".
            ControlsInspector.AddControl(textWindowsBuild, new ControlDataStateEvaluator(ValidateWindowsBuild), WindowsBuildHelp);
            ControlsInspector.AddControl(numericLowDiskGB, new ControlDataStateEvaluator(() => ControlDataState.Valid), "Layer8 couldn't save its settings");

            loading = false;

            Dirty = false;

            Initialized = true;
        }

        protected override bool ApplyChanges(out Control errorControl, out bool showError)
        {
            if (!Layer8Settings.TryParseWindowsBuild(textWindowsBuild.Text, out _))
            {
                MessageBox.Show(this, WindowsBuildHelp, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textWindowsBuild.Focus();
                textWindowsBuild.SelectAll();
                errorControl = textWindowsBuild;
                showError = false;
                return false;
            }

            try
            {
                Layer8Settings.LowDiskGB = (int)numericLowDiskGB.Value;
                Layer8Settings.LongUptimeDays = (int)numericLongUptimeDays.Value;
                Layer8Settings.NotReportingDays = (int)numericNotReportingDays.Value;
                Layer8Settings.BlueScreenDays = (int)numericBlueScreenDays.Value;
                Layer8Settings.NotScanningDays = (int)numericNotScanningDays.Value;
                Layer8Settings.WindowsBuild = textWindowsBuild.Text;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                log.Error("Couldn't save the Health Dashboard settings", ex);
                MessageBox.Show(this, "Layer8 couldn't save its settings:\n\n" + ex.Message, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                errorControl = numericLowDiskGB;
                showError = false;
                return false;
            }

            log.InfoFormat("Health Dashboard settings saved: low disk under {0} GB; long uptime {1} days or more; not reporting after {2} days; blue screens in the last {3} days; not scanning after {4} days; Windows older than build {5}",
                (int)numericLowDiskGB.Value, (int)numericLongUptimeDays.Value, (int)numericNotReportingDays.Value, (int)numericBlueScreenDays.Value, (int)numericNotScanningDays.Value, textWindowsBuild.Text.Trim());

            Dirty = false;

            return base.ApplyChanges(out errorControl, out showError);
        }

        private const string WindowsBuildHelp = "Enter a Windows build as winver shows it: 26100, or 26100.4652 to count the monthly update too.";

        private ControlDataState ValidateWindowsBuild()
        {
            return Layer8Settings.TryParseWindowsBuild(textWindowsBuild.Text, out _) ? ControlDataState.Valid : ControlDataState.Invalid;
        }

        private void Setting_Changed(object sender, EventArgs e)
        {
            if (!loading)
            {
                Dirty = true;
            }
        }

        private void WindowsBuild_Changed(object sender, EventArgs e)
        {
            if (!loading)
            {
                ControlsInspector.InspectAll();
            }
        }
    }
}
