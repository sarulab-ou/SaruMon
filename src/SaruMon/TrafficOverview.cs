using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using SaruMon.Core;
using static SaruMon.DashboardStyle;

namespace SaruMon;

internal sealed class TrafficOverview : UserControl
{
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly Func<IPAddress[], IReadOnlyList<TcpConnectionInfo>> read;
    private readonly TrafficGraph graph = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
    private readonly Label receive = Caption();
    private readonly Label send = Caption();
    private readonly Label listTitle = Caption("現在の TCP 接続");
    private readonly Label detailTitle = Caption("選択した項目の内訳");
    private readonly Label endpoint = Caption();
    private readonly Button applications = ActionButton("アプリ別", accent: true);
    private readonly Button destinations = ActionButton("宛先別");
    private readonly ResultGrid overview = new("現在のTCP接続の集計", ("アプリ", 175), ("PID", 58), ("接続数", 72));
    private readonly ResultGrid details = new("現在のTCP接続の内訳", ("宛先IP", 147), ("ポート", 63), ("状態", 89));
    private (string Id, string Addresses)? selection;
    private IPAddress[] addresses = [];
    private LiveConnection[] connections = [];
    private bool byDestination;
    private bool updating;
    private bool reading;
    private int revision;

    internal TrafficOverview(Func<IPAddress[], IReadOnlyList<TcpConnectionInfo>>? read = null)
    {
        this.read = read ?? TcpConnections.ReadWithOwners;
        Dock = DockStyle.Fill;
        BackColor = Canvas;
        ForeColor = Ink;
        receive.ForeColor = TrafficIcons.ReceiveColor;
        send.ForeColor = TrafficIcons.SendColor;
        receive.Margin = new Padding(9, 0, 0, 0);
        send.Margin = receive.Margin;
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Surface };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.Controls.Add(receive, 0, 0);
        summary.Controls.Add(send, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10, 5, 10, 7) };
        foreach (var height in new[] { 34, 94, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(graph, 0, 1);
        layout.Controls.Add(Strip(applications, destinations), 0, 2);

        var tables = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        tables.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tables.Controls.Add(listTitle, 0, 0);
        tables.Controls.Add(detailTitle, 1, 0);
        tables.Controls.Add(overview.Control, 0, 1);
        tables.Controls.Add(details.Control, 1, 1);
        layout.Controls.Add(tables, 0, 3);
        layout.Controls.Add(endpoint, 0, 4);
        Controls.Add(layout);

        applications.Click += (_, _) => SelectGrouping(false);
        destinations.Click += (_, _) => SelectGrouping(true);
        overview.Control.SelectionChanged += (_, _) => { if (!updating) RenderDetails(); };
        details.Control.SelectionChanged += (_, _) => { if (!updating) RenderEndpoint(); };
        refresh.Tick += async (_, _) => await RefreshConnectionsAsync();
        VisibleChanged += async (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            if (Visible && IsHandleCreated && addresses.Length > 0)
            {
                refresh.Start();
                await RefreshConnectionsAsync();
            }
            else
            {
                refresh.Stop();
                revision++;
            }
        };
        SetSample(null, addGraphSample: false);
        RenderConnections();
    }

    internal void SetSample(TrafficSample? sample, bool addGraphSample)
    {
        var down = sample?.HasRate == true ? sample.DownloadBytesPerSecond : (double?)null;
        var up = sample?.HasRate == true ? sample.UploadBytesPerSecond : (double?)null;
        receive.Text = $"↓ 受信  {Rate(down)}  ·  合計 {MainForm.FormatBytes(sample?.TotalReceivedBytes ?? 0)}";
        send.Text = $"↑ 送信  {Rate(up)}  ·  合計 {MainForm.FormatBytes(sample?.TotalSentBytes ?? 0)}";
        if (addGraphSample) graph.Add(down ?? 0, up ?? 0);
    }

    internal void ResetGraph() => graph.Reset();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Visible && addresses.Length > 0)
        {
            refresh.Start();
            _ = RefreshConnectionsAsync();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!Disposing && !IsDisposed) refresh.Stop();
        revision++;
        base.OnHandleDestroyed(e);
    }

    internal void SetInterface(TrafficInterface? adapter)
    {
        var next = adapter is { IsUp: true } ? adapter.Addresses.Select(value => IPAddress.TryParse(value, out var ip) ? ip : null)
            .OfType<IPAddress>().Distinct().ToArray() : [];
        var key = (adapter?.Id ?? "", string.Join(";", next.Select(ip => ip.ToString()).Order(StringComparer.Ordinal)));
        if (selection == key) return;
        selection = key;
        addresses = next;
        revision++;
        connections = [];
        listTitle.Text = next.Length == 0 ? "接続を表示できる回線がありません" : "現在の TCP 接続を取得中…";
        RenderConnections();
        if (Visible && IsHandleCreated && next.Length > 0)
        {
            refresh.Start();
            _ = RefreshConnectionsAsync();
        }
        else refresh.Stop();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (reading || !Visible || !IsHandleCreated || IsDisposed || addresses.Length == 0) return;
        reading = true;
        var requested = revision;
        var selected = addresses;
        try
        {
            var current = await Task.Run(() => ReadConnections(selected));
            if (IsDisposed || !Visible || requested != revision) return;
            connections = current;
            listTitle.Text = $"現在の TCP 接続 · {current.Length:N0} 件";
            RenderConnections();
        }
        catch (Exception ex) when (ex is NetworkInformationException or IOException or InvalidDataException)
        {
            if (IsDisposed || requested != revision) return;
            connections = [];
            listTitle.Text = "現在の TCP 接続を取得できません";
            RenderConnections();
        }
        finally { reading = false; }
    }

    private LiveConnection[] ReadConnections(IPAddress[] selected)
    {
        var capturedAt = DateTime.UtcNow;
        var rows = read(selected);
        var names = rows.Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).Distinct()
            .ToDictionary(pid => pid, pid => ReadProcessName(pid, capturedAt));
        return rows.Select(row => new LiveConnection(row, row.ProcessId is int pid ? names[pid] ?? "不明" : "不明")).ToArray();
    }

    private static string? ReadProcessName(int pid, DateTime capturedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime() <= capturedAt ? process.ProcessName : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private void SelectGrouping(bool destinationsFirst)
    {
        byDestination = destinationsFirst;
        applications.BackColor = byDestination ? Surface : Selected;
        destinations.BackColor = byDestination ? Selected : Surface;
        RenderConnections();
    }

    private void RenderConnections()
    {
        updating = true;
        try
        {
            overview.Control.Columns[0].HeaderText = byDestination ? "宛先IP" : "アプリ";
            overview.Control.Columns[1].HeaderText = byDestination ? "PID数" : "PID";
            details.Control.Columns[0].HeaderText = byDestination ? "PID / アプリ" : "宛先IP";
            var groups = connections.GroupBy(row => byDestination ? row.Address : row.Entry.ProcessId?.ToString() ?? "—", StringComparer.Ordinal);
            overview.Set(groups.OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new ResultRow(group.Key, group.ToArray(),
                    [byDestination ? group.Key : group.First().Application,
                        byDestination ? group.Select(row => row.Entry.ProcessId).Where(pid => pid.HasValue).Distinct().Count().ToString() : group.Key,
                        group.Count().ToString()])).ToArray());
        }
        finally { updating = false; }
        RenderDetails();
    }

    private void RenderDetails()
    {
        var rows = overview.Selected?.Value as LiveConnection[] ?? [];
        detailTitle.Text = overview.Selected is { } group ? $"{group.Cells[0]} の内訳" : "選択した項目の内訳";
        updating = true;
        try
        {
            details.Set(rows.Select(row => new ResultRow(row.Entry, row,
                [byDestination ? $"{row.Entry.ProcessId?.ToString() ?? "—"} / {row.Application}" : row.Address,
                    row.Entry.RemotePort.ToString(), State(row.Entry.State)])).ToArray());
        }
        finally { updating = false; }
        RenderEndpoint();
    }

    private void RenderEndpoint() => endpoint.Text = details.Selected?.Value is LiveConnection row
        ? $"{row.Application} · PID {row.Entry.ProcessId?.ToString() ?? "—"}  ·  {new IPEndPoint(row.Entry.LocalAddress, row.Entry.LocalPort)} → {new IPEndPoint(row.Entry.RemoteAddress, row.Entry.RemotePort)}"
        : "上のグラフと合計は選択回線全体。下の一覧は現在の TCP 接続。";

    internal static string Rate(double? bytesPerSecond) => bytesPerSecond.HasValue ? MainForm.FormatBitRate(bytesPerSecond.Value) : "—";

    private static string State(TcpState state) => state switch
    {
        TcpState.Established => "接続中",
        TcpState.SynSent or TcpState.SynReceived => "接続処理中",
        TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing
            or TcpState.LastAck or TcpState.TimeWait => "切断処理中",
        TcpState.Closed or TcpState.DeleteTcb => "切断済み",
        _ => state.ToString()
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }

    private sealed record LiveConnection(TcpConnectionInfo Entry, string Application)
    {
        internal string Address => (Entry.RemoteAddress.IsIPv4MappedToIPv6 ? Entry.RemoteAddress.MapToIPv4() : Entry.RemoteAddress).ToString();
    }
}
