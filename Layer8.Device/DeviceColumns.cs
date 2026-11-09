using System;
using System.Collections.Generic;

namespace Layer8.Device
{
    /// <summary>
    /// A device's answer split into columns, for tools that find out several things at once (Quick Status). The
    /// progress dialog gives each its own column that sorts by its real value (uptime, bytes, IP address, version),
    /// and Copy Results and Export carry them as separate columns.
    /// </summary>
    public sealed class DeviceColumns
    {
        public sealed class Cell
        {
            public string Column { get; internal set; }
            public string Text { get; internal set; }
            /// <summary>What the column sorts by; null sorts by the text.</summary>
            public IComparable SortKey { get; internal set; }
        }

        private readonly List<Cell> cells = new List<Cell>();

        public IList<Cell> Cells => cells.AsReadOnly();

        /// <summary>What the Status column shows for a device that answered.</summary>
        public string Status { get; set; } = "Completed";

        /// <summary>Every column in one line, for the log.</summary>
        public string Summary { get; set; }

        /// <summary>Adds a column. Empty text leaves the cell blank, and blanks sort last either way.</summary>
        public DeviceColumns Add(string column, string text, IComparable sortKey = null)
        {
            cells.Add(new Cell { Column = column, Text = text ?? string.Empty, SortKey = sortKey });
            return this;
        }

        public override string ToString() => Summary ?? Status;
    }
}
