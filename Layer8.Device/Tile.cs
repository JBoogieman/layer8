using System;
using System.Drawing;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>A tile's colour while it's lit. Neutral is always lit; the others only while their count isn't zero.</summary>
    internal enum Tone { Neutral, Good, Muted, Warn, Bad, Info }

    /// <summary>
    /// A count with its name, clicked to show just those rows. Coloured only while its count isn't zero. The Health
    /// Dashboard and the AD Cleanup Report both use it; <typeparamref name="TRow"/> is the window's own row, which
    /// <see cref="Match"/> says is on the tile or not.
    /// </summary>
    internal class Tile<TRow> : Control
    {
        private int count;
        private bool selected;
        private Font numberFont;

        public Tile(string caption, Tone tone, Func<TRow, bool> match)
        {
            Caption = caption;
            Tone = tone;
            Match = match;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            // Two quick clicks are two clicks (show, then show all again), not a double-click.
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(96, 50);
            Margin = new Padding(0, 0, 6, 6);
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = caption + ": 0";
        }

        public string Caption { get; }
        public Tone Tone { get; }
        public Func<TRow, bool> Match { get; }

        /// <summary>Its rule, for the hover text; the window adds the last run's count to it.</summary>
        public string Tip { get; set; }

        public int Count
        {
            get { return count; }
            set
            {
                if (count == value) return;
                count = value;
                UpdateAccessibleName();
                Invalidate();
            }
        }

        private int? previous;
        /// <summary>Its count in the run the window compares with; null with nothing to compare with.</summary>
        public int? Previous
        {
            get { return previous; }
            set
            {
                if (previous == value) return;
                previous = value;
                UpdateAccessibleName();
                Invalidate();
            }
        }

        private bool showDelta;
        /// <summary>Show the change from Previous beside the count: once a pass has ended, not while counts climb.</summary>
        public bool ShowDelta
        {
            get { return showDelta; }
            set
            {
                if (showDelta == value) return;
                showDelta = value;
                Invalidate();
            }
        }

        private void UpdateAccessibleName()
        {
            AccessibleName = Caption + ": " + count + (previous.HasValue && previous.Value != count ? ", was " + previous.Value : string.Empty);
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnFontChanged(EventArgs e)
        {
            numberFont?.Dispose();
            numberFont = null;
            base.OnFontChanged(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) numberFont?.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (numberFont == null) numberFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.75f, FontStyle.Bold, GraphicsUnit.Point);
            Colors(Tone == Tone.Neutral || count > 0, out Color back, out Color border, out Color number, out Color caption);

            Graphics g = e.Graphics;
            using (SolidBrush brush = new SolidBrush(back)) g.FillRectangle(brush, ClientRectangle);
            int edge = selected ? Math.Max(2, LogicalToDeviceUnits(3)) : 1;
            using (Pen pen = new Pen(selected ? SystemColors.Highlight : border))
            {
                for (int i = 0; i < edge; i++) g.DrawRectangle(pen, i, i, Width - 1 - 2 * i, Height - 1 - 2 * i);
            }

            int left = LogicalToDeviceUnits(8);
            int top = LogicalToDeviceUnits(4);
            int width = Width - left - edge - 2;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            string countText = count.ToString("N0");
            TextRenderer.DrawText(g, countText, numberFont, new Rectangle(left, top, width, numberFont.Height), number, flags);
            if (showDelta && previous.HasValue && previous.Value != count)
            {
                // "+1" or "-2", small, after the count and level with its foot: up or down since the last run.
                int delta = count - previous.Value;
                string deltaText = (delta > 0 ? "+" : string.Empty) + delta.ToString("N0");
                int x = left + TextRenderer.MeasureText(g, countText, numberFont, Size.Empty, flags).Width + LogicalToDeviceUnits(4);
                int y = top + numberFont.Height - Font.Height - LogicalToDeviceUnits(2);
                TextRenderer.DrawText(g, deltaText, Font, new Rectangle(x, y, Math.Max(0, Width - x - edge - 2), Font.Height + 2), caption, flags);
            }
            TextRenderer.DrawText(g, Caption, Font, new Rectangle(left, top + numberFont.Height, width, Font.Height + 2), caption, flags);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -edge - 2, -edge - 2));
        }

        /// <summary>Tinted by kind while lit; plain system colours in high contrast, and while there's nothing to count.</summary>
        private void Colors(bool lit, out Color back, out Color border, out Color number, out Color caption)
        {
            back = SystemColors.Window;
            border = SystemColors.ControlDark;
            number = SystemColors.WindowText;
            caption = SystemColors.WindowText;
            if (!lit)
            {
                if (!SystemInformation.HighContrast) border = SystemColors.ControlLight;
                number = caption = SystemColors.GrayText;
                return;
            }
            if (SystemInformation.HighContrast) return;
            switch (Tone)
            {
                case Tone.Good:
                    back = Color.FromArgb(234, 246, 236);
                    border = Color.FromArgb(46, 139, 87);
                    number = Color.FromArgb(16, 112, 52);
                    break;
                case Tone.Muted:
                    back = Color.FromArgb(240, 240, 240);
                    border = Color.FromArgb(150, 150, 150);
                    number = Color.FromArgb(80, 80, 80);
                    break;
                case Tone.Warn:
                    back = Color.FromArgb(255, 244, 229);
                    border = Color.FromArgb(222, 133, 23);
                    number = Color.FromArgb(166, 84, 0);
                    break;
                case Tone.Bad:
                    back = Color.FromArgb(253, 236, 236);
                    border = Color.FromArgb(206, 58, 58);
                    number = Color.FromArgb(166, 32, 32);
                    break;
                case Tone.Info:
                    back = Color.FromArgb(232, 241, 251);
                    border = Color.FromArgb(0, 103, 192);
                    number = Color.FromArgb(0, 84, 166);
                    break;
            }
        }
    }
}
