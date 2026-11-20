using Microsoft.ConfigurationManagement.AdminConsole;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Layer8
{
    /// <summary>
    /// The Health Dashboard page of Layer8 Settings: when a device counts as low on disk, and as up too long. Saved
    /// per Windows user like the General page (see Layer8Settings); a dashboard opened after a change uses the new
    /// values. Laid out in code to match the General page: the same column for the number boxes, the same note at
    /// the bottom.
    /// </summary>
    public class HealthDashboardOptionsControl : SmsPageControl
    {
        private static readonly Logger log = Layer8Log.GetLogger(typeof(HealthDashboardOptionsControl));

        private readonly NumericUpDown numericLowDiskGB;
        private readonly NumericUpDown numericLongUptimeDays;

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
                Size = new Size(360, 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 0,
            };
            Label labelLowDisk = new Label { Text = "Low disk: less free space on C: than (GB):", AutoSize = true, Location = new Point(12, 26), TabIndex = 0 };
            numericLowDiskGB = new NumericUpDown { Location = new Point(270, 24), Size = new Size(70, 20), TabIndex = 1 };
            Label labelLongUptime = new Label { Text = "Long uptime: up this many days or more:", AutoSize = true, Location = new Point(12, 56), TabIndex = 2 };
            numericLongUptimeDays = new NumericUpDown { Location = new Point(270, 54), Size = new Size(70, 20), TabIndex = 3 };
            Label labelTilesHelp = new Label
            {
                Text = "The other tiles follow Windows and ConfigMgr themselves. Hover over a tile in the dashboard for its rule.",
                Location = new Point(12, 84),
                Size = new Size(336, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 4,
            };
            tiles.Controls.AddRange(new Control[] { labelLowDisk, numericLowDiskGB, labelLongUptime, numericLongUptimeDays, labelTilesHelp });

            Label labelSavedFor = new Label
            {
                Text = "Saved for your Windows account. A dashboard opened after a change uses the new values.",
                Location = new Point(10, 142),
                Size = new Size(360, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 1,
            };

            Controls.Add(tiles);
            Controls.Add(labelSavedFor);
            numericLowDiskGB.ValueChanged += Setting_Changed;
            numericLongUptimeDays.ValueChanged += Setting_Changed;
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

            loading = false;

            Dirty = false;

            Initialized = true;
        }

        protected override bool ApplyChanges(out Control errorControl, out bool showError)
        {
            try
            {
                Layer8Settings.LowDiskGB = (int)numericLowDiskGB.Value;
                Layer8Settings.LongUptimeDays = (int)numericLongUptimeDays.Value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                log.Error("Couldn't save the Health Dashboard settings", ex);
                MessageBox.Show(this, "Layer8 couldn't save its settings:\n\n" + ex.Message, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                errorControl = numericLowDiskGB;
                showError = false;
                return false;
            }

            log.InfoFormat("Health Dashboard settings saved: low disk under {0} GB; long uptime {1} days or more",
                (int)numericLowDiskGB.Value, (int)numericLongUptimeDays.Value);

            Dirty = false;

            return base.ApplyChanges(out errorControl, out showError);
        }

        private void Setting_Changed(object sender, EventArgs e)
        {
            if (!loading)
            {
                Dirty = true;
            }
        }
    }
}
