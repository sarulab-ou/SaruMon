using System.ComponentModel;
using System.Diagnostics;
using SaruMon.Monitoring;
using static SaruMon.DashboardStyle;

namespace SaruMon;

internal sealed class TasksView : UserControl
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Label status = Caption();
    private readonly TextBox path = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Surface,
        ForeColor = Ink, Margin = new Padding(3, 3, 3, 0), AccessibleName = "選択したタスクの実行ファイル"
    };
    private readonly ResultGrid grid = new("実行中のプロセス", ("プロセス", 124), ("PID", 53), ("CPU", 75),
        ("メモリ", 68), ("親プロセス", 133), ("親PID", 61), ("スレッド", 68), ("開始時刻", 133));
    private Dictionary<ProcessIdentity, long> previousCpu = new();
    private TaskRow[] tasks = [];
    private long previousTime;
    private ProcessIdentity? selectedIdentity;
    private int sortColumn = 2;
    private bool descending = true;
    private bool active;
    private bool reading;
    private int revision;

    internal TasksView()
    {
        Dock = DockStyle.Fill;
        BackColor = Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 5, 10, 7) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        var reset = ActionButton("リセット");
        reset.Click += async (_, _) =>
        {
            ResetBaseline();
            tasks = tasks.Select(row => row with { Cpu = null }).ToArray();
            Render();
            await RefreshTasksAsync();
        };
        layout.Controls.Add(Strip(reset, status), 0, 0);
        layout.Controls.Add(grid.Control, 0, 1);
        layout.Controls.Add(path, 0, 2);
        Controls.Add(layout);

        foreach (DataGridViewColumn column in grid.Control.Columns) column.SortMode = DataGridViewColumnSortMode.Programmatic;
        grid.Control.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            descending = e.ColumnIndex == sortColumn ? !descending : e.ColumnIndex is 2 or 3 or 6 or 7;
            sortColumn = e.ColumnIndex;
            Render();
        };
        grid.Control.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
                e.ToolTipText = grid.Control.Rows[e.RowIndex].Cells[e.ColumnIndex].Value as string;
        };
        grid.Control.SelectionChanged += (_, _) => ShowPath();
        ProcessContextMenu.Attach(grid, value => value is TaskRow row ? [row.Sample.Target] : []);
        timer.Tick += async (_, _) => await RefreshTasksAsync();
        VisibleChanged += (_, _) => UpdateTimer();
        Render();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateTimer();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!Disposing && !IsDisposed) StopSampling();
        base.OnHandleDestroyed(e);
    }

    internal void SetActive(bool value)
    {
        active = value;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (Disposing || IsDisposed) return;
        if (active && Visible && IsHandleCreated)
        {
            timer.Start();
            _ = RefreshTasksAsync();
        }
        else StopSampling();
    }

    private void StopSampling()
    {
        timer.Stop();
        ResetBaseline();
    }

    private void ResetBaseline()
    {
        previousTime = 0;
        previousCpu.Clear();
        revision++;
    }

    private async Task RefreshTasksAsync()
    {
        if (reading || !active || !Visible || !IsHandleCreated || IsDisposed) return;
        reading = true;
        var requested = revision;
        var time = Stopwatch.GetTimestamp();
        try
        {
            var rows = await Task.Run(WindowsProcesses.Snapshot);
            if (Disposing || IsDisposed || !Visible || requested != revision) return;
            var elapsed = previousTime == 0 ? 0 : Stopwatch.GetElapsedTime(previousTime, time).Ticks;
            var processes = rows.ToDictionary(row => row.Target.Identity.Pid);
            tasks = rows.Select(row => new TaskRow(row, CpuPercent(row, previousCpu, elapsed, WindowsProcesses.LogicalProcessors),
                ParentName(row, processes))).ToArray();
            previousTime = time;
            previousCpu = rows.Where(row => row.CpuTicks.HasValue).ToDictionary(row => row.Target.Identity, row => row.CpuTicks!.Value);
            Render();
        }
        catch (Win32Exception)
        {
            if (Disposing || IsDisposed || requested != revision) return;
            tasks = [];
            previousTime = 0;
            previousCpu.Clear();
            Render();
            status.Text = "プロセス一覧を取得できませんでした。";
        }
        finally { reading = false; }
    }

    internal static double? CpuPercent(ProcessSample row, IReadOnlyDictionary<ProcessIdentity, long> previous, long elapsed, int logicalProcessors)
    {
        if (elapsed <= 0 || row.CpuTicks is not long current || !previous.TryGetValue(row.Target.Identity, out var before)
            || current < before) return null;
        return Math.Clamp((current - before) * 100d / elapsed / logicalProcessors, 0, 100);
    }

    internal static string? ParentName(ProcessSample row, IReadOnlyDictionary<int, ProcessSample> processes)
    {
        if (row.ParentPid > 0 && processes.TryGetValue(row.ParentPid, out var parent)
            && parent.Target.Identity.StartedUtcTicks > 0 && row.Target.Identity.StartedUtcTicks > parent.Target.Identity.StartedUtcTicks)
            return parent.Target.Name;
        return null;
    }

    private void Render()
    {
        IOrderedEnumerable<TaskRow> Sort<T>(Func<TaskRow, T> key) => descending ? tasks.OrderByDescending(key) : tasks.OrderBy(key);
        var ordered = sortColumn switch
        {
            0 => Sort(row => row.Sample.Target.Name),
            1 => Sort(row => row.Sample.Target.Identity.Pid),
            2 => Sort(row => row.Cpu),
            3 => Sort(row => row.Sample.WorkingSet),
            4 => Sort(row => row.Parent),
            5 => Sort(row => row.Sample.ParentPid),
            6 => Sort(row => row.Sample.Threads),
            _ => Sort(row => row.Sample.Target.Identity.StartedUtcTicks)
        };
        foreach (DataGridViewColumn column in grid.Control.Columns)
            column.HeaderCell.SortGlyphDirection = column.Index == sortColumn
                ? descending ? SortOrder.Descending : SortOrder.Ascending : SortOrder.None;
        status.Text = $"実行中のプロセス {tasks.Length:N0} 件";
        grid.Set(ordered.ThenBy(row => row.Sample.Target.Identity.Pid).Select(row =>
        {
            var identity = row.Sample.Target.Identity;
            return new ResultRow(identity, row,
            [
                row.Sample.Target.Name, identity.Pid.ToString(), row.Cpu is double cpu ? $"{cpu:0.0}%" : "—",
                row.Sample.WorkingSet is long bytes ? MainForm.FormatBytes(bytes, compact: true) : "—", row.Parent ?? "—",
                row.Sample.ParentPid > 0 ? row.Sample.ParentPid.ToString() : "—", row.Sample.Threads.ToString(),
                identity.StartedUtcTicks > 0 ? new DateTime(identity.StartedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM/dd HH:mm:ss") : "—"
            ]);
        }).ToArray());
        ShowPath();
    }

    private void ShowPath()
    {
        var identity = (grid.Selected?.Value as TaskRow)?.Sample.Target.Identity;
        if (selectedIdentity == identity) return;
        selectedIdentity = identity;
        path.Text = identity is { } process ? WindowsProcesses.ReadPath(process) ?? "実行ファイルのパスを取得できません" : "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed record TaskRow(ProcessSample Sample, double? Cpu, string? Parent);
}
