using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.ConfigurationManagement.AdminConsole.Common;

namespace Layer8
{
    /// <summary>
    /// Sorts a list's rows by one column: by each cell's real value where Layer8 put one in the cell's Tag (uptime,
    /// bytes, IP address, version, date), otherwise by its text the way Explorer sorts names (DEV2 before DEV10).
    /// Blank cells go last in both directions; ties go by the first column.
    /// </summary>
    public sealed class ListViewSorter : IComparer
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

        // From System32 only: the default search looks in the console's folder and Layer8's own first.
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int StrCmpLogicalW(string a, string b);
    }

    /// <summary>
    /// Makes a console list sort itself with <see cref="ListViewSorter"/>: a click on a header sorts by that column,
    /// a second click reverses it, and the arrow follows. The console's own sort compares the text, so 8,16 MB lands
    /// ahead of 670,42 MB and 9.5 seconds after 12 (issue #3). Call <see cref="Apply"/> after filling the list.
    /// </summary>
    public sealed class ListSorting
    {
        private readonly SmsSearchableListView list;
        private int column;
        private SortOrder order = SortOrder.Ascending;

        public ListSorting(SmsSearchableListView list, int column = 0, SortOrder order = SortOrder.Ascending)
        {
            this.list = list;
            this.column = column;
            this.order = order;
            list.AutoSort = false;
            list.ColumnClick += (s, e) => SortBy(e.Column);
        }

        private void SortBy(int clicked)
        {
            order = clicked == column && order == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            // The arrow is drawn per column, so the last sorted column's has to be taken off.
            if (clicked != column) list.ShowArrow(column, SortOrder.None);
            column = clicked;
            Apply();
        }

        /// <summary>Sorts the list as it stands by the current column and direction, and draws the arrow.</summary>
        public void Apply()
        {
            list.ListViewItemSorter = new ListViewSorter(column, order == SortOrder.Descending);
            list.Sort();
            list.ShowArrow(column, order);
        }
    }
}
