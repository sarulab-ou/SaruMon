using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SaruMon.Updates;

public sealed record GitHubRelease(ReleaseVersion Version, string Notes, Uri ArchiveUrl, Uri ChecksumUrl);
public sealed record PreparedUpdate(string Directory, string Executable, string Sha256);

public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/sarulab-ou/SaruMon";
    private const string ApiUrl = "https://api.github.com/repos/sarulab-ou/SaruMon/releases/latest";
    private readonly HttpClient _client;
    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
        .Split('+')[0];

    public UpdateService()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"SaruMon/{CurrentVersion}");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(new Uri(ApiUrl), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        var data = await ReadLimitedAsync(response, 2 * 1024 * 1024, null, timeout.Token);
        return ParseRelease(Encoding.UTF8.GetString(data));
    }

    public static GitHubRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()
            || !ReleaseVersion.TryParse(root.GetProperty("tag_name").GetString(), out var version)
            || version!.IsPrerelease)
            throw new InvalidDataException("安定版リリースのバージョン情報が正しくありません。");
        Uri? archive = null;
        Uri? checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != UpdateArchive.AssetName && name != UpdateArchive.ChecksumName) continue;
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri)
                || !IsReleaseAssetUrl(uri, name))
                throw new InvalidDataException("更新ファイルの配布先 URL が正しくありません。");
            if (name == UpdateArchive.AssetName)
            {
                if (archive is not null) throw new InvalidDataException("更新ファイルが重複しています。");
                archive = uri;
            }
            else
            {
                if (checksum is not null) throw new InvalidDataException("チェックサムが重複しています。");
                checksum = uri;
            }
        }
        if (archive is null || checksum is null)
            throw new InvalidDataException("このリリースに Windows 用 ZIP または SHA-256 ファイルがありません。");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new GitHubRelease(version!, notes, archive, checksum);
    }

    public static bool IsReleaseAssetUrl(Uri uri, string expectedName) =>
        IsHttps(uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/sarulab-ou/SaruMon/releases/download/", StringComparison.OrdinalIgnoreCase)
        && Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) == expectedName
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsAllowedDownloadUri(Uri uri) => IsHttps(uri) &&
        (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (!IsAllowedDownloadUri(uri)) throw new InvalidDataException("更新ファイルの転送先が許可されていません。");
            var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("ダウンロードの転送先がありません。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new HttpRequestException("ダウンロードの転送回数が多すぎます。");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub のアクセス制限により確認できません。時間をおいて再試行してください。");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maximum,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await CopyLimitedAsync(response, output, maximum, progress, cancellationToken);
        return output.ToArray();
    }

    private static async Task CopyLimitedAsync(HttpResponseMessage response, Stream output, long maximum,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var length = response.Content.Headers.ContentLength;
        if (length > maximum) throw new InvalidDataException("更新ファイルのサイズが大きすぎます。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += read;
            if (copied > maximum) throw new InvalidDataException("更新ファイルのサイズが大きすぎます。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (length > 0) progress?.Report((int)Math.Min(100, copied * 100 / length.Value));
        }
        if (length is not null && copied != length) throw new InvalidDataException("更新ファイルのダウンロードが不完全です。");
        progress?.Report(100);
    }

    public async Task<PreparedUpdate> DownloadAsync(GitHubRelease release, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        cancellationToken = timeout.Token;
        // Revalidate even when a caller constructed the release itself.
        if (!IsReleaseAssetUrl(release.ArchiveUrl, UpdateArchive.AssetName)
            || !IsReleaseAssetUrl(release.ChecksumUrl, UpdateArchive.ChecksumName))
            throw new InvalidDataException("更新元 URL が正しくありません。");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SaruMon", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var checksumResponse = await GetAsync(release.ChecksumUrl, cancellationToken);
            EnsureSuccess(checksumResponse);
            var expected = UpdateArchive.ParseChecksum(Encoding.UTF8.GetString(
                await ReadLimitedAsync(checksumResponse, 4096, null, cancellationToken)));
            var zip = Path.Combine(directory, UpdateArchive.AssetName);
            using (var response = await GetAsync(release.ArchiveUrl, cancellationToken))
            {
                EnsureSuccess(response);
                await using var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyLimitedAsync(response, output, UpdateArchive.MaximumDownloadBytes, progress, cancellationToken);
            }
            await UpdateArchive.VerifyChecksumAsync(zip, expected, cancellationToken);
            var executable = await UpdateArchive.ExtractExecutableAsync(zip, directory, cancellationToken);
            var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            if (!ReleaseVersion.TryParse(productVersion, out var executableVersion)
                || executableVersion!.CompareTo(release.Version) != 0)
                throw new InvalidDataException("更新ファイルの製品バージョンがリリース情報と一致しません。");
            await using var stream = File.OpenRead(executable);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            File.Delete(zip);
            return new PreparedUpdate(directory, executable, sha256);
        }
        catch
        {
            TryDeletePreparedDirectory(directory);
            throw;
        }
    }

    public static bool CanInstallAutomatically(out string reason)
    {
        var executable = Environment.ProcessPath;
#pragma warning disable IL3000 // Empty assembly Location is the documented single-file detection used here.
        var isSingleFile = string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
        if (!isSingleFile || executable is null || !Path.GetFileName(executable).Equals("SaruMon.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "自動更新は配布用の SaruMon.exe から起動したときに利用できます。";
            return false;
        }
        reason = "";
        return true;
    }

    public static async Task StartInstallerAsync(PreparedUpdate update, CancellationToken cancellationToken)
    {
        if (!CanInstallAutomatically(out var reason)) throw new InvalidOperationException(reason);
        var target = Path.GetFullPath(Environment.ProcessPath!);
        var targetDirectory = Path.GetDirectoryName(target)!;
        var probe = Path.Combine(targetDirectory, ".SaruMon-write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); }
        catch (UnauthorizedAccessException exception)
        { throw new IOException("このフォルダーには更新を保存できません。SaruMon を書き込み可能なフォルダーへ移してください。", exception); }
        finally { if (File.Exists(probe)) File.Delete(probe); }

        var ready = Path.Combine(update.Directory, "ready");
        var config = Path.Combine(update.Directory, "install.json");
        var script = Path.Combine(update.Directory, "install.ps1");
        using var process = Process.GetCurrentProcess();
        await using var currentFile = File.OpenRead(target);
        var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(currentFile, cancellationToken));
        await currentFile.DisposeAsync();
        var configuration = new
        {
            Target = target, Source = update.Executable, SourceHash = update.Sha256,
            CurrentHash = currentHash, ParentId = process.Id,
            ParentStartTicks = process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Ready = ready, Log = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(update.Directory)!)!, "update.log")
        };
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(configuration), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(script, InstallerScript, new UTF8Encoding(true), cancellationToken);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = update.Directory
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-ConfigPath", config })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("更新処理を開始できませんでした。");
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(10))
                    throw new IOException("更新処理の開始を確認できませんでした。アプリは終了せず、そのまま利用できます。");
                await Task.Delay(100, cancellationToken);
            }
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }

    public static void TryDeletePreparedDirectory(string directory)
    {
        // Call only for the GUID directory allocated by this class, never an installation folder.
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaruMon", "Updates")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _client.Dispose();

    // Static application-owned script: values are passed in JSON, never interpolated into executable PowerShell.
    private const string InstallerScript = """
param([Parameter(Mandatory=$true)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$target = [IO.Path]::GetFullPath([string]$config.Target)
$folder = [IO.Path]::GetDirectoryName($target)
$suffix = [Guid]::NewGuid().ToString('N')
$replacement = [IO.Path]::Combine($folder, '.SaruMon-update-' + $suffix + '.exe')
$backup = [IO.Path]::Combine($folder, '.SaruMon-backup-' + $suffix + '.exe')
$replaced = $false
function Get-Sha256([string]$Path) {
    # Do not depend on Get-FileHash module discovery in an inherited PowerShell environment.
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
try {
    $parent = Get-Process -Id ([int]$config.ParentId) -ErrorAction SilentlyContinue
    if ($null -eq $parent -or $parent.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$config.ParentStartTicks) {
        throw 'The original application process was not found.'
    }
    [IO.File]::WriteAllText([string]$config.Ready, 'ready')
    if (-not $parent.WaitForExit(45000)) { throw 'SaruMon did not exit within 45 seconds. No files were changed.' }
    if ((Get-Sha256 $target) -ne [string]$config.CurrentHash) {
        throw 'The installed executable changed after update preparation. No files were changed.'
    }
    [IO.File]::Copy([string]$config.Source, $replacement, $false)
    if ((Get-Sha256 $replacement) -ne [string]$config.SourceHash) {
        throw 'The prepared executable checksum does not match.'
    }
    # Replacement and backup are on the same volume; retry briefly for antivirus file locks.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { [IO.File]::Replace($replacement, $target, $backup); $replaced = $true; break }
        catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 250 }
    }
    Start-Process -FilePath $target -WorkingDirectory $folder
    # Logging or backup cleanup failure must never roll back an already started new application.
    try { [IO.File]::WriteAllText([string]$config.Log, ([DateTime]::Now.ToString('s') + ' Update succeeded.')) } catch { }
    try { if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) } } catch { }
} catch {
    $message = $_.Exception.Message
    if ($replaced -and [IO.File]::Exists($backup)) {
        try { [IO.File]::Copy($backup, $target, $true); [IO.File]::Delete($backup) }
        catch { $message += ' Rollback failed: ' + $_.Exception.Message }
    }
    try { [IO.File]::WriteAllText([string]$config.Log, ([DateTime]::Now.ToString('s') + ' ' + $message)) } catch { }
    try { Start-Process -FilePath $target -WorkingDirectory $folder } catch { }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show('SaruMon の更新に失敗しました。' + [Environment]::NewLine + $message + [Environment]::NewLine + '詳細: ' + [string]$config.Log, 'SaruMon 更新', 'OK', 'Error') | Out-Null
} finally {
    if ([IO.File]::Exists($replacement)) { try { [IO.File]::Delete($replacement) } catch { } }
    # Delete only known staging files; leave this tiny script for diagnostics, never delete installation directories.
    foreach ($knownFile in @([string]$config.Source, [string]$config.Ready, $ConfigPath)) {
        try { if ([IO.File]::Exists($knownFile)) { [IO.File]::Delete($knownFile) } } catch { }
    }
}
""";
}
