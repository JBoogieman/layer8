using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Sorts a list's rows by one column: by each cell's real value where Layer8 put one in the cell's Tag (uptime,
    /// bytes, IP address, version, date), otherwise by its text the way Explorer sorts names (DEV2 before DEV10).
    /// Blank cells go last in both directions; ties go by the first column.
    /// </summary>
    internal sealed class ListViewSorter : IComparer
    {
        private readonly int column;
        private readonly bool descending;

        public ListViewSorter(int column, bool descending)
        {
            this.column = column;
            this.descending = descending;
        }

        public int Compare(object x, object y)
        {
            ListViewItem a = (ListViewItem)x;
            ListViewItem b = (ListViewItem)y;
            ListViewItem.ListViewSubItem cellA = column < a.SubItems.Count ? a.SubItems[column] : null;
            ListViewItem.ListViewSubItem cellB = column < b.SubItems.Count ? b.SubItems[column] : null;
            bool blankA = string.IsNullOrEmpty(cellA?.Text);
            bool blankB = string.IsNullOrEmpty(cellB?.Text);
            if (blankA || blankB)
            {
                if (blankA != blankB) return blankA ? 1 : -1;
                return NaturalCompare(a.Text, b.Text);
            }

            int result = cellA.Tag is IComparable keyA && cellB.Tag is IComparable keyB && keyA.GetType() == keyB.GetType()
                ? keyA.CompareTo(keyB)
                : NaturalCompare(cellA.Text, cellB.Text);
            if (descending) result = -result;
            return result != 0 || column == 0 ? result : NaturalCompare(a.Text, b.Text);
        }

        internal static int NaturalCompare(string a, string b) => StrCmpLogicalW(a ?? string.Empty, b ?? string.Empty);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);
    }
}
