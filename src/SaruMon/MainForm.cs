using System.Diagnostics;
using System.Net.NetworkInformation;
using SaruMon.Core;
using SaruMon.Monitoring;
using SaruMon.Updates;

namespace SaruMon;

internal sealed class MainForm : Form
{
    private static readonly string[] BitRateUnits = ["bps", "kbps", "Mbps", "Gbps", "Tbps"];
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB"];
    private readonly AppSettings settings = AppSettings.Load();
    private readonly TrafficSampler sampler = new();
    private readonly TcpIndicatorSampler tcpIndicatorSampler = new();
    private readonly TcpTrafficMeter tcpTrafficMeter = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TrafficIcons icons = new(SystemInformation.SmallIconSize.Width);
    private readonly CpuUsageIcons cpuIcons = new(SystemInformation.SmallIconSize.Width);
    private readonly CpuUsageMeter cpuMeter = new();
    private readonly NotifyIcon tray;
    private readonly NotifyIcon cpuTray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 12, Dock = DockStyle.Fill, AccessibleName = "監視するインタフェース" };
    private readonly Label adapterDetail = new() { Dock = DockStyle.Fill, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(104, 116, 134) };
    private readonly Label footer = DetailLabel();
    private readonly TrafficOverview overview = new();
    // Reuse the existing TCP monitor lifecycle without opening its log view.
    private readonly TcpPeerView tcpMonitor = new() { Visible = false };
    private readonly InvestigationView investigation = new();
    private readonly ToolTip tips = new();
    private readonly CheckBox unicastOnly = new() { Text = "ユニキャスト通信だけで棒を表示", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ToolStripMenuItem unicastOnlyMenu = new("ユニキャスト通信だけで棒を表示") { CheckOnClick = true };
    private readonly CheckBox tcpIndicator = new() { Text = "TCP通信の目安で棒を表示", AutoSize = true };
    private readonly ToolStripMenuItem tcpIndicatorMenu = new("TCP通信の目安で棒を表示") { CheckOnClick = true };
    private readonly ToolStripMenuItem tcpOnlyMenu = new("通知領域をTCP通信だけにする（管理者権限）");
    private readonly bool startHidden;
    private TrafficSample? latestSample;
    private TcpActivity? latestTcpActivity;
    private TcpTrafficDisplay? tcpTraffic;
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

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 14), ColumnCount = 1, RowCount = 5 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));

        var interfaceHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        interfaceHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        interfaceHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        interfaceHeading.Controls.Add(new Label { Text = "監視するインタフェース", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 12, 0) }, 0, 0);
        interfaceHeading.Controls.Add(adapterDetail, 1, 0);
        body.Controls.Add(interfaceHeading, 0, 0);
        var choose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        choose.Controls.Add(adapter, 0, 0);
        var refresh = new Button { Text = "再読み込み", Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 3) };
        refresh.Click += (_, _) => RefreshAdapters();
        choose.Controls.Add(refresh, 1, 0);
        body.Controls.Add(choose, 0, 1);

        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var trafficPage = new TabPage("通信量") { BackColor = DashboardStyle.Canvas };
        var investigationPage = new TabPage("調査") { BackColor = DashboardStyle.Canvas };
        trafficPage.Controls.Add(overview);
        investigationPage.Controls.Add(investigation);
        tabs.TabPages.AddRange([trafficPage, investigationPage]);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            var investigating = tabs.SelectedTab == investigationPage;
            body.SuspendLayout();
            try
            {
                body.RowStyles[3].Height = investigating ? 0 : 70;
                body.RowStyles[4].Height = investigating ? 0 : 27;
                foreach (var row in new[] { 3, 4 })
                    if (body.GetControlFromPosition(0, row) is { } control) control.Visible = !investigating;
            }
            finally { body.ResumeLayout(); }
        };
        body.Controls.Add(tabs, 0, 2);

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
        mainOptions.Controls.Add(startup);
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
        body.Controls.Add(options, 0, 3);
        footer.Text = "1秒ごとに更新 · × を押しても監視を続けます";
        footer.Dock = DockStyle.Fill;
        footer.TextAlign = ContentAlignment.MiddleLeft;
        body.Controls.Add(footer, 0, 4);
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
        cpuTray = new NotifyIcon
        {
            Icon = cpuIcons.GetIcon(null),
            Text = "SaruMon · CPU 使用率を計測中",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        cpuTray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        unicastOnly.CheckedChanged += (_, _) => SetUnicastOnlyIndicator(unicastOnly.Checked);
        unicastOnlyMenu.CheckedChanged += (_, _) => SetUnicastOnlyIndicator(unicastOnlyMenu.Checked);
        tcpIndicator.CheckedChanged += (_, _) => SetTcpActivityIndicator(tcpIndicator.Checked);
        tcpIndicatorMenu.CheckedChanged += (_, _) => SetTcpActivityIndicator(tcpIndicatorMenu.Checked);
        tcpOnlyMenu.Click += async (_, _) => await SetTcpOnlyModeAsync(!tcpOnlyMode);
        adapter.SelectedIndexChanged += (_, _) => SelectAdapter();
        RefreshAdapters();
        timer.Tick += (_, _) =>
        {
            Poll();
            UpdateCpuIndicator();
        };
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
        overview.ResetGraph();
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
        var choice = adapter.SelectedItem as AdapterChoice;
        var item = choice?.Interface;
        overview.SetInterface(item);
        tcpMonitor.SetInterface(item);
        investigation.SetInterface(item);
        adapterDetail.Text = item is not null
            ? item.Description + "  ·  " + (item.Addresses.Count == 0 ? "IP アドレスなし" : string.Join("  /  ", item.Addresses))
            : choice is null ? "利用できるインタフェースがありません。" : "再接続を待っています。";
        tips.SetToolTip(adapterDetail, adapterDetail.Text);
    }

    private void Poll()
    {
        if (exiting) return;
        if (++tick % 5 == 0) RefreshAdapters();
        var selected = adapter.SelectedItem as AdapterChoice;
        var sample = selected == null ? null : sampler.Sample(TrafficNetwork.ReadCounters(selected.Id), clock.Elapsed);
        latestSample = sample;
        tcpTraffic = tcpTrafficMeter.Read(tcpMonitor.LiveTraffic);
        if (!tcpOnlyMode && settings.TcpActivityIndicator && sample?.IsAvailable == true)
            latestTcpActivity = tcpIndicatorSampler.Read(sample.HasRate);
        else if (latestTcpActivity.HasValue)
            ResetTcpIndicator();
        overview.SetSample(latestSample, addGraphSample: true);
        RenderIndicator();
    }

    private void UpdateCpuIndicator()
    {
        var percentage = cpuMeter.ReadPercentage();
        var icon = cpuIcons.GetIcon(percentage);
        if (!ReferenceEquals(cpuTray.Icon, icon)) cpuTray.Icon = icon;
        var tooltip = percentage is int value ? $"SaruMon · CPU 使用率 {value}%" : "SaruMon · CPU 使用率を取得できません";
        if (cpuTray.Text != tooltip) cpuTray.Text = tooltip;
    }

    private async Task SetTcpOnlyModeAsync(bool enabled)
    {
        if (changingTcpMode || shuttingDown || enabled == tcpOnlyMode) return;
        changingTcpMode = true;
        tcpOnlyMode = enabled;
        tcpOnlyMenu.Checked = enabled;
        tcpOnlyMenu.Enabled = false;
        unicastOnly.Enabled = unicastOnlyMenu.Enabled = tcpIndicator.Enabled = tcpIndicatorMenu.Enabled = !enabled;
        ResetTcpIndicator();
        tcpTraffic = tcpTrafficMeter.Read(null);
        RenderIndicator();
        try
        {
            if (enabled) await tcpMonitor.StartForTrafficDisplayAsync();
            else await tcpMonitor.StopForTrafficDisplayAsync();
        }
        finally
        {
            changingTcpMode = false;
            if (!IsDisposed && !shuttingDown)
            {
                tcpOnlyMenu.Enabled = true;
                var error = tcpMonitor.MonitorError;
                if (error is not null) footer.Text = error;
                RenderIndicator();
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
            if (tcpTraffic is null) status = changingTcpMode ? "管理者確認中" : tcpMonitor.IsMonitorRunning ? "計測待ち" : "観測停止中";
            else if (tcpTraffic.Incomplete) status += "（一部未計測）";
        }
        var mode = tcpOnlyMode ? "TCP観測" : useTcp ? (onlyUnicast ? "TCP目安・ユニキャスト" : "TCP目安") : (onlyUnicast ? "ユニキャスト" : "全通信");
        var download = tcpOnlyMode ? tcpTraffic?.DownloadBytesPerSecond : sample?.HasRate == true ? sample.DownloadBytesPerSecond : null;
        var upload = tcpOnlyMode ? tcpTraffic?.UploadBytesPerSecond : sample?.HasRate == true ? sample.UploadBytesPerSecond : null;
        var trafficIcon = icons.GetIcon(sample?.IsAvailable == true, receiving ? download.GetValueOrDefault() : 0,
            sending ? upload.GetValueOrDefault() : 0);
        // Icons are cached. Avoid repeated shell/window notifications while the state is unchanged.
        if (!ReferenceEquals(tray.Icon, trafficIcon)) tray.Icon = trafficIcon;
        if (!ReferenceEquals(Icon, trafficIcon)) Icon = trafficIcon;
        var name = (adapter.SelectedItem as AdapterChoice)?.Interface?.Name ?? "未選択";
        const int tooltipLimit = 63;
        const string namedTitle = "SaruMon · ";
        var scope = tcpOnlyMode ? "TCP" : "全体";
        var rates = $"{scope} ↓ {TrafficOverview.Rate(download)} ↑ {TrafficOverview.Rate(upload)}";
        var details = $"{rates}\n棒: {mode} · {status}";
        var nameBudget = Math.Min(10, tooltipLimit - namedTitle.Length - details.Length - 1);
        var title = nameBudget < 1 ? "SaruMon" : namedTitle + (name.Length <= nameBudget ? name : name[..(nameBudget - 1)] + "…");
        var tooltip = $"{title}\n{details}";
        if (tooltip.Length > tooltipLimit) tooltip = $"SaruMon\n{rates}\n{status}";
        if (tooltip.Length > tooltipLimit) tooltip = tooltip[..tooltipLimit];
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
        "1. タスクバー右端の ∧（隠れているアイコン）を開きます。\n2. SaruMon の通信量アイコンと CPU アイコンを、時計の近くの通知領域へドラッグします。\n\nCPU アイコンの青い棒は PC 全体の使用率です。マウスを重ねるとパーセントを確認できます。\n\nWindows の「タスクバーの設定 → その他のシステム トレイ アイコン」からも表示を選べます。同名の項目が複数ある場合は、現在表示されているアイコンを直接ドラッグしてください。\n\nスタートアップ登録は、ログイン時にアプリを起動する設定です。アイコンを常に表示する設定とは別です。",
        "SaruMon アイコンを常に表示する", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowHelp() => MessageBox.Show(this,
        "1. 監視するインタフェースを選択します。\n2. 通知領域に格納すると、白地の棒グラフで通信量を確認できます。\n\n左の緑は受信で、上から下へ伸びます。右の赤は送信で、下から上へ伸びます。\n1秒ごとに、通信量に応じて1ピクセル刻みで伸びます。約8.2 kbps以下の通信は1ピクセル、約134.2 Mbps以上は最大の高さです。\n通信がない方向の棒は消え、未接続はオレンジの × です。\n「ユニキャスト通信だけで棒を表示」をオンにすると、ユニキャスト通信があった方向だけ棒を表示します。高さはその方向の全通信量です。\n管理者権限は不要で、TCP・UDPとLAN内の個別通信を含みます。設定は次回起動時も引き継ぎます。\n「TCP通信の目安で棒を表示」では、選択した回線とPC全体のTCPで同じ区間に通信が増えた方向を表示します。高さはその方向の全通信量です。\nPC内や別回線のTCPが選択回線のUDPと重なると点灯する場合があります。管理者権限は不要です。\n通知領域メニューの「通知領域をTCP通信だけにする（管理者権限）」をオンにすると、選択インタフェースのTCPの棒と速度を通知領域に表示します。\nPC内で完結する通信は除きます。TCP観測は起動時にオフです。\n「通信量」タブには、回線全体の速度・合計・直近5分のグラフと、現在のTCP接続を表示します。\n通常権限で「アプリ別」にTCP接続とUDPのローカルIP・ポート・PIDを表示します。UDPの宛先IP・ポートは「調査」で確認できます。\n速度はビット毎秒（bps、kbps、Mbpsなど）で表示します。合計の単位はバイトです。\n合計は現在のインタフェースを選択してからの通信量です。\n\n「調査」タブの「記録開始（管理者）」でTCP・UDP通信を記録します。\n「アプリ別」または「宛先別」でまとめ、同じIPへの通信の内訳を確認できます。\n上りはTCPとUDPの合計、下りはTCPの観測量です。\n停止後も「調査レポート…」でMarkdown、「全件CSV…」で集計を保存できます。\n\nアイコンが隠れている場合は、タスクバー右端の ∧ を開き、SaruMon を時計の近くへドラッグしてください。\nアイコンのクリック、または SaruMon の再起動で画面が開きます。\n終了するには、メニューの「終了」を選びます。\n\n更新は「ヘルプ → 更新を確認」から実行できます。",
        "SaruMon の使い方", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public async void RequestExit()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        timer.Stop();
        Enabled = false;
        try
        {
            await tcpMonitor.ShutdownAsync();
            await investigation.ShutdownAsync();
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
            cpuTray.Visible = false;
            var trayMenu = tray.ContextMenuStrip;
            cpuTray.Dispose();
            tray.Dispose();
            trayMenu?.Dispose();
            cpuMeter.Dispose();
            cpuIcons.Dispose();
            tcpMonitor.Dispose();
            tips.Dispose();
            icons.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static string FormatBitRate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        var unit = 0;
        while (bits >= 1000 && unit < BitRateUnits.Length - 1)
        {
            bits /= 1000;
            unit++;
        }
        return bits.ToString(unit == 0 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + BitRateUnits[unit];
    }

    internal static string FormatBytes(double bytes, bool compact = false)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1) { bytes /= 1024; unit++; }
        var format = unit == 0 || compact && bytes >= 10 ? "0" : compact ? "0.#" : "0.0";
        return bytes.ToString(format, System.Globalization.CultureInfo.CurrentCulture) + (compact ? "" : " ") + ByteUnits[unit];
    }

    private static Label DetailLabel() => new() { AutoSize = true, ForeColor = Color.FromArgb(104, 116, 134), Font = new Font("Yu Gothic UI", 9), Margin = Padding.Empty };

    private sealed record AdapterChoice(string Id, string Display, TrafficInterface? Interface)
    {
        public override string ToString() => Display;
    }
}
