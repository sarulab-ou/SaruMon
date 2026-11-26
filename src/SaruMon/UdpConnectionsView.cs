using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SaruMon.Core;
using SaruMon.Monitoring;

namespace SaruMon;

internal sealed class UdpConnectionsView : UserControl
{
    private readonly Button start = new() { Text = "開始（管理者権限）", AutoSize = true };
    private readonly Button stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button save = new() { Text = "保存", AutoSize = true, Enabled = false };
    private readonly CheckBox reverse = new() { Text = "逆引き（ホスト名を表示）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        AccessibleName = "選択したインタフェースで観測した UDP 送信先と累積送信量"
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly TrafficMonitorClient client;
    private readonly ReverseDnsResolver resolver;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private IPAddress[] addresses = [];
    private string selectionKey = "";
    private string interfaceName = "";
    private int revision;
    private int appliedRevision = -1;
    private int pendingOperations;
    private bool monitorRequested;
    private bool stopFailed;
    private bool disposed;
    private bool resourcesDisposed;
    private bool saving;
    private Task? saveTask;
    private Task? shutdownTask;
    private string? operationError;
    private CancellationTokenSource? startCancellation;
    private CancellationTokenSource? dnsCancellation;

    public UdpConnectionsView(TrafficMonitorClient? client = null, ReverseDnsResolver? resolver = null)
    {
        this.client = client ?? new TrafficMonitorClient(MonitoringMode.Udp);
        this.resolver = resolver ?? new ReverseDnsResolver();
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        start.Margin = new Padding(0, 0, 8, 0);
        stop.Margin = new Padding(0, 0, 8, 0);
        save.Margin = new Padding(0, 0, 18, 0);
        reverse.Margin = new Padding(0, 7, 0, 0);
        toolbar.Controls.AddRange([start, stop, save, reverse]);
        layout.Controls.Add(toolbar, 0, 0);
        AddColumn("RemoteIp", "送信先 IP", 150, fill: true);
        AddColumn("RemotePort", "宛先ポート", 90);
        AddColumn("HostName", "逆引きホスト名", 160, fill: true);
        AddColumn("FirstSeen", "開始時刻", 135);
        AddColumn("Bytes", "累積送信バイト", 125);
        AddColumn("Packets", "送信数", 90);
        AddColumn("LastSeen", "最終時刻", 135);
        AddColumn("Local", "ローカル側", 170, fill: true);
        grid.Columns["RemotePort"]!.ValueType = typeof(int);
        foreach (var column in new[] { "Bytes", "Packets" })
        {
            grid.Columns[column]!.ValueType = typeof(long);
            grid.Columns[column]!.DefaultCellStyle.Format = "N0";
        }
        grid.Columns["FirstSeen"]!.ValueType = typeof(DateTimeOffset);
        grid.Columns["FirstSeen"]!.DefaultCellStyle.Format = "MM/dd HH:mm:ss";
        grid.Columns["LastSeen"]!.ValueType = typeof(DateTimeOffset);
        grid.Columns["LastSeen"]!.DefaultCellStyle.Format = "MM/dd HH:mm:ss";
        grid.Columns["FirstSeen"]!.HeaderCell.ToolTipText = "この宛先への送信を監視中に最初に観測した時刻です。";
        grid.Columns["LastSeen"]!.HeaderCell.ToolTipText = "この宛先への送信を監視中に最後に観測した時刻です。";
        grid.Columns["HostName"]!.Visible = false;
        layout.Controls.Add(grid, 0, 1);
        status.Text = "停止中 · 「開始」で UDP の送信先と送信量を観測します。";
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(104, 116, 134),
            Text = "開始後の UDP 送信を宛先ごとに最大2,048件保持します。受信・開始前の通信は含みません。\n停止後も一覧を保存できます。新しく開始するかインタフェースを変えると集計を消去します。\n開始・最終は最初と最後の送信観測時刻です。逆引きの名前は閲覧サイト名とは限りません。"
        }, 0, 3);
        Controls.Add(layout);
        start.Click += async (_, _) => await StartMonitoringAsync();
        stop.Click += async (_, _) => await StopMonitoringAsync();
        save.Click += async (_, _) => await SaveLogAsync();
        timer.Tick += (_, _) => RefreshSnapshot();
        reverse.CheckedChanged += (_, _) =>
        {
            CancelDns();
            grid.Columns["HostName"]!.Visible = reverse.Checked;
            foreach (DataGridViewRow row in grid.Rows)
                row.Cells["HostName"].Value = reverse.Checked ? NameFor(((UdpFlowInfo)row.Tag!).RemoteAddress) : "";
            StartDns();
        };
        UpdateButtons();
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
        if (disposed) return;
        var next = adapter?.IsUp == true
            ? adapter.Addresses.Select(value => IPAddress.TryParse(value, out var ip) ? Normalize(ip) : null)
                .OfType<IPAddress>().Distinct().ToArray()
            : [];
        var key = adapter?.Id + "|" + string.Join(";", next.Select(ip => ip.ToString()).Order(StringComparer.Ordinal));
        if (key == selectionKey) return;
        selectionKey = key;
        interfaceName = adapter?.Name ?? "";
        addresses = next;
        revision++;
        appliedRevision = -1;
        CancelDns();
        grid.Rows.Clear();
        status.Text = next.Length == 0 ? "利用できる IP アドレスがありません。" : "停止中 · 「開始」で UDP の送信先と送信量を観測します。";
        UpdateButtons();
        // Even while this tab is hidden, the helper must follow the selected interface.
        _ = ApplyInterfaceAsync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (disposed) return;
        if (Visible && IsHandleCreated) { timer.Start(); RefreshSnapshot(); }
        else { timer.Stop(); CancelDns(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Visible && !disposed) { timer.Start(); RefreshSnapshot(); }
    }

    private async Task StartMonitoringAsync()
    {
        if (disposed || stopFailed || monitorRequested || client.IsRunning || pendingOperations != 0 || addresses.Length == 0) return;
        monitorRequested = true;
        operationError = null;
        CancelDns();
        grid.Rows.Clear();
        appliedRevision = -1;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startCancellation = cancellation;
        pendingOperations++;
        UpdateButtons();
        status.Text = "管理者権限の確認を待っています…";
        var acquired = false;
        try
        {
            await operations.WaitAsync(cancellation.Token);
            acquired = true;
            var expected = revision;
            await client.StartAsync(addresses, cancellation.Token);
            if (!monitorRequested || disposed) { await client.StopAsync(); return; }
            // Adapter changes during the elevation prompt must not expose the old selection.
            if (expected == revision) appliedRevision = expected;
            await ApplyLatestAddressesAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            monitorRequested = false;
            if (acquired) await StopAfterFailureAsync();
            if (!disposed) status.Text = "観測の開始をキャンセルしました。";
        }
        catch (Exception ex)
        {
            monitorRequested = false;
            if (!disposed) operationError = "UDP 観測を開始できません: " + ex.Message;
            if (acquired) await StopAfterFailureAsync();
        }
        finally
        {
            if (ReferenceEquals(startCancellation, cancellation)) startCancellation = null;
            cancellation.Dispose();
            if (acquired) operations.Release();
            pendingOperations--;
            if (!disposed) { UpdateButtons(); RefreshSnapshot(); }
        }
    }

    private async Task ApplyInterfaceAsync()
    {
        pendingOperations++;
        UpdateButtons();
        var acquired = false;
        try
        {
            await operations.WaitAsync(lifetime.Token);
            acquired = true;
            if (!disposed)
                await ApplyLatestAddressesAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            monitorRequested = false;
            if (!disposed) operationError = "対象の切り替えに失敗したため観測を停止しました: " + ex.Message;
            if (acquired) await StopAfterFailureAsync();
        }
        finally
        {
            if (acquired) operations.Release();
            pendingOperations--;
            if (!disposed) { UpdateButtons(); RefreshSnapshot(); }
        }
    }

    private async Task ApplyLatestAddressesAsync(CancellationToken cancellationToken)
    {
        while (!disposed && appliedRevision != revision)
        {
            var expected = revision;
            await client.SetLocalAddressesAsync(addresses, cancellationToken);
            if (expected == revision) appliedRevision = expected;
        }
    }

    private async Task StopMonitoringAsync()
    {
        if (disposed) return;
        monitorRequested = false;
        startCancellation?.Cancel();
        CancelDns();
        operationError = null;
        pendingOperations++;
        UpdateButtons();
        status.Text = "UDP 観測を停止しています…";
        var acquired = false;
        try
        {
            await operations.WaitAsync(lifetime.Token);
            acquired = true;
            await client.StopAsync();
            stopFailed = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            stopFailed = true;
            if (!disposed) operationError = "UDP 観測の停止を確認できません: " + ex.Message;
        }
        finally
        {
            if (acquired) operations.Release();
            pendingOperations--;
            if (!disposed) { UpdateButtons(); RefreshSnapshot(); }
        }
    }

    private async Task StopAfterFailureAsync()
    {
        try { await client.StopAsync(); stopFailed = false; }
        catch (Exception ex)
        {
            stopFailed = true;
            Debug.WriteLine("SaruMon UDP stop: " + ex);
            if (!disposed) operationError = (operationError ?? "UDP 観測で問題が発生しました。") + " 停止確認: " + ex.Message;
        }
    }

    private void UpdateButtons()
    {
        if (disposed) return;
        start.Enabled = addresses.Length != 0 && !stopFailed && !monitorRequested && !client.IsRunning && pendingOperations == 0;
        stop.Enabled = (monitorRequested || client.IsRunning || stopFailed) && (monitorRequested || pendingOperations == 0);
        save.Enabled = !saving && appliedRevision == revision && pendingOperations == 0 && client.UdpSnapshot.Count > 0;
    }

    private void RefreshSnapshot()
    {
        if (disposed || !Visible || !IsHandleCreated) return;
        try
        {
            UpdateButtons();
            if (!client.IsRunning && pendingOperations == 0) monitorRequested = false;
            if (appliedRevision != revision || addresses.Length == 0)
            {
                CancelDns();
                grid.Rows.Clear();
                status.Text = operationError ?? client.Error ?? (addresses.Length == 0 ? "利用できる IP アドレスがありません。"
                    : pendingOperations != 0 ? "UDP 観測の状態を変更しています…" : "停止中 · 「開始」で UDP の送信先と送信量を観測します。");
                return;
            }
            var flows = CurrentSnapshot();
            Render(flows);
            var limitation = client.Error ?? (client.DroppedEvents > 0
                ? $"保持上限2,048件のため、新規通信の {client.DroppedEvents:N0} イベントを記録していません。" : null);
            status.Text = operationError ?? limitation ?? $"{(client.IsRunning ? "監視中" : "停止中")} · {flows.Length} 件 · 集計を保持しています · {DateTime.Now:HH:mm:ss} 更新";
            UpdateButtons();
            StartDns();
        }
        catch (Exception ex)
        {
            CancelDns();
            grid.Rows.Clear();
            status.Text = "UDP 送信先の一覧を表示できません: " + ex.Message;
        }
    }

    private UdpFlowInfo[] CurrentSnapshot()
    {
        if (appliedRevision != revision || addresses.Length == 0) return [];
        var selected = addresses.ToHashSet();
        return client.UdpSnapshot.Where(flow => IPAddress.TryParse(flow.LocalAddress, out var local)
            && selected.Contains(Normalize(local))).OrderByDescending(flow => flow.LastSeen).ToArray();
    }

    private async Task SaveLogAsync()
    {
        if (disposed || saving || appliedRevision != revision || pendingOperations != 0) return;
        // Freeze the records and optional cached names before opening the dialog.
        // Monitoring may continue during the dialog and file write without changing this export.
        var flows = CurrentSnapshot();
        if (flows.Length == 0) return;
        var selectedInterface = interfaceName;
        Dictionary<string, string>? names = null;
        if (reverse.Checked)
        {
            names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var flow in flows)
                if (IPAddress.TryParse(flow.RemoteAddress, out var ip) && resolver.TryGetCached(Normalize(ip), out var name)
                    && name is not null)
                    names[flow.RemoteAddress] = name;
        }
        var observationStatus = client.Error ?? (client.DroppedEvents > 0
            ? $"保持上限により新規通信の {client.DroppedEvents} イベントは未記録"
            : client.IsRunning ? "監視中に観測した通信のみ" : "停止時までに観測した通信のみ");
        using var dialog = new SaveFileDialog
        {
            Title = "UDP 送信ログを保存", Filter = "CSV ファイル (*.csv)|*.csv", DefaultExt = "csv",
            AddExtension = true, OverwritePrompt = true, RestoreDirectory = true,
            FileName = $"SaruMon-udp-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || disposed) return;
        var path = dialog.FileName;
        saving = true;
        UpdateButtons();
        status.Text = "通信ログを保存しています…";
        try
        {
            saveTask = Task.Run(() => ConnectionLogExporter.Write(path, flows, selectedInterface, names, observationStatus));
            await saveTask;
            if (!disposed) status.Text = $"{flows.Length:N0} 件の通信ログを保存しました: {path}";
        }
        catch (Exception ex)
        {
            if (!disposed)
                MessageBox.Show(this, "通信ログを保存できませんでした。\n" + ex.Message, "SaruMon",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            saving = false;
            if (!disposed) UpdateButtons();
        }
    }

    private void Render(IReadOnlyList<UdpFlowInfo> flows)
    {
        var selected = grid.CurrentRow?.Tag as UdpFlowInfo;
        var scroll = grid.FirstDisplayedScrollingRowIndex;
        var sorted = grid.SortedColumn;
        var direction = grid.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var item in flows)
            {
                var index = grid.Rows.Add(item.RemoteAddress, item.RemotePort, reverse.Checked ? NameFor(item.RemoteAddress) : "",
                    item.FirstSeen.ToLocalTime(), item.Bytes, item.Packets,
                    item.LastSeen.ToLocalTime(), Endpoint(item.LocalAddress, item.LocalPort));
                grid.Rows[index].Tag = item;
            }
            if (sorted is not null && sorted.Visible && grid.Rows.Count > 0) grid.Sort(sorted, direction);
            grid.ClearSelection();
            if (selected is not null)
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is UdpFlowInfo item && item.LocalAddress == selected.LocalAddress && item.LocalPort == selected.LocalPort
                        && item.RemoteAddress == selected.RemoteAddress && item.RemotePort == selected.RemotePort)
                    {
                        grid.CurrentCell = row.Cells["RemoteIp"];
                        row.Selected = true;
                        break;
                    }
            if (scroll >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(scroll, grid.Rows.Count - 1);
        }
        finally { grid.ResumeLayout(); }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
        // ETW network events do not carry an IPv6 scope identifier.
        : address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;
    private static string Endpoint(string address, int port) => IPAddress.TryParse(address, out var ip)
        ? new IPEndPoint(ip, port).ToString() : address + ":" + port;
    private string NameFor(string address) => !IPAddress.TryParse(address, out var ip) ? "—"
        : resolver.TryGetCached(ip, out var name) ? name ?? "—" : "確認中…";

    private void StartDns()
    {
        if (disposed || !Visible || !IsHandleCreated || !reverse.Checked || dnsCancellation is not null || grid.Rows.Count == 0) return;
        var cancellation = new CancellationTokenSource();
        dnsCancellation = cancellation;
        _ = ResolveNamesAsync(cancellation, revision);
    }

    private async Task ResolveNamesAsync(CancellationTokenSource cancellation, int expected)
    {
        try
        {
            var peers = grid.Rows.Cast<DataGridViewRow>().Select(row => ((UdpFlowInfo)row.Tag!).RemoteAddress)
                .Select(value => IPAddress.TryParse(value, out var ip) ? Normalize(ip) : null).OfType<IPAddress>()
                .Distinct().Where(ip => !resolver.TryGetCached(ip, out _)).ToArray();
            foreach (var batch in peers.Chunk(4))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var names = await Task.WhenAll(batch.Select(async ip =>
                    (Address: ip, Name: await resolver.ResolveAsync(ip, cancellation.Token))));
                if (disposed || !Visible || !reverse.Checked || expected != revision || cancellation.IsCancellationRequested) return;
                foreach (DataGridViewRow row in grid.Rows)
                    foreach (var result in names)
                        if (IPAddress.TryParse(((UdpFlowInfo)row.Tag!).RemoteAddress, out var remote) && Normalize(remote).Equals(result.Address))
                            row.Cells["HostName"].Value = result.Name ?? "—";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine("SaruMon UDP reverse DNS: " + ex);
            if (!disposed && Visible && expected == revision && reverse.Checked)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            // The normal form-exit path awaits ShutdownAsync. This fallback also observes
            // all failures when a control is disposed without that orderly exit path.
            _ = ObserveShutdownAsync(ShutdownAsync());
            timer.Dispose();
        }
        base.Dispose(disposing);
    }

    public Task ShutdownAsync()
    {
        if (shutdownTask is not null && !shutdownTask.IsFaulted && !shutdownTask.IsCanceled) return shutdownTask;
        if (!disposed)
        {
            disposed = true;
            monitorRequested = false;
            timer.Stop();
            CancelDns();
            lifetime.Cancel();
            if (!IsDisposed && !Disposing)
            {
                start.Enabled = false;
                stop.Enabled = false;
                save.Enabled = false;
                reverse.Enabled = false;
            }
        }
        return shutdownTask = ShutdownClientAsync();
    }

    private async Task ShutdownClientAsync()
    {
        // Continue off the UI context so fallback cleanup is not tied to its message loop.
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await client.StopAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            if (saveTask is not null)
            {
                try { await saveTask.ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine("SaruMon log save during shutdown: " + ex); }
            }
            lifetime.Dispose();
        }
        finally { operations.Release(); }
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine("SaruMon UDP cleanup: " + ex); }
    }
}
