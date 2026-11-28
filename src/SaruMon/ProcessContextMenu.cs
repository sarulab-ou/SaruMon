using System.ComponentModel;
using SaruMon.Monitoring;

namespace SaruMon;

internal static class ProcessContextMenu
{
    internal static void Attach(ResultGrid grid, Func<object, IEnumerable<ProcessTarget>> targets)
    {
        var table = grid.Control;
        var menu = new ContextMenuStrip();
        table.Disposed += (_, _) => menu.Dispose();
        table.CellMouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            table.CurrentCell = table.Rows[e.RowIndex].Cells[e.ColumnIndex];
            Show(table.PointToClient(Cursor.Position));
        };
        table.KeyDown += (_, e) =>
        {
            if (table.CurrentCell is not { } cell || !(e.KeyCode == Keys.Apps || e is { KeyCode: Keys.F10, Shift: true })) return;
            e.Handled = e.SuppressKeyPress = true;
            var bounds = table.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false);
            Show(new Point(bounds.Left, bounds.Bottom));
        };

        void Show(Point point)
        {
            if (grid.Selected is not { } row) return;
            foreach (var item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            menu.Items.AddRange(CreateItems(table, targets(row.Value)));
            menu.Show(table, point);
        }
    }

    internal static ToolStripItem[] CreateItems(Control owner, IEnumerable<ProcessTarget> targets)
    {
        var processes = targets.DistinctBy(target => target.Identity)
            .OrderBy(target => target.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.Identity.Pid).ToArray();
        var repeatedPids = processes.GroupBy(target => target.Identity.Pid).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        return [Action("タスクを終了", false), Action("強制終了", true)];

        ToolStripMenuItem Action(string title, bool force)
        {
            var action = new ToolStripMenuItem(title) { Enabled = processes.Any(target => target.Identity.CanTarget) };
            if (processes.Length == 1)
                action.Click += (_, _) => RequestExit(owner, processes[0], force);
            else
                foreach (var target in processes)
                {
                    var label = target.Identity.Pid > 0 ? target.Label : "PID 不明";
                    if (repeatedPids.Contains(target.Identity.Pid) && target.Identity.StartedUtcTicks > 0)
                        label += $" · {new DateTime(target.Identity.StartedUtcTicks, DateTimeKind.Utc).ToLocalTime():MM/dd HH:mm:ss}";
                    var item = new ToolStripMenuItem(label.Replace("&", "&&")) { Enabled = target.Identity.CanTarget };
                    item.Click += (_, _) => RequestExit(owner, target, force);
                    action.DropDownItems.Add(item);
                }
            return action;
        }
    }

    private static void RequestExit(Control owner, ProcessTarget target, bool force)
    {
        if (force && MessageBox.Show(owner,
            $"{target.Label} を強制終了しますか？\n未保存のデータが失われる場合があります。",
            "強制終了", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        try { WindowsProcesses.RequestExit(target.Identity, force); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(owner, $"{target.Label}\n{ex.Message}", "タスクの終了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
