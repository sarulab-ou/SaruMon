using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using SaruMon.Core;
using SaruMon.Monitoring;
using static SaruMon.DashboardStyle;

namespace SaruMon;

internal sealed class TrafficOverview : UserControl
{
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly Func<IPAddress[], IReadOnlyList<TcpConnectionInfo>> readTcp;
    private readonly Func<IPAddress[], IReadOnlyList<UdpEndpointInfo>> readUdp;
    private readonly TrafficGraph graph = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
    private readonly Label receive = Caption();
    private readonly Label send = Caption();
    private readonly Label listTitle = Caption("接続を表示できる回線がありません");
    private readonly Label detailTitle = Caption("選択した項目の内訳");
    private readonly Label endpoint = Caption();
    private readonly Button applications = ActionButton("アプリ別", accent: true);
    private readonly Button destinations = ActionButton("宛先別");
    private readonly ResultGrid overview = new("TCP 接続と UDP ローカルポートの集計");
    private readonly ResultGrid details = new("選択した項目の内訳");
    private (string Id, string Addresses)? selection;
    private IPAddress[] addresses = [];
    private LiveSnapshot? snapshot;
    private bool byDestination;
    private bool updating;
    private bool reading;
    private int revision;

    internal TrafficOverview(Func<IPAddress[], IReadOnlyList<TcpConnectionInfo>>? readTcp = null,
        Func<IPAddress[], IReadOnlyList<UdpEndpointInfo>>? readUdp = null)
    {
        this.readTcp = readTcp ?? TcpConnections.ReadWithOwners;
        this.readUdp = readUdp ?? UdpEndpoints.ReadWithOwners;
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
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
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
        details.Control.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        details.Control.ShowCellToolTips = false;
        ProcessContextMenu.Attach(overview, Owners);
        ProcessContextMenu.Attach(details, Owners);
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
        snapshot = null;
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
            snapshot = current;
            RenderConnections();
        }
        finally { reading = false; }
    }

    private LiveSnapshot ReadConnections(IPAddress[] selected)
    {
        var capturedAt = DateTime.UtcNow;
        var tcp = ReadTable(readTcp, selected);
        var udp = ReadTable(readUdp, selected);
        var owners = (tcp ?? []).Select(row => row.ProcessId).Concat((udp ?? []).Select(row => row.ProcessId))
            .OfType<int>().Distinct().ToDictionary(pid => pid, pid => ReadOwner(pid, capturedAt));
        ProcessTarget Owner(int? pid) => pid is int value ? owners[value] : new ProcessTarget(default, "不明");
        var rows = (tcp ?? []).Select(row => new LiveSocket(row.LocalAddress, row.LocalPort,
                new IPEndPoint(row.RemoteAddress, row.RemotePort), row.State, Owner(row.ProcessId)))
            .Concat((udp ?? []).Select(row => new LiveSocket(row.LocalAddress, row.LocalPort, null,
                null, Owner(row.ProcessId)))).ToArray();
        return new LiveSnapshot(rows, tcp?.Count, udp?.Count);
    }

    private static IReadOnlyList<T>? ReadTable<T>(Func<IPAddress[], IReadOnlyList<T>> read, IPAddress[] selected)
    {
        try { return read(selected); }
        catch (Exception ex) when (ex is NetworkInformationException or IOException or InvalidDataException) { return null; }
    }

    private static ProcessTarget ReadOwner(int pid, DateTime capturedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var started = process.StartTime.ToUniversalTime();
            if (started <= capturedAt) return new ProcessTarget(new ProcessIdentity(pid, started.Ticks), process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        return new ProcessTarget(new ProcessIdentity(pid, 0), "不明");
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
        listTitle.Text = snapshot is { } current
            ? $"TCP 接続 {CountLabel(current.TcpCount)}" + (byDestination ? "" : $" · UDP ローカル {CountLabel(current.UdpCount)}")
            : addresses.Length == 0 ? "接続を表示できる回線がありません" : "接続とポートを取得中…";
        updating = true;
        try
        {
            var rows = snapshot?.Rows ?? [];
            if (byDestination)
            {
                overview.Headers(("宛先IP", 175), ("PID数", 58), ("接続数", 72));
                details.Headers(("PID / アプリ", 120), ("ポート", 77), ("件数", 50), ("状態別の件数", 158));
                overview.Set(Group(rows.Where(row => !row.IsUdp), row => row.Address)
                    .Select(group => new ResultRow(group.Key, group.ToArray(),
                        [group.Key, group.Select(row => row.ProcessId).OfType<int>().Distinct().Count().ToString(), group.Count().ToString()])).ToArray());
            }
            else
            {
                overview.Headers(("アプリ", 160), ("PID", 60), ("TCP", 50), ("UDP", 50));
                details.Headers(("IP アドレス", 120), ("ポート", 77), ("件数", 50), ("状態別の件数", 158));
                overview.Set(Group(rows, row => row.PidLabel)
                    .Select(group => new ResultRow(group.Key, group.ToArray(),
                        [group.First().Application, group.Key, CountRows(group.Count(row => !row.IsUdp), snapshot?.TcpCount),
                            CountRows(group.Count(row => row.IsUdp), snapshot?.UdpCount)])).ToArray());
            }
            details.Control.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        }
        finally { updating = false; }
        RenderDetails();
    }

    private static string CountLabel(int? count) => count is int value ? $"{value:N0} 件" : "取得不可";

    private static string CountRows(int count, int? total) => total.HasValue ? count.ToString("N0") : "—";

    private static IOrderedEnumerable<IGrouping<string, LiveSocket>> Group(IEnumerable<LiveSocket> rows, Func<LiveSocket, string> key) =>
        rows.GroupBy(key, StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal);

    private static IEnumerable<ProcessTarget> Owners(object value) =>
        value is LiveSocket[] rows ? rows.Select(row => row.Owner) : [];

    private void RenderDetails()
    {
        var rows = overview.Selected?.Value as LiveSocket[] ?? [];
        detailTitle.Text = overview.Selected is { } group ? $"{group.Cells[0]} の内訳" : "選択した項目の内訳";
        updating = true;
        try
        {
            var grouped = byDestination
                ? Group(rows, row => row.PidLabel).Select(group =>
                    DetailRow(group.First().Owner.Identity, $"{group.Key} / {group.First().Application}", group.ToArray()))
                : rows.GroupBy(row => (row.IsUdp, row.Address))
                    .OrderBy(group => group.Key.IsUdp).ThenByDescending(group => group.Count())
                    .ThenBy(group => group.Key.Address, StringComparer.Ordinal)
                    .Select(group => DetailRow(group.Key, group.Key.Address, group.ToArray()));
            details.Set(grouped.ToArray());
        }
        finally { updating = false; }
        RenderEndpoint();
    }

    private static ResultRow DetailRow(object key, string label, LiveSocket[] rows)
    {
        var ports = string.Join(", ", rows.Select(row => row.Port).Distinct().Order());
        var counts = rows[0].IsUdp
            ? $"UDP · {rows[0].UdpScope}"
            : string.Join(Environment.NewLine, rows.GroupBy(row => State(row.TcpState.GetValueOrDefault()))
                .OrderBy(group => group.Key.Order).Select(group => $"{group.Key.Label} {group.Count():N0}"));
        return new ResultRow(key, rows, [label, ports, rows.Length.ToString("N0"), counts]);
    }

    private void RenderEndpoint()
    {
        if (details.Selected is not { Value: LiveSocket[] rows, Cells: var cells })
        {
            endpoint.Text = "UDP の一覧はアプリ別で確認できます。";
            return;
        }

        var row = rows[0];
        var scope = row.IsUdp ? "UDP ローカル" : "TCP 宛先";
        var location = rows.Length == 1 && row.Remote is { } remote
            ? $"{new IPEndPoint(row.LocalAddress, row.LocalPort)} → {remote}"
            : $"{row.Address} · ポート {cells[1]}";
        var counts = row.IsUdp ? row.UdpScope : cells[3].Replace(Environment.NewLine, " / ", StringComparison.Ordinal);
        endpoint.Text = $"{scope} {location} · {cells[2]} 件 · {counts} · {row.Application} · PID {row.PidLabel}";
    }

    internal static string Rate(double? bytesPerSecond) => bytesPerSecond.HasValue ? MainForm.FormatBitRate(bytesPerSecond.Value) : "—";

    private static (int Order, string Label) State(TcpState state) => state switch
    {
        TcpState.Established => (0, "接続中"),
        TcpState.SynSent or TcpState.SynReceived => (1, "接続処理中"),
        TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing
            or TcpState.LastAck or TcpState.TimeWait => (2, "切断処理中"),
        TcpState.Closed or TcpState.DeleteTcb => (3, "切断済み"),
        _ => (4, "その他")
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }

    private sealed record LiveSnapshot(LiveSocket[] Rows, int? TcpCount, int? UdpCount);

    private sealed record LiveSocket(IPAddress LocalAddress, int LocalPort, IPEndPoint? Remote, TcpState? TcpState, ProcessTarget Owner)
    {
        internal bool IsUdp => Remote is null;
        internal int? ProcessId => Owner.Identity.Pid > 0 ? Owner.Identity.Pid : null;
        internal string Application => Owner.Name;
        internal string PidLabel => ProcessId?.ToString() ?? "—";
        internal int Port => Remote?.Port ?? LocalPort;
        internal string Address
        {
            get
            {
                var address = Remote?.Address ?? LocalAddress;
                return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            }
        }
        internal string UdpScope => LocalAddress.Equals(IPAddress.Any) || LocalAddress.Equals(IPAddress.Any.MapToIPv6()) ? "全 IPv4"
            : LocalAddress.Equals(IPAddress.IPv6Any) ? "全 IPv6"
            : "ローカル";
    }
}
