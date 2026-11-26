using System.Diagnostics;

namespace SaruMon.Updates;

public sealed class UpdateDialog : Form
{
    private readonly UpdateService _service = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action _requestExit;
    private readonly Label _status;
    private readonly TextBox _notes;
    private readonly Button _check;
    private readonly Button _install;
    private readonly Button _close;
    private readonly ProgressBar _progress;
    private GitHubRelease? _release;
    private bool _busy;
    private bool _installStarted;

    public UpdateDialog(Action requestExit)
    {
        _requestExit = requestExit;
        Text = "SaruMon の更新";
        Font = new Font("Yu Gothic UI", 10F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(540, 430);
        ClientSize = new Size(600, 460);
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = $"現在のバージョン: {UpdateService.CurrentVersion}", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        _status = new Label { AutoSize = true, MaximumSize = new Size(540, 0), Text = "GitHub の公開リリースを確認します。", Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(_status, 0, 1);
        _notes = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window, Text = "更新情報を確認中…"
        };
        layout.Controls.Add(_notes, 0, 2);
        _progress = new ProgressBar { Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 6, 0, 2) };
        layout.Controls.Add(_progress, 0, 3);
        var releaseLink = new LinkLabel { Text = "GitHub Releases を開く", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
        releaseLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(UpdateService.RepositoryUrl + "/releases") { UseShellExecute = true }); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(releaseLink, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _close = new Button { Text = "閉じる", AutoSize = true, MinimumSize = new Size(90, 32) };
        _install = new Button { Text = "更新して再起動", AutoSize = true, MinimumSize = new Size(150, 32), Enabled = false };
        _check = new Button { Text = "再確認", AutoSize = true, MinimumSize = new Size(90, 32) };
        buttons.Controls.AddRange([_close, _install, _check]);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        _close.Click += (_, _) => Close();
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        Shown += async (_, _) => await CheckAsync();
        FormClosing += (_, args) =>
        {
            if (_installStarted) return;
            if (_busy)
            {
                _cancellation.Cancel();
                _status.Text = "処理をキャンセルしています…";
                _close.Enabled = false;
                args.Cancel = true;
            }
        };
        FormClosed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); _service.Dispose(); };
        CancelButton = _close;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _check.Enabled = !busy;
        _install.Enabled = !busy && _release is not null && UpdateService.CanInstallAutomatically(out _);
        _close.Text = busy ? "キャンセル" : "閉じる";
        _progress.Visible = busy;
    }

    private async Task CheckAsync()
    {
        if (_busy || _cancellation.IsCancellationRequested) return;
        _release = null;
        SetBusy(true);
        _progress.Style = ProgressBarStyle.Marquee;
        _status.Text = "最新の公開リリースを確認しています…";
        try
        {
            var release = await _service.CheckAsync(_cancellation.Token);
            if (release is null)
            {
                _status.Text = "公開リリースがまだないか、リポジトリにアクセスできません。";
                _notes.Text = "Windows 用の公開リリースが利用可能になると、ここから更新できます。";
            }
            else if (ReleaseVersion.TryParse(UpdateService.CurrentVersion, out var current) && release.Version.CompareTo(current) <= 0)
            {
                _status.Text = "お使いの SaruMon は最新です。";
                _notes.Text = $"公開中: {release.Version}\r\n\r\n{NormalizeLines(release.Notes)}";
            }
            else
            {
                _release = release;
                _status.Text = $"新しいバージョン {release.Version} を利用できます。";
                _notes.Text = NormalizeLines(release.Notes);
                if (!UpdateService.CanInstallAutomatically(out var reason)) _status.Text += "\r\n" + reason;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_cancellation.IsCancellationRequested) _status.Text = "更新の確認がタイムアウトしました。再確認してください。";
        }
        catch (Exception exception) { ShowFailure("更新を確認できませんでした。", exception); }
        finally
        {
            SetBusy(false);
            if (_cancellation.IsCancellationRequested) Close();
        }
    }

    private async Task InstallAsync()
    {
        if (_busy || _release is null) return;
        if (MessageBox.Show(this,
            $"SaruMon {_release.Version} をダウンロードして更新します。\r\n更新時にアプリを終了し、完了後に再起動します。設定は引き継がれます。",
            "更新の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        SetBusy(true);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _status.Text = "更新ファイルをダウンロードしています…";
        PreparedUpdate? prepared = null;
        try
        {
            prepared = await _service.DownloadAsync(_release, new Progress<int>(value =>
            {
                if (IsDisposed) return;
                _progress.Value = value;
                _status.Text = value < 100 ? $"ダウンロード中: {value}%" : "ダウンロード完了。更新ファイルを検証しています…";
            }), _cancellation.Token);
            _status.Text = "検証が完了しました。更新して再起動します…";
            await UpdateService.StartInstallerAsync(prepared, _cancellation.Token);
            _installStarted = true;
            Close();
            _requestExit();
        }
        catch (OperationCanceledException)
        {
            if (!_cancellation.IsCancellationRequested) _status.Text = "更新のダウンロードがタイムアウトしました。再試行してください。";
        }
        catch (Exception exception) { ShowFailure("更新できませんでした。現在のアプリはそのまま利用できます。", exception); }
        finally
        {
            if (!_installStarted)
            {
                if (prepared is not null) UpdateService.TryDeletePreparedDirectory(prepared.Directory);
                SetBusy(false);
                if (_cancellation.IsCancellationRequested) Close();
            }
        }
    }

    private void ShowFailure(string message, Exception exception)
    {
        _status.Text = message;
        _notes.Text = exception.Message;
    }

    private static string NormalizeLines(string value) => value.ReplaceLineEndings("\r\n");
}
