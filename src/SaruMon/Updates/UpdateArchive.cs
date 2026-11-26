using System.IO.Compression;
using System.Security.Cryptography;

namespace SaruMon.Updates;

public static class UpdateArchive
{
    public const string AssetName = "SaruMon-win-x64.zip";
    public const string ChecksumName = AssetName + ".sha256";
    public const long MaximumDownloadBytes = 180L * 1024 * 1024;
    public const long MaximumExecutableBytes = 300L * 1024 * 1024;

    public static string ParseChecksum(string text)
    {
        // Accept the standard sha256sum / Get-FileHash formats used by the release script.
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) throw new InvalidDataException("チェックサムの形式が正しくありません。");
        var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)
            || (parts.Length == 2 && parts[1].TrimStart('*') != AssetName))
            throw new InvalidDataException("チェックサムの形式が正しくありません。");
        return parts[0].ToUpperInvariant();
    }

    public static async Task VerifyChecksumAsync(string zipPath, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新ファイルの SHA-256 が一致しません。インストールを中止しました。");
    }

    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':')
            || name.StartsWith('/') || name.Any(char.IsControl)) return false;
        return !name.Split('/').Any(p => p is "." or "..");
    }

    /// <summary>Only the root executable is extracted. No release-supplied scripts are run.</summary>
    public static async Task<string> ExtractExecutableAsync(string zipPath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is 0 or > 100)
            throw new InvalidDataException("更新 ZIP のファイル数が正しくありません。");
        ZipArchiveEntry? executable = null;
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeEntryName(entry.FullName) || !names.Add(entry.FullName))
                throw new InvalidDataException("更新 ZIP に不正なファイル名があります。");
            total = checked(total + entry.Length);
            if (total > MaximumExecutableBytes + 10 * 1024 * 1024)
                throw new InvalidDataException("更新 ZIP の展開サイズが大きすぎます。");
            // Reject Unix symlinks even though this routine never creates one.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新 ZIP にシンボリックリンクがあります。");
            if (entry.FullName == "SaruMon.exe") executable = entry;
        }
        if (executable is null || executable.Length < 2 || executable.Length > MaximumExecutableBytes)
            throw new InvalidDataException("更新 ZIP の直下に SaruMon.exe が見つかりません。");
        Directory.CreateDirectory(destination);
        var path = Path.Combine(destination, "SaruMon.exe");
        await using (var input = executable.Open())
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > executable.Length || copied > MaximumExecutableBytes)
                    throw new InvalidDataException("実行ファイルのサイズが正しくありません。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (copied != executable.Length) throw new InvalidDataException("更新ファイルが不完全です。");
        }
        using (var input = File.OpenRead(path))
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                throw new InvalidDataException("更新ファイルは Windows 実行ファイルではありません。");
        return path;
    }
}
