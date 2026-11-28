using Microsoft.ConfigurationManagement.AdminConsole;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Layer8
{
    /// <summary>
    /// The AD Cleanup Report page of Layer8 Settings: after how many days a computer counts as unused in AD, and
    /// a client as silent; and the OU its Move to OU offers first. Saved per Windows user like the other pages (see
    /// Layer8Settings); a report opened after a change uses the new value. Laid out in code to match the Health
    /// Dashboard page.
    /// </summary>
    public class ADCleanupOptionsControl : SmsPageControl
    {
        private static readonly Logger log = Layer8Log.GetLogger(typeof(ADCleanupOptionsControl));

        private readonly NumericUpDown numericNotSeenDays;
        private readonly TextBox textMoveTo;

        // Set while the page fills its controls, so filling them doesn't count as a change.
        private bool loading;

        public ADCleanupOptionsControl(SmsPageData pageData)
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
                Size = new Size(360, 136),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 0,
            };
            Label labelNotSeen = new Label { Text = "Not seen by AD or by the client in (days):", AutoSize = true, Location = new Point(12, 26), TabIndex = 0 };
            numericNotSeenDays = new NumericUpDown { Location = new Point(270, 24), Size = new Size(70, 20), TabIndex = 1 };
            Label labelTilesHelp = new Label
            {
                Text = "90 is ConfigMgr's own default for AD System Discovery's logon filter and for Delete Aged Discovery Data. Under 45, computers in use can show as stale: AD rewrites a computer's last logon only every 9 to 14 days, and a computer changes its password every 30. Hover over a tile in the report for its rule.",
                Location = new Point(12, 54),
                Size = new Size(336, 72),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 2,
            };
            tiles.Controls.AddRange(new Control[] { labelNotSeen, numericNotSeenDays, labelTilesHelp });

            GroupBox fixes = new GroupBox
            {
                Text = "Fixes",
                Location = new Point(10, 156),
                Size = new Size(360, 128),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 1,
            };
            Label labelMoveTo = new Label { Text = "Move to OU offers first:", AutoSize = true, Location = new Point(12, 26), TabIndex = 0 };
            textMoveTo = new TextBox
            {
                Location = new Point(12, 44),
                Size = new Size(336, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 1,
            };
            Label labelFixesHelp = new Label
            {
                Text = "The last OU a move used, or one typed here as a distinguished name (OU=Disabled,DC=contoso,DC=com). Empty for none. Disable, Enable, Move to OU and Delete each ask before changing anything in AD.",
                Location = new Point(12, 70),
                Size = new Size(336, 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 2,
            };
            fixes.Controls.AddRange(new Control[] { labelMoveTo, textMoveTo, labelFixesHelp });

            Label labelSavedFor = new Label
            {
                Text = "Saved for your Windows account. A report opened after a change uses the new value.",
                Location = new Point(10, 296),
                Size = new Size(360, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 2,
            };

            Controls.Add(tiles);
            Controls.Add(fixes);
            Controls.Add(labelSavedFor);
            numericNotSeenDays.ValueChanged += Setting_Changed;
            textMoveTo.TextChanged += Setting_Changed;
            ResumeLayout(false);

            Title = "AD Cleanup Report";
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            loading = true;

            numericNotSeenDays.Minimum = Layer8Settings.MinADNotSeenDays;
            numericNotSeenDays.Maximum = Layer8Settings.MaxADNotSeenDays;
            numericNotSeenDays.Value = Layer8Settings.ADNotSeenDays;
            textMoveTo.Text = Layer8Settings.ADMoveTo ?? string.Empty;

            // The console's property sheet only knows controls added here, and marks the one ApplyChanges names when a save fails.
            ControlsInspector.AddControl(numericNotSeenDays, new ControlDataStateEvaluator(() => ControlDataState.Valid), "Layer8 couldn't save its settings");
            ControlsInspector.AddControl(textMoveTo, new ControlDataStateEvaluator(() => ControlDataState.Valid), "Layer8 couldn't save its settings");

            loading = false;

            Dirty = false;

            Initialized = true;
        }

        protected override bool ApplyChanges(out Control errorControl, out bool showError)
        {
            try
            {
                Layer8Settings.ADNotSeenDays = (int)numericNotSeenDays.Value;
                Layer8Settings.ADMoveTo = textMoveTo.Text;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                log.Error("Couldn't save the AD Cleanup Report settings", ex);
                MessageBox.Show(this, "Layer8 couldn't save its settings:\n\n" + ex.Message, "Layer8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                errorControl = numericNotSeenDays;
                showError = false;
                return false;
            }

            log.InfoFormat("AD Cleanup Report settings saved: not seen in {0}; move to OU {1}", Utility.Days((int)numericNotSeenDays.Value),
                           string.IsNullOrWhiteSpace(textMoveTo.Text) ? "(none)" : textMoveTo.Text.Trim());

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
