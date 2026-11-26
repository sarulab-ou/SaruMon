using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SaruMon.Core;

namespace SaruMon;

internal sealed class ConnectionsView : UserControl
{
    private readonly TabControl views = new() { Dock = DockStyle.Fill };
    private readonly TcpPeerView peerView = new();
    private readonly CheckBox reverse = new() { Text = "逆引き（ホスト名を表示）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button refresh = new() { Text = "再読み込み", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        AccessibleName = "選択したインタフェースの TCP 接続先"
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly ReverseDnsResolver resolver;
    private IPAddress[] addresses = [];
    private string selectionKey = "";
    private int revision;
    private bool reading;
    private bool refreshPending;
    private bool stopped;
    private CancellationTokenSource? dnsCancellation;

    public ConnectionsView(ReverseDnsResolver? resolver = null)
    {
        this.resolver = resolver ?? new ReverseDnsResolver();
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.Controls.Add(reverse, 0, 0);
        toolbar.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(toolbar, 0, 0);
        AddColumn("RemoteIp", "接続先 IP", 150, fill: true);
        AddColumn("RemotePort", "宛先ポート", 90);
        AddColumn("HostName", "逆引きホスト名", 160, fill: true);
        AddColumn("State", "状態", 115);
        AddColumn("Local", "ローカル側", 170, fill: true);
        grid.Columns["RemotePort"]!.ValueType = typeof(int);
        grid.Columns["HostName"]!.Visible = false;
        layout.Controls.Add(grid, 0, 1);
        status.Text = "選択したインタフェースの TCP 接続を表示します。";
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(104, 116, 134),
            Text = "現在の TCP 接続です。「相手ごとの通信量」で上り・下りの合計を記録し、CSV に保存できます。\n逆引きは DNS に問い合わせます。取得できない場合も IP とポートは表示します。"
        }, 0, 3);
        var currentPage = new TabPage("現在の接続") { BackColor = Color.White };
        var trafficPage = new TabPage("相手ごとの通信量") { BackColor = Color.White };
        currentPage.Controls.Add(layout);
        trafficPage.Controls.Add(peerView);
        views.TabPages.AddRange([currentPage, trafficPage]);
        Controls.Add(views);
        views.SelectedIndexChanged += (_, _) => UpdatePolling();
        refresh.Click += async (_, _) => await RefreshAsync();
        timer.Tick += async (_, _) => await RefreshAsync();
        reverse.CheckedChanged += (_, _) =>
        {
            CancelDns();
            grid.Columns["HostName"]!.Visible = reverse.Checked;
            foreach (DataGridViewRow row in grid.Rows)
                row.Cells["HostName"].Value = reverse.Checked ? NameFor(((TcpConnectionInfo)row.Tag!).RemoteAddress) : "";
            StartDns();
        };
    }

    private void AddColumn(string name, string heading, int width, bool fill = false) => grid.Columns.Add(
        new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = heading, Width = width, MinimumWidth = width,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.Automatic
        });

    public void SetInterface(TrafficInterface? adapter)
    {
        if (stopped) return;
        peerView.SetInterface(adapter);
        var next = adapter?.IsUp == true
            ? adapter.Addresses.Select(value => IPAddress.TryParse(value, out var ip) ? ip : null).OfType<IPAddress>().ToArray()
            : [];
        var key = adapter?.Id + "|" + string.Join(";", next.Select(ip => ip.ToString()).Order(StringComparer.Ordinal));
        if (key == selectionKey) return;
        selectionKey = key;
        addresses = next;
        revision++;
        CancelDns();
        grid.Rows.Clear();
        status.Text = next.Length == 0 ? "利用できる IP アドレスがありません。" : "接続一覧を取得中…";
        _ = RefreshAsync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePolling();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (stopped) return;
        if (Visible && IsHandleCreated && views.SelectedIndex == 0) { timer.Start(); _ = RefreshAsync(); }
        else { timer.Stop(); CancelDns(); }
    }

    private async Task RefreshAsync()
    {
        if (stopped || !Visible || !IsHandleCreated || views.SelectedIndex != 0) return;
        if (reading) { refreshPending = true; return; }
        reading = true;
        refresh.Enabled = false;
        var expected = revision;
        var selected = addresses;
        try
        {
            if (selected.Length == 0)
            {
                grid.Rows.Clear();
                status.Text = "利用できる IP アドレスがありません。";
                return;
            }
            var connections = await Task.Run(() => TcpConnections.Read(selected));
            if (stopped || !Visible || views.SelectedIndex != 0 || expected != revision) return;
            Render(connections);
            status.Text = $"{connections.Count} 接続 · {DateTime.Now:HH:mm:ss} 更新 · 2秒ごとに更新";
            StartDns();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException
            or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            if (!stopped && expected == revision)
            {
                CancelDns();
                grid.Rows.Clear();
                status.Text = "接続一覧を取得できません: " + ex.Message;
            }
        }
        finally
        {
            reading = false;
            if (!stopped)
            {
                refresh.Enabled = true;
                if (refreshPending) { refreshPending = false; _ = RefreshAsync(); }
            }
        }
    }

    private void Render(IReadOnlyList<TcpConnectionInfo> connections)
    {
        var selected = grid.CurrentRow?.Tag as TcpConnectionInfo;
        var scroll = grid.FirstDisplayedScrollingRowIndex;
        var sorted = grid.SortedColumn;
        var direction = grid.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var item in connections)
            {
                var index = grid.Rows.Add(item.RemoteAddress.ToString(), item.RemotePort,
                    reverse.Checked ? NameFor(item.RemoteAddress) : "", StateText(item.State),
                    new IPEndPoint(item.LocalAddress, item.LocalPort).ToString());
                grid.Rows[index].Tag = item;
            }
            if (sorted is not null && sorted.Visible && grid.Rows.Count > 0) grid.Sort(sorted, direction);
            grid.ClearSelection();
            if (selected is not null)
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is TcpConnectionInfo item && item.LocalAddress.Equals(selected.LocalAddress)
                        && item.LocalPort == selected.LocalPort && item.RemoteAddress.Equals(selected.RemoteAddress)
                        && item.RemotePort == selected.RemotePort)
                    {
                        grid.CurrentCell = row.Cells["RemoteIp"];
                        row.Selected = true;
                        break;
                    }
            if (scroll >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(scroll, grid.Rows.Count - 1);
        }
        finally { grid.ResumeLayout(); }
    }

    private string NameFor(IPAddress address) => resolver.TryGetCached(address, out var name) ? name ?? "—" : "確認中…";

    private void StartDns()
    {
        if (stopped || !Visible || views.SelectedIndex != 0 || !reverse.Checked || dnsCancellation is not null || grid.Rows.Count == 0) return;
        var cancellation = new CancellationTokenSource();
        dnsCancellation = cancellation;
        _ = ResolveNamesAsync(cancellation, revision);
    }

    private async Task ResolveNamesAsync(CancellationTokenSource cancellation, int expected)
    {
        try
        {
            // Snapshot the displayed peers. Four lookups at a time; never block table refreshes.
            var peers = grid.Rows.Cast<DataGridViewRow>().Select(row => ((TcpConnectionInfo)row.Tag!).RemoteAddress)
                .Distinct().Where(ip => !resolver.TryGetCached(ip, out _)).ToArray();
            foreach (var batch in peers.Chunk(4))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var names = await Task.WhenAll(batch.Select(async ip =>
                    (Address: ip, Name: await resolver.ResolveAsync(ip, cancellation.Token))));
                if (stopped || !Visible || views.SelectedIndex != 0 || !reverse.Checked || expected != revision || cancellation.IsCancellationRequested) return;
                foreach (DataGridViewRow row in grid.Rows)
                    foreach (var result in names)
                        if (((TcpConnectionInfo)row.Tag!).RemoteAddress.Equals(result.Address))
                            row.Cells["HostName"].Value = result.Name ?? "—";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            if (!stopped && expected == revision && reverse.Checked)
                status.Text = "逆引きを完了できませんでした。IP とポートは引き続き表示します。";
        }
        finally
        {
            if (ReferenceEquals(dnsCancellation, cancellation)) dnsCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelDns()
    {
        dnsCancellation?.Cancel();
        dnsCancellation = null;
    }

    private static string StateText(TcpState state) => state switch
    {
        TcpState.Established => "接続中", TcpState.SynSent => "接続要求中", TcpState.SynReceived => "接続応答中",
        TcpState.FinWait1 or TcpState.FinWait2 => "切断中", TcpState.CloseWait => "切断待ち",
        TcpState.Closing => "切断処理中", TcpState.LastAck => "最終応答待ち", TcpState.TimeWait => "終了待機",
        TcpState.Closed => "切断済み", _ => state.ToString()
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopped = true;
            timer.Stop();
            timer.Dispose();
            CancelDns();
        }
        base.Dispose(disposing);
    }

    public Task ShutdownAsync()
    {
        stopped = true;
        timer.Stop();
        CancelDns();
        return peerView.ShutdownAsync();
    }
}
