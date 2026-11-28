namespace SaruMon;

using static DashboardStyle;

internal static class DashboardStyle
{
    internal static readonly Color Canvas = Color.White;
    internal static readonly Color Surface = Color.FromArgb(247, 249, 252);
    internal static readonly Color Ink = Color.FromArgb(29, 42, 64);
    internal static readonly Color Muted = Color.FromArgb(76, 94, 119);
    internal static readonly Color Border = Color.FromArgb(211, 222, 236);
    internal static readonly Color Selected = Color.FromArgb(220, 235, 253);
    internal static readonly Color Heading = Color.FromArgb(233, 239, 247);
    internal static readonly Color Warning = Color.FromArgb(139, 76, 10);
    internal static Label Caption(string text = "") => new() { Text = text, AutoSize = false, Dock = DockStyle.Fill,
        AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = Padding.Empty, BackColor = Color.Transparent };

    internal static FlowLayoutPanel Strip(params Control[] children)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        foreach (var control in children)
        {
            if (control is Label label)
            {
                label.AutoSize = true;
                label.Dock = DockStyle.None;
                label.Margin = new Padding(2, 6, 0, 0);
            }
            row.Controls.Add(control);
        }
        return row;
    }

    internal static Button ActionButton(string text, bool accent = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 27, MinimumSize = new Size(0, 25),
            BackColor = accent ? Selected : Surface, ForeColor = Ink,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 1), Padding = new Padding(5, 0, 5, 0)
        };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }

}

internal sealed record ResultRow(object Key, object Value, string[] Cells);

internal sealed class ResultGrid
{
    private ResultRow[] rows = [];
    internal DataGridView Control { get; } = new()
    {
        Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, BackgroundColor = Surface,
        BorderStyle = BorderStyle.None, GridColor = Border, EnableHeadersVisualStyles = false,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false,
        ColumnHeadersHeight = 27, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        Margin = new Padding(0, 0, 6, 1), CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
    };

    internal ResultGrid(string name, params (string Title, int Width)[] columns)
    {
        Control.AccessibleName = name;
        Control.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Ink,
            SelectionBackColor = DashboardStyle.Selected, SelectionForeColor = Ink };
        Control.AlternatingRowsDefaultCellStyle.BackColor = Canvas;
        Control.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Heading, ForeColor = Ink,
            SelectionBackColor = Heading, SelectionForeColor = Ink };
        foreach (var (title, width) in columns) AddColumn(title, width);
        Control.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Control.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < rows.Length && e.ColumnIndex < rows[e.RowIndex].Cells.Length)
                e.Value = rows[e.RowIndex].Cells[e.ColumnIndex];
        };
    }

    internal ResultRow? Selected => Control.CurrentCell is { RowIndex: var index } && index < rows.Length ? rows[index] : null;

    internal void Headers(params string[] names)
    {
        if (Control.Columns.Count == names.Length && names.Select((name, index) => Control.Columns[index].HeaderText == name).All(same => same)) return;
        Control.RowCount = 0;
        Control.Columns.Clear();
        var dimensions = names.Length == 4 ? new[] { 120, 66, 66, 91 } : new[] { 121, 60, 53, 64, 64 };
        for (var i = 0; i < names.Length; i++) AddColumn(names[i], dimensions[i]);
        Control.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }

    private void AddColumn(string title, int width) =>
        Control.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = title, Width = width, FillWeight = width, MinimumWidth = Math.Min(width, 48),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

    internal void Set(ResultRow[] updated)
    {
        var key = Selected?.Key;
        var scroll = Control.FirstDisplayedScrollingRowIndex;
        var scrollKey = scroll >= 0 && scroll < rows.Length ? rows[scroll].Key : null;
        rows = updated;
        Control.RowCount = rows.Length;
        if (rows.Length > 0)
        {
            var index = Array.FindIndex(rows, row => Equals(row.Key, key));
            if (index < 0) index = 0;
            if (Control.CurrentCell?.RowIndex != index) Control.CurrentCell = Control.Rows[index].Cells[0];
            var anchor = Array.FindIndex(rows, row => Equals(row.Key, scrollKey));
            if (anchor >= 0 && anchor != Control.FirstDisplayedScrollingRowIndex) Control.FirstDisplayedScrollingRowIndex = anchor;
        }
        Control.Invalidate();
    }
}
