using System.Diagnostics;
using System.Net.NetworkInformation;
using SaruMon.Core;
using SaruMon.Updates;

namespace SaruMon;

internal sealed class MainForm : Form
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly TrafficSampler sampler = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TrafficIcons icons = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
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
    private readonly bool startHidden;
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
            $"SaruMon {UpdateService.CurrentVersion}\n\nインタフェースごとの通信量、TCP 接続先、UDP 送信先の監視と CSV 保存ができます。\nパケットの内容は取得しません。\n\n実行場所:\n{Environment.ProcessPath}", "SaruMon について", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, help]);
        MainMenuStrip = menu;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 14), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 64, 28, 40, 48 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
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
        foreach (var (state, text) in new[] { (TrafficState.Receiving, "↓ 受信"), (TrafficState.Sending, "↑ 送信"), (TrafficState.Idle, "● 通信なし"), (TrafficState.Offline, "× 未接続") })
            legend.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = TrafficIcons.ColorFor(state), Margin = new Padding(0, 0, 18, 0) });
        tips.SetToolTip(legend, "下りは緑、上りは赤。直近の通信速度が大きいほど、それぞれの矢印が強く光ります。");
        trafficLayout.Controls.Add(legend, 0, 2);
        trafficPage.Controls.Add(trafficLayout);
        peersPage.Controls.Add(connections);
        detailPage.Controls.Add(udpConnections);
        tabs.TabPages.AddRange([trafficPage, peersPage, detailPage]);
        body.Controls.Add(tabs, 0, 4);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
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
        options.Controls.Add(startup, 0, 0);
        options.Controls.Add(hide, 1, 0);
        body.Controls.Add(options, 0, 5);
        footer.Text = "0.5秒ごとに更新 · × を押しても監視を続けます";
        footer.Dock = DockStyle.Fill;
        footer.TextAlign = ContentAlignment.MiddleLeft;
        body.Controls.Add(footer, 0, 6);
        Controls.Add(body);
        Controls.Add(menu);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("SaruMon を開く", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("アイコンを常に表示するには", null, (_, _) => { ShowFromTray(); ShowTrayHelp(); });
        trayMenu.Items.Add("更新を確認…", null, (_, _) => { ShowFromTray(); OpenUpdates(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("終了", null, (_, _) => RequestExit());
        tray = new NotifyIcon { Icon = icons.Idle, Text = "SaruMon", ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
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
        if (++tick % 10 == 0) RefreshAdapters();
        var selected = adapter.SelectedItem as AdapterChoice;
        var sample = selected == null ? null : sampler.Sample(TrafficNetwork.ReadCounters(selected.Id), clock.Elapsed);
        var state = sample?.IsAvailable != true ? TrafficState.Offline :
            sample.IsReceiving && sample.IsSending ? TrafficState.Both :
            sample.IsReceiving ? TrafficState.Receiving : sample.IsSending ? TrafficState.Sending : TrafficState.Idle;
        var status = state switch { TrafficState.Receiving => "受信中", TrafficState.Sending => "送信中", TrafficState.Both => "送受信中", TrafficState.Offline => "未接続", _ => "待機中" };
        connection.Text = "● " + status;
        connection.ForeColor = TrafficIcons.ColorFor(state);
        receiveRate.Text = sample?.HasRate == true ? FormatBytes(sample.DownloadBytesPerSecond) + "/s" : "—";
        sendRate.Text = sample?.HasRate == true ? FormatBytes(sample.UploadBytesPerSecond) + "/s" : "—";
        receiveTotal.Text = "選択後の合計  " + FormatBytes(sample?.TotalReceivedBytes ?? 0);
        sendTotal.Text = "選択後の合計  " + FormatBytes(sample?.TotalSentBytes ?? 0);
        graph.Add(sample?.DownloadBytesPerSecond ?? 0, sample?.UploadBytesPerSecond ?? 0);
        var trafficIcon = icons.GetIcon(sample?.IsAvailable == true,
            sample?.HasRate == true ? sample.DownloadBytesPerSecond : 0,
            sample?.HasRate == true ? sample.UploadBytesPerSecond : 0);
        tray.Icon = trafficIcon;
        Icon = trafficIcon;
        var name = selected?.Interface?.Name ?? "未選択";
        if (name.Length > 20) name = name[..19] + "…";
        var tooltip = $"SaruMon · {name}\n↓ {receiveRate.Text}  ↑ {sendRate.Text}\n{status}";
        tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
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
        "1. タスクバー右端の ∧（隠れているアイコン）を開きます。\n2. SaruMon の上下矢印アイコンを、時計の近くの通知領域へドラッグします。\n\nWindows の「タスクバーの設定 → その他のシステム トレイ アイコン」からも表示を選べます。同名の項目が複数ある場合は、動いているアイコンを直接ドラッグしてください。\n\nスタートアップ登録は、ログイン時にアプリを起動する設定です。アイコンを常に表示する設定とは別です。",
        "SaruMon アイコンを常に表示する", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowHelp() => MessageBox.Show(this,
        "1. 監視するインタフェースを選択します。\n2. 通知領域に格納すると、矢印の発光で通信を確認できます。\n\n下り（受信）: 緑の下矢印 / 上り（送信）: 赤の上矢印\n直近の通信速度が大きいほど、各矢印が強く光ります。\n通信がない方向は暗い灰色、未接続はオレンジの × です。\n速度の単位はバイト/秒です（1 KiB = 1,024 B）。\n合計は現在のインタフェースを選択してからの通信量です。\n\n「接続先（TCP）」タブで IP・宛先ポート・接続状態を確認できます。\n「逆引き」をオンにすると、DNS で取得したホスト名も表示します。\nホスト名は閲覧サイト名とは限りません。\n\n「UDP 送信先」で「開始」を押すと、管理者承認後に UDP 送信を記録します。\n「ログを保存」で、初回検出時刻・送信イベント数・累積送信量を CSV に保存できます。\n監視開始前の通信は含みません。停止後も保存でき、再開・インタフェース変更で記録をクリアします。\n\nアイコンが隠れている場合は、タスクバー右端の ∧ を開き、SaruMon を時計の近くへドラッグしてください。\nアイコンのクリック、または SaruMon の再起動で画面が開きます。\n終了するには、メニューの「終了」を選びます。\n\n更新は「ヘルプ → 更新を確認」から実行できます。",
        "SaruMon の使い方", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public async void RequestExit()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        timer.Stop();
        Enabled = false;
        try { await udpConnections.ShutdownAsync(); }
        catch (Exception ex)
        {
            shuttingDown = false;
            Enabled = true;
            timer.Start();
            MessageBox.Show(this, "UDP 監視の終了を確認できませんでした。\n" + ex.Message,
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
