using System.Drawing;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>A one-line question box (the console has no general-purpose one Layer8 can call).</summary>
    internal static class InputPrompt
    {
        /// <summary>The trimmed answer, or null when the user cancels or leaves it empty.</summary>
        public static string Ask(string title, string question, string initialAnswer)
        {
            using (Form form = new Form())
            {
                form.SuspendLayout();
                form.AutoScaleDimensions = new SizeF(6F, 13F);
                form.AutoScaleMode = AutoScaleMode.Font;
                form.Text = title;
                form.ClientSize = new Size(420, 108);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;

                Label label = new Label { Text = question, Location = new Point(12, 10), Size = new Size(396, 30) };
                TextBox answer = new TextBox { Text = initialAnswer ?? string.Empty, Location = new Point(12, 46), Size = new Size(396, 20) };
                Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(252, 76), Size = new Size(75, 23) };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(333, 76), Size = new Size(75, 23) };

                form.Controls.AddRange(new Control[] { label, answer, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                form.ResumeLayout(false);
                answer.SelectAll();

                if (form.ShowDialog() != DialogResult.OK) return null;
                string text = answer.Text.Trim();
                return text.Length == 0 ? null : text;
            }
        }
    }
}
