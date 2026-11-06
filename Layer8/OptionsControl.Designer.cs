namespace Layer8
{
    partial class OptionsControl
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.groupBoxDevices = new System.Windows.Forms.GroupBox();
            this.labelDevicesAtOnce = new System.Windows.Forms.Label();
            this.numericDevicesAtOnce = new System.Windows.Forms.NumericUpDown();
            this.labelConfirmAbove = new System.Windows.Forms.Label();
            this.numericConfirmAbove = new System.Windows.Forms.NumericUpDown();
            this.checkReachable = new System.Windows.Forms.CheckBox();
            this.labelReachableSeconds = new System.Windows.Forms.Label();
            this.numericReachableSeconds = new System.Windows.Forms.NumericUpDown();
            this.labelReachableHelp = new System.Windows.Forms.Label();
            this.groupBoxLogging = new System.Windows.Forms.GroupBox();
            this.labelLogLevel = new System.Windows.Forms.Label();
            this.comboLogLevel = new System.Windows.Forms.ComboBox();
            this.labelLogFileCaption = new System.Windows.Forms.Label();
            this.labelLogFile = new System.Windows.Forms.Label();
            this.buttonOpenLogFolder = new System.Windows.Forms.Button();
            this.labelSavedFor = new System.Windows.Forms.Label();
            this.groupBoxDevices.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericDevicesAtOnce)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numericConfirmAbove)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numericReachableSeconds)).BeginInit();
            this.groupBoxLogging.SuspendLayout();
            this.SuspendLayout();
            //
            // groupBoxDevices
            //
            this.groupBoxDevices.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.groupBoxDevices.Controls.Add(this.labelDevicesAtOnce);
            this.groupBoxDevices.Controls.Add(this.numericDevicesAtOnce);
            this.groupBoxDevices.Controls.Add(this.labelConfirmAbove);
            this.groupBoxDevices.Controls.Add(this.numericConfirmAbove);
            this.groupBoxDevices.Controls.Add(this.checkReachable);
            this.groupBoxDevices.Controls.Add(this.labelReachableSeconds);
            this.groupBoxDevices.Controls.Add(this.numericReachableSeconds);
            this.groupBoxDevices.Controls.Add(this.labelReachableHelp);
            this.groupBoxDevices.Location = new System.Drawing.Point(10, 10);
            this.groupBoxDevices.Name = "groupBoxDevices";
            this.groupBoxDevices.Size = new System.Drawing.Size(360, 190);
            this.groupBoxDevices.TabIndex = 0;
            this.groupBoxDevices.TabStop = false;
            this.groupBoxDevices.Text = "Actions on many devices";
            //
            // labelDevicesAtOnce
            //
            this.labelDevicesAtOnce.AutoSize = true;
            this.labelDevicesAtOnce.Location = new System.Drawing.Point(12, 26);
            this.labelDevicesAtOnce.Name = "labelDevicesAtOnce";
            this.labelDevicesAtOnce.Size = new System.Drawing.Size(140, 13);
            this.labelDevicesAtOnce.TabIndex = 0;
            this.labelDevicesAtOnce.Text = "Devices contacted at once:";
            //
            // numericDevicesAtOnce
            //
            this.numericDevicesAtOnce.Location = new System.Drawing.Point(270, 24);
            this.numericDevicesAtOnce.Name = "numericDevicesAtOnce";
            this.numericDevicesAtOnce.Size = new System.Drawing.Size(70, 20);
            this.numericDevicesAtOnce.TabIndex = 1;
            this.numericDevicesAtOnce.ValueChanged += new System.EventHandler(this.Setting_Changed);
            // 
            // labelConfirmAbove
            // 
            this.labelConfirmAbove.AutoSize = true;
            this.labelConfirmAbove.Location = new System.Drawing.Point(12, 56);
            this.labelConfirmAbove.Name = "labelConfirmAbove";
            this.labelConfirmAbove.Size = new System.Drawing.Size(220, 13);
            this.labelConfirmAbove.TabIndex = 6;
            this.labelConfirmAbove.Text = "Ask before running on more devices than:";
            // 
            // numericConfirmAbove
            // 
            this.numericConfirmAbove.Location = new System.Drawing.Point(270, 54);
            this.numericConfirmAbove.Name = "numericConfirmAbove";
            this.numericConfirmAbove.Size = new System.Drawing.Size(70, 20);
            this.numericConfirmAbove.TabIndex = 7;
            this.numericConfirmAbove.ValueChanged += new System.EventHandler(this.Setting_Changed);
            //
            // checkReachable
            //
            this.checkReachable.AutoSize = true;
            this.checkReachable.Location = new System.Drawing.Point(15, 86);
            this.checkReachable.Name = "checkReachable";
            this.checkReachable.Size = new System.Drawing.Size(270, 17);
            this.checkReachable.TabIndex = 2;
            this.checkReachable.Text = "Check that each device answers before connecting";
            this.checkReachable.UseVisualStyleBackColor = true;
            this.checkReachable.CheckedChanged += new System.EventHandler(this.Setting_Changed);
            //
            // labelReachableSeconds
            //
            this.labelReachableSeconds.AutoSize = true;
            this.labelReachableSeconds.Location = new System.Drawing.Point(32, 114);
            this.labelReachableSeconds.Name = "labelReachableSeconds";
            this.labelReachableSeconds.Size = new System.Drawing.Size(150, 13);
            this.labelReachableSeconds.TabIndex = 3;
            this.labelReachableSeconds.Text = "Seconds to wait for an answer:";
            //
            // numericReachableSeconds
            //
            this.numericReachableSeconds.Location = new System.Drawing.Point(270, 112);
            this.numericReachableSeconds.Name = "numericReachableSeconds";
            this.numericReachableSeconds.Size = new System.Drawing.Size(70, 20);
            this.numericReachableSeconds.TabIndex = 4;
            this.numericReachableSeconds.ValueChanged += new System.EventHandler(this.Setting_Changed);
            //
            // labelReachableHelp
            //
            this.labelReachableHelp.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.labelReachableHelp.Location = new System.Drawing.Point(12, 142);
            this.labelReachableHelp.Name = "labelReachableHelp";
            this.labelReachableHelp.Size = new System.Drawing.Size(336, 40);
            this.labelReachableHelp.TabIndex = 5;
            this.labelReachableHelp.Text = "A device that answers on neither port 135 (WMI) nor 445 (file sharing) is listed as offline right away, instead of after a WMI connection gives up (20 seconds or more).";
            //
            // groupBoxLogging
            //
            this.groupBoxLogging.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.groupBoxLogging.Controls.Add(this.labelLogLevel);
            this.groupBoxLogging.Controls.Add(this.comboLogLevel);
            this.groupBoxLogging.Controls.Add(this.labelLogFileCaption);
            this.groupBoxLogging.Controls.Add(this.labelLogFile);
            this.groupBoxLogging.Controls.Add(this.buttonOpenLogFolder);
            this.groupBoxLogging.Location = new System.Drawing.Point(10, 210);
            this.groupBoxLogging.Name = "groupBoxLogging";
            this.groupBoxLogging.Size = new System.Drawing.Size(360, 130);
            this.groupBoxLogging.TabIndex = 1;
            this.groupBoxLogging.TabStop = false;
            this.groupBoxLogging.Text = "Logging";
            //
            // labelLogLevel
            //
            this.labelLogLevel.AutoSize = true;
            this.labelLogLevel.Location = new System.Drawing.Point(12, 26);
            this.labelLogLevel.Name = "labelLogLevel";
            this.labelLogLevel.Size = new System.Drawing.Size(53, 13);
            this.labelLogLevel.TabIndex = 0;
            this.labelLogLevel.Text = "Log level:";
            //
            // comboLogLevel
            //
            this.comboLogLevel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboLogLevel.FormattingEnabled = true;
            this.comboLogLevel.Location = new System.Drawing.Point(240, 23);
            this.comboLogLevel.Name = "comboLogLevel";
            this.comboLogLevel.Size = new System.Drawing.Size(100, 21);
            this.comboLogLevel.TabIndex = 1;
            this.comboLogLevel.SelectedIndexChanged += new System.EventHandler(this.Setting_Changed);
            //
            // labelLogFileCaption
            //
            this.labelLogFileCaption.AutoSize = true;
            this.labelLogFileCaption.Location = new System.Drawing.Point(12, 56);
            this.labelLogFileCaption.Name = "labelLogFileCaption";
            this.labelLogFileCaption.Size = new System.Drawing.Size(44, 13);
            this.labelLogFileCaption.TabIndex = 2;
            this.labelLogFileCaption.Text = "Log file:";
            //
            // labelLogFile
            //
            this.labelLogFile.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.labelLogFile.AutoEllipsis = true;
            this.labelLogFile.Location = new System.Drawing.Point(12, 72);
            this.labelLogFile.Name = "labelLogFile";
            this.labelLogFile.Size = new System.Drawing.Size(336, 13);
            this.labelLogFile.TabIndex = 3;
            //
            // buttonOpenLogFolder
            //
            this.buttonOpenLogFolder.AutoSize = true;
            this.buttonOpenLogFolder.Location = new System.Drawing.Point(12, 94);
            this.buttonOpenLogFolder.Name = "buttonOpenLogFolder";
            this.buttonOpenLogFolder.Size = new System.Drawing.Size(110, 23);
            this.buttonOpenLogFolder.TabIndex = 4;
            this.buttonOpenLogFolder.Text = "Open Log Folder";
            this.buttonOpenLogFolder.UseVisualStyleBackColor = true;
            this.buttonOpenLogFolder.Click += new System.EventHandler(this.ButtonOpenLogFolder_Click);
            //
            // labelSavedFor
            //
            this.labelSavedFor.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.labelSavedFor.Location = new System.Drawing.Point(10, 352);
            this.labelSavedFor.Name = "labelSavedFor";
            this.labelSavedFor.Size = new System.Drawing.Size(360, 30);
            this.labelSavedFor.TabIndex = 2;
            this.labelSavedFor.Text = "Saved for your Windows account. Changes apply to the next action you start. 0 devices means never ask.";
            //
            // OptionsControl
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.groupBoxDevices);
            this.Controls.Add(this.groupBoxLogging);
            this.Controls.Add(this.labelSavedFor);
            this.Name = "OptionsControl";
            this.Size = new System.Drawing.Size(380, 450);
            this.groupBoxDevices.ResumeLayout(false);
            this.groupBoxDevices.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericDevicesAtOnce)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numericConfirmAbove)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numericReachableSeconds)).EndInit();
            this.groupBoxLogging.ResumeLayout(false);
            this.groupBoxLogging.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.GroupBox groupBoxDevices;
        private System.Windows.Forms.Label labelDevicesAtOnce;
        private System.Windows.Forms.NumericUpDown numericDevicesAtOnce;
        private System.Windows.Forms.Label labelConfirmAbove;
        private System.Windows.Forms.NumericUpDown numericConfirmAbove;
        private System.Windows.Forms.CheckBox checkReachable;
        private System.Windows.Forms.Label labelReachableSeconds;
        private System.Windows.Forms.NumericUpDown numericReachableSeconds;
        private System.Windows.Forms.Label labelReachableHelp;
        private System.Windows.Forms.GroupBox groupBoxLogging;
        private System.Windows.Forms.Label labelLogLevel;
        private System.Windows.Forms.ComboBox comboLogLevel;
        private System.Windows.Forms.Label labelLogFileCaption;
        private System.Windows.Forms.Label labelLogFile;
        private System.Windows.Forms.Button buttonOpenLogFolder;
        private System.Windows.Forms.Label labelSavedFor;
    }
}
