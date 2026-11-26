using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SaruMon.Core;
using SaruMon.Monitoring;

namespace SaruMon;

internal sealed class TcpPeerView : UserControl
{
    private readonly Button start = new() { Text = "開始（管理者権限）", AutoSize = true };
    private readonly Button stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button save = new() { Text = "ログを保存…", AutoSize = true, Enabled = false };
    private readonly CheckBox reverse = new() { Text = "逆引き（ホスト名を表示）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        AccessibleName = "選択したインタフェースの相手別 TCP 送受信量"
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly TrafficMonitorClient client;
    private readonly ReverseDnsResolver resolver;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private IPAddress[] addresses = [];
    private string selectionKey = "";
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

    public TcpPeerView(TrafficMonitorClient? client = null, ReverseDnsResolver? resolver = null)
    {
        this.client = client ?? new TrafficMonitorClient(MonitoringMode.Tcp);
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
        AddColumn("RemoteIp", "相手 IP", 150, fill: true);
        AddColumn("HostName", "相手名（逆引き）", 180, fill: true);
        AddColumn("SentBytes", "上り合計（バイト）", 155);
        AddColumn("ReceivedBytes", "下り合計（バイト）", 155);
        foreach (var column in new[] { "SentBytes", "ReceivedBytes" })
        {
            grid.Columns[column]!.ValueType = typeof(long);
            grid.Columns[column]!.DefaultCellStyle.Format = "N0";
        }
        grid.Columns["HostName"]!.Visible = false;
        layout.Controls.Add(grid, 0, 1);
        status.Text = "停止中 · 「開始」で相手ごとの TCP 送受信量を観測します。";
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(104, 116, 134),
            Text = "開始後の TCP 送受信量を相手 IP ごとに合算します。ポートや接続が異なっても同じ相手にまとめます。\n停止後も保存できます。新しく開始するかインタフェースを変えると集計を消去します。\n逆引きは DNS に問い合わせます。取得済みの名前だけを CSV に保存し、未取得の場合は空欄にします。"
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
                row.Cells["HostName"].Value = reverse.Checked ? NameFor(((TcpPeerInfo)row.Tag!).RemoteAddress) : "";
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
        addresses = next;
        revision++;
        appliedRevision = -1;
        CancelDns();
        grid.Rows.Clear();
        status.Text = next.Length == 0 ? "利用できる IP アドレスがありません。" : "停止中 · 「開始」で相手ごとの TCP 送受信量を観測します。";
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
            if (!disposed) operationError = "TCP 観測を開始できません: " + ex.Message;
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
        status.Text = "TCP 観測を停止しています…";
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
            if (!disposed) operationError = "TCP 観測の停止を確認できません: " + ex.Message;
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
            Debug.WriteLine("SaruMon TCP stop: " + ex);
            if (!disposed) operationError = (operationError ?? "TCP 観測で問題が発生しました。") + " 停止確認: " + ex.Message;
        }
    }

    private void UpdateButtons()
    {
        if (disposed) return;
        start.Enabled = addresses.Length != 0 && !stopFailed && !monitorRequested && !client.IsRunning && pendingOperations == 0;
        stop.Enabled = (monitorRequested || client.IsRunning || stopFailed) && (monitorRequested || pendingOperations == 0);
        save.Enabled = !saving && appliedRevision == revision && pendingOperations == 0 && client.TcpSnapshot.Count > 0;
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
                    : pendingOperations != 0 ? "TCP 観測の状態を変更しています…" : "停止中 · 「開始」で相手ごとの TCP 送受信量を観測します。");
                return;
            }
            var peers = CurrentSnapshot();
            Render(peers);
            var limitation = client.Error ?? (client.DroppedEvents > 0
                ? $"保持上限2,048件のため、新規通信の {client.DroppedEvents:N0} イベントを記録していません。" : null);
            status.Text = operationError ?? limitation ?? $"{(client.IsRunning ? "監視中" : "停止中")} · {peers.Length} 件 · 集計を保持しています · {DateTime.Now:HH:mm:ss} 更新";
            UpdateButtons();
            StartDns();
        }
        catch (Exception ex)
        {
            CancelDns();
            grid.Rows.Clear();
            status.Text = "相手別 TCP 通信量を表示できません: " + ex.Message;
        }
    }

    private TcpPeerInfo[] CurrentSnapshot()
    {
        if (appliedRevision != revision || addresses.Length == 0) return [];
        return client.TcpSnapshot.OrderByDescending(peer => peer.LastSeen).ToArray();
    }

    private async Task SaveLogAsync()
    {
        if (disposed || saving || appliedRevision != revision || pendingOperations != 0) return;
        // Freeze the records and optional cached names before opening the dialog.
        // Monitoring may continue during the dialog and file write without changing this export.
        var peers = CurrentSnapshot();
        if (peers.Length == 0) return;
        Dictionary<string, string>? names = null;
        if (reverse.Checked)
        {
            names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var peer in peers)
                if (IPAddress.TryParse(peer.RemoteAddress, out var ip) && resolver.TryGetCached(Normalize(ip), out var name)
                    && name is not null)
                    names[peer.RemoteAddress] = name;
        }
        var partial = client.Error is not null || client.DroppedEvents > 0;
        using var dialog = new SaveFileDialog
        {
            Title = partial ? "相手別 TCP 通信量を保存（一部未記録の集計）" : "相手別 TCP 通信量を保存",
            Filter = "CSV ファイル (*.csv)|*.csv", DefaultExt = "csv",
            AddExtension = true, OverwritePrompt = true, RestoreDirectory = true,
            FileName = $"SaruMon-tcp-log-{DateTime.Now:yyyyMMdd-HHmmss}{(partial ? "-一部未記録" : "")}.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || disposed) return;
        var path = dialog.FileName;
        saving = true;
        UpdateButtons();
        status.Text = "通信ログを保存しています…";
        try
        {
            saveTask = Task.Run(() => TcpLogExporter.Write(path, peers, names));
            await saveTask;
            if (!disposed) status.Text = $"{peers.Length:N0} 件の TCP 通信量を保存しました{(partial ? "（一部未記録）" : "")}: {path}";
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

    private void Render(IReadOnlyList<TcpPeerInfo> peers)
    {
        var selected = grid.CurrentRow?.Tag as TcpPeerInfo;
        var scroll = grid.FirstDisplayedScrollingRowIndex;
        var sorted = grid.SortedColumn;
        var direction = grid.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var item in peers)
            {
                var index = grid.Rows.Add(item.RemoteAddress, reverse.Checked ? NameFor(item.RemoteAddress) : "",
                    item.SentBytes, item.ReceivedBytes);
                grid.Rows[index].Tag = item;
            }
            if (sorted is not null && sorted.Visible && grid.Rows.Count > 0) grid.Sort(sorted, direction);
            grid.ClearSelection();
            if (selected is not null)
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is TcpPeerInfo item && item.RemoteAddress == selected.RemoteAddress)
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
            var peers = grid.Rows.Cast<DataGridViewRow>().Select(row => ((TcpPeerInfo)row.Tag!).RemoteAddress)
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
                        if (IPAddress.TryParse(((TcpPeerInfo)row.Tag!).RemoteAddress, out var remote) && Normalize(remote).Equals(result.Address))
                            row.Cells["HostName"].Value = result.Name ?? "—";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine("SaruMon TCP reverse DNS: " + ex);
            if (!disposed && Visible && expected == revision && reverse.Checked)
                status.Text = "逆引きを完了できませんでした。相手 IP と通信量は引き続き表示します。";
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
        catch (Exception ex) { Debug.WriteLine("SaruMon TCP cleanup: " + ex); }
    }
}
