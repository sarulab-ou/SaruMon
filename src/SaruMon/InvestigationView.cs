using System.Net;
using SaruMon.Core;
using SaruMon.Monitoring;
using static SaruMon.DashboardStyle;

namespace SaruMon;

internal sealed class InvestigationView : UserControl
{
    private readonly TrafficMonitorClient client = new(MonitoringMode.Investigation);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly TrafficGraph graph = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
    private readonly Button start = ActionButton("記録開始（管理者）", accent: true);
    private readonly Button stop = ActionButton("停止");
    private readonly Button save = ActionButton("調査レポート…");
    private readonly Button csv = ActionButton("全件CSV…");
    private readonly Button back = ActionButton("← 宛先一覧");
    private readonly Label status = Caption();
    private readonly Label breakdownTitle = Caption();
    private readonly TextBox details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = false, BorderStyle = BorderStyle.None,
        BackColor = Surface, ForeColor = Ink, AccessibleName = "選択した内訳の実行ファイルと観測時刻" };
    private readonly Button[] groupButtons = [ActionButton("アプリ別"), ActionButton("宛先別")];
    private readonly Button[] sortButtons = [ActionButton("最新"), ActionButton("上り"), ActionButton("下り")];
    private readonly ResultGrid primary = new("調査の集計", ("対象", 220), ("上り TCP+UDP", 105), ("下り TCP", 105), ("最終観測", 132));
    private readonly ResultGrid secondary = new("宛先・アプリの内訳", ("対象", 160), ("上り TCP+UDP", 105), ("下り TCP", 105), ("最終観測", 132));
    private TrafficInterface? selectedInterface;
    private TrafficInterface? recordedInterface;
    private InvestigationRecord? recording;
    private InvestigationSnapshot? lastSnapshot;
    private string? expandedAddress;
    private bool busy;
    private bool ending;
    private bool updatingTables;
    private int grouping;
    private int order;
    private string? notice;

    internal InvestigationView()
    {
        Dock = DockStyle.Fill;
        BackColor = Canvas;
        ForeColor = Ink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10, 5, 10, 7) };
        foreach (var height in new[] { 34, 23, 94, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.Controls.Add(Strip(start, stop, save, csv), 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(graph, 0, 2);
        layout.Controls.Add(Strip([.. groupButtons, Caption("並べ替え　"), .. sortButtons]), 0, 3);
        var tables = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        tables.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tables.Controls.Add(Caption("通信の集計"), 0, 0);
        tables.Controls.Add(Strip(back, breakdownTitle), 1, 0);
        tables.Controls.Add(primary.Control, 0, 1);
        tables.Controls.Add(secondary.Control, 1, 1);
        layout.Controls.Add(tables, 0, 4);
        layout.Controls.Add(details, 0, 5);
        Controls.Add(layout);

        start.Click += async (_, _) => await StartAsync();
        stop.Click += async (_, _) => await StopAsync();
        save.Click += (_, _) => ShowReport();
        csv.Click += async (_, _) => await SaveCsvAsync();
        for (var i = 0; i < groupButtons.Length; i++)
        {
            var index = i;
            groupButtons[i].Click += (_, _) => SetGrouping(index);
        }
        for (var i = 0; i < sortButtons.Length; i++)
        {
            var index = i;
            sortButtons[i].Click += (_, _) => { order = index; RenderChoices(); RenderTables(); };
        }
        primary.Control.SelectionChanged += (_, _) =>
        {
            if (updatingTables) return;
            expandedAddress = null;
            RenderBreakdown();
        };
        secondary.Control.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenDestination(); };
        secondary.Control.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || !OpenDestination()) return;
            e.Handled = e.SuppressKeyPress = true;
        };
        secondary.Control.SelectionChanged += (_, _) => { if (!updatingTables) ShowDetails(); };
        ProcessContextMenu.Attach(primary, GetProcessTargets);
        ProcessContextMenu.Attach(secondary, GetProcessTargets);
        back.Click += (_, _) => { expandedAddress = null; RenderBreakdown(); };
        timer.Tick += (_, _) => ReadRecording();
        VisibleChanged += (_, _) => { if (Visible && !ending) RenderTables(); };
        RenderChoices();
        RenderStatus();
    }

    private static IEnumerable<ProcessTarget> GetProcessTargets(object value)
    {
        IReadOnlyList<InvestigationRow> rows = value switch
        {
            InvestigationGroup group => group.Rows,
            InvestigationRow row => [row],
            _ => []
        };
        return rows.Select(row => new ProcessTarget(new ProcessIdentity(row.Key.Process.Pid, row.Key.Process.StartedUtcTicks),
            row.Path is not null ? Path.GetFileName(row.Path) : "発生元未確認"));
    }

    internal void SetInterface(TrafficInterface? adapter)
    {
        selectedInterface = adapter;
        if (client.IsRunning && !busy && !SameInterface(recordedInterface, adapter))
            _ = StopAsync("回線またはローカルIPが変わったため停止しました。");
        RenderStatus();
    }

    private async Task StartAsync()
    {
        if (busy || ending || selectedInterface is not { IsUp: true } target || target.Addresses.Count == 0) return;
        busy = true;
        notice = "管理者承認を待っています…";
        RenderStatus();
        try
        {
            var addresses = target.Addresses.Select(IPAddress.Parse).ToArray();
            await client.StartAsync(addresses, CancellationToken.None);
            if (ending) return;
            var now = DateTimeOffset.UtcNow;
            recordedInterface = target;
            recording = new InvestigationRecord(target.Name, now, now, [], 0, false);
            lastSnapshot = null;
            expandedAddress = null;
            notice = null;
            graph.Reset();
            RenderTables();
            timer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException)
        {
            notice = client.Error ?? ex.Message;
        }
        finally
        {
            busy = false;
            if (!ending) RenderStatus();
        }
        if (!ending && client.IsRunning && !SameInterface(target, selectedInterface))
            await StopAsync("回線またはローカルIPが変わったため停止しました。");
    }

    private async Task StopAsync(string? reason = null)
    {
        if (busy || ending) return;
        busy = true;
        notice = "最後の集計を取得しています…";
        RenderStatus();
        try
        {
            await client.StopAsync();
            notice = reason ?? client.Error;
        }
        catch (InvalidOperationException ex) { notice = ex.Message; }
        finally
        {
            busy = false;
            if (!ending) ReadRecording();
        }
    }

    private void ReadRecording()
    {
        if (ending || recording is null) return;
        var snapshot = client.InvestigationSnapshot;
        var changed = snapshot is not null && !ReferenceEquals(snapshot.Rows, recording.Rows);
        if (snapshot is not null && !ReferenceEquals(snapshot, lastSnapshot))
        {
            var seconds = Math.Max(0.01, (snapshot.SampledAt - (lastSnapshot?.SampledAt ?? recording.Started)).TotalSeconds);
            var newRows = snapshot.Rows;
            var earliest = changed && newRows.Count > 0 ? newRows.Min(row => row.FirstSeen) : recording.Started;
            var deltaSent = changed ? InvestigationReport.Sum(newRows, row => row.Sent) - InvestigationReport.Sum(recording.Rows, row => row.Sent) : 0;
            var deltaReceived = changed ? InvestigationReport.Sum(newRows, row => row.Received) - InvestigationReport.Sum(recording.Rows, row => row.Received) : 0;
            // A delayed refresh occupies its elapsed slots instead of compressing the time axis.
            for (var i = 1; i < Math.Round(Math.Min(300, seconds)); i++) graph.Add(0, 0);
            graph.Add(Math.Max(0, deltaReceived) / seconds, Math.Max(0, deltaSent) / seconds);
            recording = recording with { Rows = newRows, Started = earliest < recording.Started ? earliest : recording.Started,
                Ended = snapshot.SampledAt < recording.Started ? recording.Started : snapshot.SampledAt,
                DroppedEvents = snapshot.DroppedEvents, Incomplete = snapshot.Incomplete };
            lastSnapshot = snapshot;
        }
        if (!client.IsRunning && !busy)
        {
            timer.Stop();
            if (snapshot is null) recording = recording with { Ended = DateTimeOffset.UtcNow };
            if (client.Error is not null)
            {
                recording = recording with { Incomplete = true };
                notice = client.Error;
            }
        }
        if (Visible && changed) RenderTables();
        RenderStatus();
    }

    private void RenderStatus()
    {
        var data = recording;
        start.Enabled = !busy && !client.IsRunning && selectedInterface is { IsUp: true, Addresses.Count: > 0 };
        stop.Enabled = !busy && (client.IsRunning || client.Error?.Contains("停止を再試行", StringComparison.Ordinal) == true);
        save.Enabled = csv.Enabled = data is not null && !busy;
        var phase = client.IsRunning ? "記録中" : data is null ? "未記録" : "停止";
        var label = data is null ? phase + " · 回線を選んで記録を開始" :
            $"{phase} · {data.Interface} · {data.Rows.Count:N0} 行 · 発生元未確認 {data.Rows.Count(row => row.Path is null):N0} 行";
        if (data?.Incomplete == true) label += $" · 部分記録（未記録イベント {data.DroppedEvents:N0}）";
        status.Text = notice is not null ? label + " · " + notice : label;
        status.ForeColor = data?.Incomplete == true || notice is not null ? Warning : Muted;
    }

    private void RenderTables()
    {
        var rows = recording?.Rows ?? [];
        var byAddress = grouping == 1;
        var groups = byAddress ? InvestigationReport.Destinations(rows) : InvestigationReport.Applications(rows);
        updatingTables = true;
        try
        {
            primary.Headers(byAddress ? "宛先IP" : "アプリ", "上り", "下り", "最終観測");
            primary.Set(Sort(groups).Select(GroupRow).ToArray());
        }
        finally { updatingTables = false; }
        RenderBreakdown();
    }

    private void SetGrouping(int value)
    {
        grouping = value;
        expandedAddress = null;
        RenderChoices();
        RenderTables();
    }

    private void RenderChoices()
    {
        for (var i = 0; i < groupButtons.Length; i++) groupButtons[i].BackColor = i == grouping ? Selected : Surface;
        for (var i = 0; i < sortButtons.Length; i++) sortButtons[i].BackColor = i == order ? Selected : Surface;
    }

    private void RenderBreakdown()
    {
        updatingTables = true;
        try
        {
            var group = primary.Selected?.Value as InvestigationGroup;
            var destinations = grouping == 0;
            var detailRows = group?.Rows ?? [];
            back.Visible = destinations && expandedAddress is not null;
            if (destinations && expandedAddress is null)
            {
                breakdownTitle.Text = "宛先IP · ダブルクリックで内訳";
                secondary.Headers("宛先IP", "上り", "下り", "最終観測");
                secondary.Set(Sort(InvestigationReport.Destinations(detailRows)).Select(GroupRow).ToArray());
            }
            else
            {
                var address = destinations ? expandedAddress : group?.Label;
                breakdownTitle.Text = address is null ? "通信の内訳" : $"{address} の内訳";
                secondary.Headers("アプリ / PID", "種別", "ポート", "上り", "下り");
                secondary.Set(Sort(detailRows.Where(row => row.Key.Address == address)).Select(row => new ResultRow(row.Key, row,
                    [$"{InvestigationReport.ApplicationLabel(row)} / {InvestigationReport.Pid(row)}", InvestigationReport.Protocol(row.Key.Protocol),
                        row.Key.Port.ToString(), MainForm.FormatBytes(row.Sent, compact: true),
                        row.Key.Protocol == InvestigationProtocol.Tcp ? MainForm.FormatBytes(row.Received, compact: true) : "—"])).ToArray());
            }
        }
        finally { updatingTables = false; }
        ShowDetails();
    }

    private bool OpenDestination()
    {
        if (grouping != 0 || secondary.Selected?.Value is not InvestigationGroup group) return false;
        expandedAddress = group.Key;
        RenderBreakdown();
        return true;
    }

    private void ShowDetails()
    {
        details.Text = secondary.Selected?.Value is InvestigationRow row
            ? $"{row.Path ?? "実行ファイル未確認"}  ·  PID {InvestigationReport.Pid(row)}\r\n初回 {DisplayTime(row.FirstSeen)}  /  最終 {DisplayTime(row.LastSeen)}  ·  上り TCP＋UDP / 下り TCP"
            : "上りは TCP＋UDP、下りは観測した TCP。UDP の下りは「—」。";
    }

    private IOrderedEnumerable<InvestigationGroup> Sort(IEnumerable<InvestigationGroup> groups) =>
        (order switch
        {
            1 => groups.OrderByDescending(item => item.Sent),
            2 => groups.OrderByDescending(item => item.IncludesTcp).ThenByDescending(item => item.Received),
            _ => groups.OrderByDescending(item => item.LastSeen)
        }).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase);

    private IOrderedEnumerable<InvestigationRow> Sort(IEnumerable<InvestigationRow> rows) =>
        (order switch
        {
            1 => rows.OrderByDescending(row => row.Sent),
            2 => rows.OrderBy(row => row.Key.Protocol).ThenByDescending(row => row.Received),
            _ => rows.OrderByDescending(row => row.LastSeen)
        }).ThenBy(row => row.Key.Process.Pid).ThenBy(row => row.Key.Port).ThenBy(row => row.Key.Protocol);

    private static ResultRow GroupRow(InvestigationGroup group) => new(group.Key, group,
        [group.Label, MainForm.FormatBytes(group.Sent, compact: true),
            group.IncludesTcp ? MainForm.FormatBytes(group.Received, compact: true) : "—", group.LastSeen.ToLocalTime().ToString("HH:mm:ss")]);

    private void ShowReport()
    {
        if (recording is not { } data) return;
        var application = grouping == 0 ? primary.Selected?.Value as InvestigationGroup : null;
        using var preview = new Form { Text = "調査レポートのプレビュー", ClientSize = new Size(830, 530),
            MinimumSize = new Size(540, 340), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = true };
        var scope = Selector("レポート対象", "全体", "選択アプリ");
        if (application is null) scope.Items.RemoveAt(1);
        var render = new Button { Text = "表示", AutoSize = true };
        var accept = new Button { Text = "この内容を保存…", AutoSize = true, Enabled = false };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 10), AccessibleName = "出力されるMarkdown" };
        var top = Strip(scope, render, accept, Caption("IP・実行ファイルのパスは実値です。共有前にご確認ください。"));
        top.Height = 39;
        top.Dock = DockStyle.Top;
        top.BackColor = Surface;
        preview.Controls.Add(text);
        preview.Controls.Add(top);
        bool rendering = false;
        string? previewMarkdown = null;
        async Task RenderAsync()
        {
            rendering = true;
            scope.Enabled = render.Enabled = accept.Enabled = false;
            previewMarkdown = null;
            var selected = scope.SelectedIndex == 1 ? application : null;
            text.Text = "作成中…";
            try
            {
                var markdown = await Task.Run(() => InvestigationReport.Markdown(data, selected, selected is null ? null :
                    InvestigationReport.StartupMatches(selected.Rows[0].Path)));
                if (!preview.IsDisposed)
                {
                    previewMarkdown = markdown;
                    text.Text = markdown.ReplaceLineEndings("\r\n");
                    accept.Enabled = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (!preview.IsDisposed) text.Text = ex.Message;
            }
            finally
            {
                rendering = false;
                if (!preview.IsDisposed) scope.Enabled = render.Enabled = true;
            }
        }
        preview.Shown += async (_, _) => await RenderAsync();
        scope.SelectedIndexChanged += async (_, _) => { if (!rendering) await RenderAsync(); };
        render.Click += async (_, _) => await RenderAsync();
        accept.Click += async (_, _) =>
        {
            if (previewMarkdown is { } output)
                await SaveFileAsync(preview, "Markdown (*.md)|*.md", "md", path => InvestigationReport.SaveMarkdown(path, output));
        };
        preview.ShowDialog(FindForm());
    }

    private async Task SaveCsvAsync()
    {
        if (recording is { } data)
            await SaveFileAsync(this, "CSV (*.csv)|*.csv", "csv", path => InvestigationReport.SaveCsv(path, data));
    }

    private static async Task SaveFileAsync(IWin32Window owner, string filter, string extension, Action<string> write)
    {
        using var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true,
            FileName = $"SaruMon-調査-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}" };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        var path = dialog.FileName;
        try { await Task.Run(() => write(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(owner, ex.Message, "保存できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal async Task ShutdownAsync()
    {
        ending = true;
        timer.Stop();
        try { await client.DisposeAsync(); }
        catch
        {
            ending = false;
            RenderStatus();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            if (!ending) _ = ShutdownAfterDisposeAsync();
        }
        base.Dispose(disposing);
    }

    private async Task ShutdownAfterDisposeAsync()
    {
        ending = true;
        try { await client.DisposeAsync(); } catch (InvalidOperationException) { }
    }

    private static bool SameInterface(TrafficInterface? left, TrafficInterface? right) =>
        left is not null && right is { IsUp: true } && left.Id == right.Id
        && new HashSet<string>(left.Addresses, StringComparer.OrdinalIgnoreCase).SetEquals(right.Addresses);

    private static string DisplayTime(DateTimeOffset time) => time.ToLocalTime().ToString("MM/dd HH:mm:ss");
    private static ComboBox Selector(string label, params string[] values)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = label, Width = values[0].Length < 5 ? 100 : 156,
            BackColor = Surface, ForeColor = Ink, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 1, 8, 0) };
        box.Items.AddRange(values);
        box.SelectedIndex = 0;
        return box;
    }

}
