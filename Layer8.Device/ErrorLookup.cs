using System;
using System.Drawing;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Error Lookup on the Layer8 menus: type or paste an error code in any form (0x87D00324, -2016410844, 1603)
    /// and see what it means as you type, with Copy for a ticket or a chat.
    /// </summary>
    internal static class ErrorLookup
    {
        public static void Show(string initialCode)
        {
            using (Form form = new Form())
            {
                form.SuspendLayout();
                form.AutoScaleDimensions = new SizeF(6F, 13F);
                form.AutoScaleMode = AutoScaleMode.Font;
                form.Text = "Error Lookup";
                form.ClientSize = new Size(480, 196);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;

                Label prompt = new Label
                {
                    Text = "Error code, as hex (0x87D00324), decimal (1603) or negative (-2016410844):",
                    Location = new Point(12, 10),
                    Size = new Size(456, 16),
                };
                TextBox code = new TextBox { Text = initialCode ?? string.Empty, Location = new Point(12, 30), Size = new Size(456, 20) };
                TextBox meaning = new TextBox
                {
                    Location = new Point(12, 60),
                    Size = new Size(456, 94),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    TabStop = false,
                };
                Button copy = new Button { Text = "Copy", Location = new Point(312, 164), Size = new Size(75, 23) };
                Button close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(393, 164), Size = new Size(75, 23) };

                void Update()
                {
                    string text = code.Text.Trim();
                    if (text.Length == 0)
                    {
                        meaning.Text = string.Empty;
                    }
                    else if (ErrorCodes.TryParse(text, out uint value))
                    {
                        meaning.Text = ErrorCodes.Describe(value) + Environment.NewLine + Environment.NewLine +
                            string.Format("Hex 0x{0:X8}   Decimal {0}   Signed {1}", value, unchecked((int)value));
                    }
                    else
                    {
                        meaning.Text = "That isn't an error code.";
                    }
                    copy.Enabled = ErrorCodes.TryParse(text, out _);
                }

                code.TextChanged += (sender, e) => Update();
                copy.Click += (sender, e) =>
                {
                    if (ErrorCodes.TryParse(code.Text, out uint value)) Clipboard.SetText(ErrorCodes.FormatAndDescribe(value));
                };

                form.Controls.AddRange(new Control[] { prompt, code, meaning, copy, close });
                form.AcceptButton = copy;
                form.CancelButton = close;
                form.ResumeLayout(false);
                Update();
                code.SelectAll();
                form.ShowDialog();
            }
        }
    }
}
