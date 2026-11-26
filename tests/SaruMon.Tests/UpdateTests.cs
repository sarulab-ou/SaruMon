using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SaruMon.Updates;

namespace SaruMon.Tests;

public static class UpdateTests
{
    public static int Run()
    {
        var count = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Update test failed: " + message);
            count++;
        }
        ReleaseVersion Version(string text)
        {
            if (!ReleaseVersion.TryParse(text, out var result)) throw new Exception("Version parse failed: " + text);
            return result!;
        }
        void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { count++; return; }
            throw new Exception("Update test failed: " + message);
        }

        Assert(Version("v1.2.3").CompareTo(Version("1.2.3+build.9")) == 0, "v prefix and metadata");
        Assert(Version("1.10.0").CompareTo(Version("1.9.99")) > 0, "numeric version comparison");
        Assert(Version("1.0.0").CompareTo(Version("1.0.0-rc.99")) > 0, "stable follows prerelease");
        Assert(Version("1.0.0-rc.10").CompareTo(Version("1.0.0-rc.2")) > 0, "numeric prerelease");
        Assert(Version("1.0.0-1").CompareTo(Version("1.0.0-alpha")) < 0, "numeric precedes alphabetic");
        Assert(Version("1.0.0-alpha").CompareTo(Version("1.0.0-alpha.1")) < 0, "shorter prerelease");
        Assert(!ReleaseVersion.TryParse("1.02.0", out _), "reject zero-padded core");
        Assert(!ReleaseVersion.TryParse("1.2.3-01", out _), "reject zero-padded prerelease");
        Assert(!ReleaseVersion.TryParse("1.2", out _), "reject incomplete semver");
        Assert(!ReleaseVersion.TryParse("1.2.3<script>", out _), "reject invalid tag");
        Assert(Version("99999999999999999999.0.0").CompareTo(Version("9.0.0")) > 0, "large number does not overflow");

        var hash = new string('a', 64);
        Assert(UpdateArchive.ParseChecksum(hash + "  SaruMon-win-x64.zip\n") == hash.ToUpperInvariant(), "standard checksum");
        Assert(UpdateArchive.ParseChecksum(hash + " *SaruMon-win-x64.zip\r\n") == hash.ToUpperInvariant(), "binary checksum marker");
        Throws<InvalidDataException>(() => UpdateArchive.ParseChecksum(hash + "  Other.zip"), "checksum filename");
        Throws<InvalidDataException>(() => UpdateArchive.ParseChecksum(new string('z', 64)), "checksum hex");
        Throws<InvalidDataException>(() => UpdateArchive.ParseChecksum(hash + "\n" + hash), "multiple checksums");
        foreach (var path in new[] { "../SaruMon.exe", "/SaruMon.exe", "C:/SaruMon.exe", "folder/../../SaruMon.exe", "folder\\SaruMon.exe", "a/./b", "a\0b" })
            Assert(!UpdateArchive.IsSafeEntryName(path), "unsafe path " + path);
        Assert(UpdateArchive.IsSafeEntryName("SaruMon.exe"), "normal root entry");
        Assert(UpdateService.IsReleaseAssetUrl(new Uri("https://github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip"), UpdateArchive.AssetName), "valid asset URL");
        foreach (var url in new[]
        {
            "http://github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip",
            "https://github.com.evil.invalid/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip",
            "https://github.com/other/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip",
            "https://github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/Other.exe",
            "https://user:pass@github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip"
        }) Assert(!UpdateService.IsReleaseAssetUrl(new Uri(url), UpdateArchive.AssetName), "reject unsafe asset URL");

        var json = """
{"tag_name":"v1.2.3","draft":false,"prerelease":false,"body":"release notes","assets":[
{"name":"SaruMon-win-x64.zip","browser_download_url":"https://github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip"},
{"name":"SaruMon-win-x64.zip.sha256","browser_download_url":"https://github.com/sarulab-ou/SaruMon/releases/download/v1.2.3/SaruMon-win-x64.zip.sha256"}]}
""";
        Assert(UpdateService.ParseRelease(json).Version.Text == "1.2.3", "release response");
        Throws<InvalidDataException>(() => UpdateService.ParseRelease(json.Replace("\"draft\":false", "\"draft\":true")), "reject draft");
        Throws<InvalidDataException>(() => UpdateService.ParseRelease(json.Replace("\"prerelease\":false", "\"prerelease\":true")), "reject prerelease");
        Throws<InvalidDataException>(() => UpdateService.ParseRelease(json.Replace(".zip.sha256", ".zip.sha256-missing")), "missing checksum");

        var temp = Path.Combine(Path.GetTempPath(), "SaruMon-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string MakeZip(string filename, params (string Name, byte[] Data)[] entries)
            {
                var zip = Path.Combine(temp, filename);
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var (name, data) in entries)
                {
                    using var stream = archive.CreateEntry(name).Open();
                    stream.Write(data);
                }
                return zip;
            }
            var zip = MakeZip("valid.zip", ("SaruMon.exe", new byte[] { (byte)'M', (byte)'Z', 0, 0 }), ("README.md", Encoding.UTF8.GetBytes("README")));
            var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
            UpdateArchive.VerifyChecksumAsync(zip, expected, default).GetAwaiter().GetResult();
            count++;
            Throws<InvalidDataException>(() => UpdateArchive.VerifyChecksumAsync(zip, new string('0', 64), default).GetAwaiter().GetResult(), "mismatched archive hash");
            var output = Path.Combine(temp, "valid");
            var exe = UpdateArchive.ExtractExecutableAsync(zip, output, default).GetAwaiter().GetResult();
            Assert(File.Exists(exe) && !File.Exists(Path.Combine(output, "README.md")), "only extract executable");
            var traversal = MakeZip("traversal.zip", ("../SaruMon.exe", new byte[] { 77, 90 }));
            Throws<InvalidDataException>(() => UpdateArchive.ExtractExecutableAsync(traversal, Path.Combine(temp, "traversal"), default).GetAwaiter().GetResult(), "reject archive traversal");
            var duplicate = MakeZip("duplicate.zip", ("SaruMon.exe", new byte[] { 77, 90 }), ("sarumon.exe", new byte[] { 77, 90 }));
            Throws<InvalidDataException>(() => UpdateArchive.ExtractExecutableAsync(duplicate, Path.Combine(temp, "duplicate"), default).GetAwaiter().GetResult(), "reject case-insensitive duplicate");
            var nonPe = MakeZip("nonpe.zip", ("SaruMon.exe", new byte[] { 1, 2, 3 }));
            Throws<InvalidDataException>(() => UpdateArchive.ExtractExecutableAsync(nonPe, Path.Combine(temp, "nonpe"), default).GetAwaiter().GetResult(), "reject non-executable");
            var missing = MakeZip("missing.zip", ("README.md", new byte[] { 1, 2 }));
            Throws<InvalidDataException>(() => UpdateArchive.ExtractExecutableAsync(missing, Path.Combine(temp, "missing"), default).GetAwaiter().GetResult(), "missing executable");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => UpdateArchive.VerifyChecksumAsync(zip, expected, cancelled.Token).GetAwaiter().GetResult(), "cancellation");
        }
        finally
        {
            // This directory was just allocated by this test and contains only its fixtures.
            Directory.Delete(temp, true);
        }
        return count;
    }
}
