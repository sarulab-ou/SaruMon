using System.Diagnostics;
using System.Net.NetworkInformation;
using SaruMon.Core;
using SaruMon.Monitoring;
using SaruMon.Updates;

namespace SaruMon;

internal sealed class MainForm : Form
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly TrafficSampler sampler = new();
    private readonly TcpIndicatorSampler tcpIndicatorSampler = new();
    private readonly TcpTrafficMeter tcpTrafficMeter = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TrafficIcons icons = new(SystemInformation.SmallIconSize.Width);
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 12, Dock = DockStyle.Fill, AccessibleName = "監視するインタフェース" };
    private readonly Label adapterDetail = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(104, 116, 134) };
    private readonly Label connection = new() { AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(10, 8, 10, 8) };
    private readonly Label receiveRate = RateLabel();
    private readonly Label sendRate = RateLabel();
    private readonly Label receiveTotal = DetailLabel();
    private readonly Label sendTotal = DetailLabel();
    private readonly Label footer = DetailLabel();
    private readonly TrafficGraph graph = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
    private readonly ConnectionsView connections = new();
    private readonly UdpConnectionsView udpConnections = new();
    private readonly ToolTip tips = new();
    private readonly CheckBox unicastOnly = new() { Text = "ユニキャスト通信だけで棒を表示", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ToolStripMenuItem unicastOnlyMenu = new("ユニキャスト通信だけで棒を表示") { CheckOnClick = true };
    private readonly CheckBox tcpIndicator = new() { Text = "TCP通信の目安で棒を表示", AutoSize = true };
    private readonly ToolStripMenuItem tcpIndicatorMenu = new("TCP通信の目安で棒を表示") { CheckOnClick = true };
    private readonly CheckBox tcpOnly = new() { Text = "TCP通信だけを表示（管理者権限）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ToolStripMenuItem tcpOnlyMenu = new("TCP通信だけを表示（管理者権限）");
    private readonly bool startHidden;
    private TrafficSample? latestSample;
    private TcpActivity? latestTcpActivity;
    private TcpTrafficDisplay? tcpTraffic;
    private long? displayedTcpRevision;
    private bool tcpOnlyMode;
    private bool changingTcpMode;
    private bool refreshing;
    private bool exiting;
    private bool shuttingDown;
    private bool notified;
    private int tick;

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        Text = "SaruMon — 通信モニター";
        Name = "SaruMonMainWindow";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(860, 700);
        MinimumSize = new Size(800, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(31, 44, 63);
        Icon = icons.Idle;

        var menu = new MenuStrip { BackColor = Color.White, Padding = new Padding(12, 5, 0, 5) };
        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add("通知領域に格納", null, (_, _) => HideToTray());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("終了(&X)", null, (_, _) => RequestExit());
        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add("使い方", null, (_, _) => ShowHelp());
        help.DropDownItems.Add("アイコンを常に表示するには", null, (_, _) => ShowTrayHelp());
        help.DropDownItems.Add("更新を確認(&U)…", null, (_, _) => OpenUpdates());
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("SaruMon について", null, (_, _) => MessageBox.Show(this,
            $"SaruMon {UpdateService.CurrentVersion}\n\nインタフェースごとの通信量、TCP・UDP の監視と CSV 保存ができます。\nパケットの内容は取得しません。\n\n実行場所:\n{Environment.ProcessPath}", "SaruMon について", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, help]);
        MainMenuStrip = menu;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 14), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 64, 28, 40, 48 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "SaruMon", Font = new Font("Yu Gothic UI", 26, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        header.Controls.Add(connection, 1, 0);
        body.Controls.Add(header, 0, 0);
        body.Controls.Add(new Label { Text = "監視するインタフェース", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var choose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        choose.Controls.Add(adapter, 0, 0);
        var refresh = new Button { Text = "再読み込み", Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 3) };
        refresh.Click += (_, _) => RefreshAdapters();
        choose.Controls.Add(refresh, 1, 0);
        body.Controls.Add(choose, 0, 2);
        body.Controls.Add(adapterDetail, 0, 3);

        var rates = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        rates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rates.Controls.Add(CreateRateCard("↓ 受信", TrafficIcons.ReceiveColor, receiveRate, receiveTotal, new Padding(0, 0, 7, 0)), 0, 0);
        rates.Controls.Add(CreateRateCard("↑ 送信", TrafficIcons.SendColor, sendRate, sendTotal, new Padding(7, 0, 0, 0)), 1, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var trafficPage = new TabPage("通信量") { BackColor = BackColor };
        var peersPage = new TabPage("接続先（TCP）") { BackColor = Color.White };
        var detailPage = new TabPage("UDP 送信先") { BackColor = Color.White };
        var trafficLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6), Margin = Padding.Empty };
        trafficLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        trafficLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        trafficLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        trafficLayout.Controls.Add(rates, 0, 0);
        trafficLayout.Controls.Add(graph, 0, 1);
        var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
        foreach (var (state, text) in new[] { (TrafficState.Receiving, "↓ 受信"), (TrafficState.Sending, "↑ 送信"), (TrafficState.Idle, "□ 通信なし"), (TrafficState.Offline, "× 未接続") })
            legend.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = TrafficIcons.ColorFor(state), Margin = new Padding(0, 0, 18, 0) });
        tips.SetToolTip(legend, "アイコンの左は受信（緑、上から下）、右は送信（赤、下から上）です。棒の高さは1秒ごとの通信量に応じて変わります。");
        trafficLayout.Controls.Add(legend, 0, 2);
        trafficPage.Controls.Add(trafficLayout);
        peersPage.Controls.Add(connections);
        detailPage.Controls.Add(udpConnections);
        tabs.TabPages.AddRange([trafficPage, peersPage, detailPage]);
        body.Controls.Add(tabs, 0, 4);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        options.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        options.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        var startup = new CheckBox { Text = "Windows ログイン時に起動", AutoSize = true, Anchor = AnchorStyles.Left };
        try { startup.Checked = AppSettings.StartWithWindows; }
        catch (System.Security.SecurityException) { startup.Enabled = false; }
        startup.CheckedChanged += (_, _) =>
        {
            try { AppSettings.StartWithWindows = startup.Checked; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                MessageBox.Show(this, "自動起動の設定を変更できませんでした。\n" + ex.Message, "SaruMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        tips.SetToolTip(startup, "次回のログインから通知領域で起動します。実行ファイルを移動した場合は設定し直してください。");
        var hide = new Button { Text = "通知領域に格納", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2) };
        hide.Click += (_, _) => HideToTray();
        var mainOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        startup.Margin = new Padding(3, 7, 15, 0);
        tcpOnly.Margin = new Padding(3, 7, 3, 0);
        mainOptions.Controls.AddRange([startup, tcpOnly]);
        options.Controls.Add(mainOptions, 0, 0);
        options.Controls.Add(hide, 1, 0);
        unicastOnly.Checked = settings.UnicastOnlyIndicator;
        unicastOnlyMenu.Checked = settings.UnicastOnlyIndicator;
        tips.SetToolTip(unicastOnly, "ユニキャスト通信があった方向だけ棒を表示します。高さはその方向の全通信量です。\n管理者権限は不要です。");
        tcpIndicator.Checked = settings.TcpActivityIndicator;
        tcpIndicatorMenu.Checked = settings.TcpActivityIndicator;
        tips.SetToolTip(tcpIndicator, "選択した回線とPC全体のTCPで、同じ計測区間に通信が増えた方向を表示します。\nPC内のTCPと回線上のUDPが重なる場合も点灯します。管理者権限は不要です。");
        var indicatorOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        indicatorOptions.Controls.AddRange([unicastOnly, tcpIndicator]);
        options.Controls.Add(indicatorOptions, 0, 1);
        options.SetColumnSpan(indicatorOptions, 2);
        tips.SetToolTip(tcpOnly, "選択したインタフェースのローカルIPでTCPを観測し、速度・合計・グラフ・通知領域の棒に表示します。\n相手ごとのTCP監視と同じ観測を使い、このPC内で完結する通信は表示から除きます。");
        body.Controls.Add(options, 0, 5);
        footer.Text = "1秒ごとに更新 · × を押しても監視を続けます";
        footer.Dock = DockStyle.Fill;
        footer.TextAlign = ContentAlignment.MiddleLeft;
        body.Controls.Add(footer, 0, 6);
        Controls.Add(body);
        Controls.Add(menu);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("SaruMon を開く", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(unicastOnlyMenu);
        trayMenu.Items.Add(tcpIndicatorMenu);
        trayMenu.Items.Add(tcpOnlyMenu);
        trayMenu.Items.Add("アイコンを常に表示するには", null, (_, _) => { ShowFromTray(); ShowTrayHelp(); });
        trayMenu.Items.Add("更新を確認…", null, (_, _) => { ShowFromTray(); OpenUpdates(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("終了", null, (_, _) => RequestExit());
        tray = new NotifyIcon { Icon = icons.Idle, Text = "SaruMon", ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        unicastOnly.CheckedChanged += (_, _) => SetUnicastOnlyIndicator(unicastOnly.Checked);
        unicastOnlyMenu.CheckedChanged += (_, _) => SetUnicastOnlyIndicator(unicastOnlyMenu.Checked);
        tcpIndicator.CheckedChanged += (_, _) => SetTcpActivityIndicator(tcpIndicator.Checked);
        tcpIndicatorMenu.CheckedChanged += (_, _) => SetTcpActivityIndicator(tcpIndicatorMenu.Checked);
        tcpOnly.CheckedChanged += async (_, _) => await SetTcpOnlyModeAsync(tcpOnly.Checked);
        tcpOnlyMenu.Click += async (_, _) => await SetTcpOnlyModeAsync(!tcpOnlyMode);
        adapter.SelectedIndexChanged += (_, _) => SelectAdapter();
        RefreshAdapters();
        timer.Tick += (_, _) => Poll();
        timer.Start();
        Poll();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startHidden) Hide();
    }

    private void RefreshAdapters()
    {
        if (adapter.DroppedDown) return;
        var selectedId = (adapter.SelectedItem as AdapterChoice)?.Id ?? settings.InterfaceId;
        try
        {
            var available = TrafficNetwork.GetInterfaces();
            refreshing = true;
            adapter.BeginUpdate();
            adapter.Items.Clear();
            foreach (var item in available) adapter.Items.Add(new AdapterChoice(item.Id, item.ToString(), item));
            if (selectedId != null && available.All(x => x.Id != selectedId))
                adapter.Items.Add(new AdapterChoice(selectedId, "前回のインタフェース（見つかりません）", null));
            adapter.SelectedIndex = selectedId == null ? (adapter.Items.Count > 0 ? 0 : -1) :
                adapter.Items.Cast<AdapterChoice>().ToList().FindIndex(x => x.Id == selectedId);
            UpdateAdapterDetail();
        }
        catch (NetworkInformationException ex)
        {
            footer.Text = "インタフェースの取得に失敗: " + ex.Message;
        }
        finally { adapter.EndUpdate(); refreshing = false; }
        if (settings.InterfaceId == null) SelectAdapter();
    }

    private void SelectAdapter()
    {
        if (refreshing) return;
        if (adapter.SelectedItem is not AdapterChoice selected) return;
        settings.InterfaceId = selected.Id;
        sampler.Reset();
        graph.Reset();
        UpdateAdapterDetail();
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            footer.Text = "選択の保存に失敗しました。次回起動時は再選択してください。";
        }
        Poll();
    }

    private void UpdateAdapterDetail()
    {
        connections.SetInterface((adapter.SelectedItem as AdapterChoice)?.Interface);
        udpConnections.SetInterface((adapter.SelectedItem as AdapterChoice)?.Interface);
        if (adapter.SelectedItem is not AdapterChoice choice) { adapterDetail.Text = "利用できるインタフェースがありません。"; return; }
        var item = choice.Interface;
        adapterDetail.Text = item == null ? "再接続を待っています。このインタフェースの選択を保持します。" :
            item.Description + "\n" + (item.Addresses.Count == 0 ? "IP アドレスなし" : string.Join("  /  ", item.Addresses));
        tips.SetToolTip(adapterDetail, adapterDetail.Text);
    }

    private void Poll()
    {
        if (exiting) return;
        if (++tick % 5 == 0) RefreshAdapters();
        var selected = adapter.SelectedItem as AdapterChoice;
        var sample = selected == null ? null : sampler.Sample(TrafficNetwork.ReadCounters(selected.Id), clock.Elapsed);
        latestSample = sample;
        tcpTraffic = tcpTrafficMeter.Read(connections.LiveTraffic);
        if (!tcpOnlyMode && settings.TcpActivityIndicator && sample?.IsAvailable == true)
            latestTcpActivity = tcpIndicatorSampler.Read(sample.HasRate);
        else if (latestTcpActivity.HasValue)
            ResetTcpIndicator();
        RenderTraffic(addGraphSample: true);
    }

    private void RenderTraffic(bool addGraphSample)
    {
        var traffic = tcpOnlyMode && latestSample?.IsAvailable == true ? tcpTraffic : null;
        if (traffic is not null && displayedTcpRevision != traffic.Revision)
        {
            displayedTcpRevision = traffic.Revision;
            graph.Reset();
        }
        var down = tcpOnlyMode ? traffic?.DownloadBytesPerSecond : latestSample?.HasRate == true ? latestSample.DownloadBytesPerSecond : null;
        var up = tcpOnlyMode ? traffic?.UploadBytesPerSecond : latestSample?.HasRate == true ? latestSample.UploadBytesPerSecond : null;
        receiveRate.Text = down.HasValue ? FormatBytes(down.Value) + "/s" : "—";
        sendRate.Text = up.HasValue ? FormatBytes(up.Value) + "/s" : "—";
        receiveTotal.Text = tcpOnlyMode ? "TCP 観測後の合計  " + (traffic is null ? "—" : FormatBytes(traffic.ReceivedBytes))
            : "選択後の合計  " + FormatBytes(latestSample?.TotalReceivedBytes ?? 0);
        sendTotal.Text = tcpOnlyMode ? "TCP 観測後の合計  " + (traffic is null ? "—" : FormatBytes(traffic.SentBytes))
            : "選択後の合計  " + FormatBytes(latestSample?.TotalSentBytes ?? 0);
        if (addGraphSample) graph.Add(down ?? 0, up ?? 0);
        RenderIndicator();
    }

    private async Task SetTcpOnlyModeAsync(bool enabled)
    {
        if (changingTcpMode || shuttingDown || enabled == tcpOnlyMode) return;
        changingTcpMode = true;
        tcpOnlyMode = enabled;
        tcpOnly.Checked = tcpOnlyMenu.Checked = enabled;
        tcpOnly.Enabled = tcpOnlyMenu.Enabled = false;
        unicastOnly.Enabled = unicastOnlyMenu.Enabled = tcpIndicator.Enabled = tcpIndicatorMenu.Enabled = !enabled;
        ResetTcpIndicator();
        displayedTcpRevision = null;
        graph.Reset();
        RenderTraffic(addGraphSample: false);
        try
        {
            if (enabled) await connections.StartTcpTrafficDisplayAsync();
            else await connections.StopTcpTrafficDisplayAsync();
        }
        finally
        {
            changingTcpMode = false;
            if (!IsDisposed && !shuttingDown)
            {
                tcpOnly.Enabled = tcpOnlyMenu.Enabled = true;
                var error = connections.TcpMonitorError;
                if (error is not null) footer.Text = error;
                RenderTraffic(addGraphSample: false);
            }
        }
    }

    private void SetUnicastOnlyIndicator(bool enabled)
    {
        if (settings.UnicastOnlyIndicator == enabled) return;
        settings.UnicastOnlyIndicator = enabled;
        unicastOnly.Checked = enabled;
        unicastOnlyMenu.Checked = enabled;
        // Reuse the last interval: switching display mode neither polls nor resets totals.
        SaveIndicatorSettings();
    }

    private void SetTcpActivityIndicator(bool enabled)
    {
        if (settings.TcpActivityIndicator == enabled) return;
        settings.TcpActivityIndicator = enabled;
        tcpIndicator.Checked = enabled;
        tcpIndicatorMenu.Checked = enabled;
        ResetTcpIndicator();
        SaveIndicatorSettings();
    }

    private void ResetTcpIndicator()
    {
        latestTcpActivity = null;
        tcpIndicatorSampler.Reset();
    }

    private void SaveIndicatorSettings()
    {
        RenderIndicator();
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            footer.Text = "アイコンの設定を保存できませんでした。次回起動時は再設定してください。";
        }
    }

    private void RenderIndicator()
    {
        var sample = latestSample;
        var onlyUnicast = !tcpOnlyMode && settings.UnicastOnlyIndicator;
        var useTcp = !tcpOnlyMode && settings.TcpActivityIndicator;
        var receiving = tcpOnlyMode ? tcpTraffic?.DownloadBytesPerSecond > 0 : onlyUnicast ? sample?.IsUnicastReceiving == true : sample?.IsReceiving == true;
        var sending = tcpOnlyMode ? tcpTraffic?.UploadBytesPerSecond > 0 : onlyUnicast ? sample?.IsUnicastSending == true : sample?.IsSending == true;
        if (useTcp)
        {
            receiving &= latestTcpActivity?.IsReceiving == true;
            sending &= latestTcpActivity?.IsSending == true;
        }
        var state = sample?.IsAvailable != true ? TrafficState.Offline :
            receiving && sending ? TrafficState.Both :
            receiving ? TrafficState.Receiving : sending ? TrafficState.Sending : TrafficState.Idle;
        var status = state switch { TrafficState.Receiving => "受信中", TrafficState.Sending => "送信中", TrafficState.Both => "送受信中", TrafficState.Offline => "未接続", _ => "待機中" };
        if (onlyUnicast && sample?.IsAvailable == true)
        {
            if (sample.ReceivedUnicastDelta is null && sample.SentUnicastDelta is null) status = "判定不可";
            else if (sample.ReceivedUnicastDelta is null || sample.SentUnicastDelta is null) status += "（一部判定不可）";
        }
        if (useTcp && sample?.IsAvailable == true)
        {
            if (latestTcpActivity is null) status = "計測待ち";
            else if (!latestTcpActivity.Value.IsAvailable) status = "TCP統計なし";
        }
        if (tcpOnlyMode && sample?.IsAvailable == true)
        {
            if (tcpTraffic is null) status = changingTcpMode ? "管理者確認中" : connections.IsTcpMonitorRunning ? "計測待ち" : "観測停止中";
            else if (tcpTraffic.Incomplete) status += "（一部未計測）";
        }
        var mode = tcpOnlyMode ? "TCP観測" : useTcp ? (onlyUnicast ? "TCP目安・ユニキャスト" : "TCP目安") : (onlyUnicast ? "ユニキャスト" : "全通信");
        connection.Text = "● " + (tcpOnlyMode || useTcp || onlyUnicast ? mode + " " : "") + status;
        connection.ForeColor = TrafficIcons.ColorFor(state);
        var download = tcpOnlyMode ? tcpTraffic?.DownloadBytesPerSecond : sample?.DownloadBytesPerSecond;
        var upload = tcpOnlyMode ? tcpTraffic?.UploadBytesPerSecond : sample?.UploadBytesPerSecond;
        var trafficIcon = icons.GetIcon(sample?.IsAvailable == true, receiving ? download.GetValueOrDefault() : 0,
            sending ? upload.GetValueOrDefault() : 0);
        // Icons are cached. Avoid repeated shell/window notifications while the state is unchanged.
        if (!ReferenceEquals(tray.Icon, trafficIcon)) tray.Icon = trafficIcon;
        if (!ReferenceEquals(Icon, trafficIcon)) Icon = trafficIcon;
        var name = (adapter.SelectedItem as AdapterChoice)?.Interface?.Name ?? "未選択";
        if (name.Length > 10) name = name[..9] + "…";
        var scope = tcpOnlyMode ? "TCP" : "全体";
        var tooltip = $"SaruMon · {name}\n棒: {mode} · {status}\n{scope} ↓ {receiveRate.Text} ↑ {sendRate.Text}";
        if (tooltip.Length > 63) tooltip = tooltip[..63];
        if (tray.Text != tooltip) tray.Text = tooltip;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        Hide();
        if (notified) return;
        notified = true;
        tray.ShowBalloonTip(2500, "SaruMon は監視を続けています", "アイコンが隠れている場合は、タスクバーの ∧ を開き、SaruMon を時計の近くへドラッグしてください。", ToolTipIcon.Info);
    }

    private void OpenUpdates()
    {
        using var dialog = new UpdateDialog(RequestExit);
        dialog.ShowDialog(this);
    }

    private void ShowTrayHelp() => MessageBox.Show(this,
        "1. タスクバー右端の ∧（隠れているアイコン）を開きます。\n2. SaruMon の棒グラフのアイコンを、時計の近くの通知領域へドラッグします。\n\nWindows の「タスクバーの設定 → その他のシステム トレイ アイコン」からも表示を選べます。同名の項目が複数ある場合は、動いているアイコンを直接ドラッグしてください。\n\nスタートアップ登録は、ログイン時にアプリを起動する設定です。アイコンを常に表示する設定とは別です。",
        "SaruMon アイコンを常に表示する", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowHelp() => MessageBox.Show(this,
        "1. 監視するインタフェースを選択します。\n2. 通知領域に格納すると、白地の棒グラフで通信量を確認できます。\n\n左の緑は受信で、上から下へ伸びます。右の赤は送信で、下から上へ伸びます。\n1秒ごとに、通信量に応じて1ピクセル刻みで伸びます。1 KiB/s以下の通信は1ピクセル、16 MiB/s以上は最大の高さです。\n通信がない方向の棒は消え、未接続はオレンジの × です。\n「ユニキャスト通信だけで棒を表示」をオンにすると、ユニキャスト通信があった方向だけ棒を表示します。高さはその方向の全通信量です。\n管理者権限は不要で、TCP・UDPとLAN内の個別通信を含みます。設定は次回起動時も引き継ぎます。\n「TCP通信の目安で棒を表示」では、選択した回線とPC全体のTCPで同じ区間に通信が増えた方向を表示します。高さはその方向の全通信量です。\nPC内や別回線のTCPが選択回線のUDPと重なると点灯する場合があります。管理者権限は不要です。\n「TCP通信だけを表示（管理者権限）」をオンにすると、選択したインタフェースのローカルIPに対応するTCPの棒・速度・合計・グラフを表示します。\nPC内で完結する通信は除きます。TCP観測は起動時にオフです。\n通常時と目安モードの速度・合計・グラフはインタフェース全体の値です。\n速度の単位はバイト/秒です（1 KiB = 1,024 B）。\n合計は現在のインタフェースを選択してからの通信量です。\n\n「接続先（TCP）」タブで IP・宛先ポート・接続状態を確認できます。\n「相手ごとの通信量」で監視を開始すると、相手 IP ごとの上り・下り合計を記録します。\n「ログを保存」で、相手 IP・名前・上り合計・下り合計・接続回数・開始時刻・最終時刻を CSV に保存できます。\n「リセット」で相手別ログを消去します。監視中は新しい集計を続けます。\n接続回数は監視中に観測した分です。監視前からの接続は回数に含めません。\n開始・最終時刻は、その相手を最初・最後に観測した時刻です。\n「逆引き」をオンにすると、DNS で取得したホスト名も表示します。\nホスト名は閲覧サイト名とは限りません。\n\n「UDP 送信先」で「開始」を押すと、管理者承認後に UDP 送信を記録します。\n「保存」で、開始時刻・最終時刻・送信イベント数・累積送信量を CSV に保存できます。\n「リセット」で UDP 送信先ログを消去します。監視中はリセット後の送信から集計を続けます。\nUDP の開始・最終時刻は、最初・最後に送信を観測した時刻です。\n監視開始前の通信は含みません。停止後も保存でき、再開・インタフェース変更で記録をクリアします。\n\nアイコンが隠れている場合は、タスクバー右端の ∧ を開き、SaruMon を時計の近くへドラッグしてください。\nアイコンのクリック、または SaruMon の再起動で画面が開きます。\n終了するには、メニューの「終了」を選びます。\n\n更新は「ヘルプ → 更新を確認」から実行できます。",
        "SaruMon の使い方", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public async void RequestExit()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        timer.Stop();
        Enabled = false;
        try
        {
            await connections.ShutdownAsync();
            await udpConnections.ShutdownAsync();
        }
        catch (Exception ex)
        {
            shuttingDown = false;
            Enabled = true;
            timer.Start();
            MessageBox.Show(this, "通信監視の終了を確認できませんでした。\n" + ex.Message,
                "SaruMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop(); timer.Dispose();
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            tips.Dispose();
            icons.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static Label RateLabel() => new() { AutoSize = true, Font = new Font("Yu Gothic UI", 25, FontStyle.Bold), Text = "—", Margin = Padding.Empty };
    private static Label DetailLabel() => new() { AutoSize = true, ForeColor = Color.FromArgb(104, 116, 134), Font = new Font("Yu Gothic UI", 9), Margin = Padding.Empty };
    private static Control CreateRateCard(string title, Color color, Label rate, Label total, Padding margin)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(15, 7, 15, 7), RowCount = 3, Margin = margin };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = color, Margin = Padding.Empty }, 0, 0);
        rate.ForeColor = color;
        panel.Controls.Add(rate, 0, 1);
        panel.Controls.Add(total, 0, 2);
        return panel;
    }

    private sealed record AdapterChoice(string Id, string Display, TrafficInterface? Interface)
    {
        public override string ToString() => Display;
    }
}
