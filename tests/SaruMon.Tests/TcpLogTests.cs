using System.Globalization;
using System.Text;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TcpLogTests
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SaruMon-tcp-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "log.csv");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var first = DateTimeOffset.Parse("2026-01-02T03:04:05+09:00", CultureInfo.InvariantCulture);
            var peer = new TcpPeerInfo("198.51.100.2", 1234, 5678, first, first.AddMinutes(1));
            TcpLogExporter.Write(path, [peer], new Dictionary<string, string> { [peer.RemoteAddress] = "test,\"name\"\nsecond line" });
            Assert(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "UTF-8 BOM missing.");
            var rows = ReadCsv(File.ReadAllText(path));
            Assert(rows.Count == 2 && rows.All(row => row.Count == 4)
                && rows[0].SequenceEqual(new[] { "相手IP", "相手名", "上り合計（バイト）", "下り合計（バイト）" }),
                "TCP export must contain exactly the four requested columns.");
            Assert(rows[1][0] == peer.RemoteAddress && rows[1][1] == "test,\"name\"\nsecond line", "IP/name quoting lost data or split a row.");
            Assert(rows[1][2] == "1234" && rows[1][3] == "5678", "Upload/download totals were exchanged or formatted with locale separators.");
            TcpLogExporter.Write(path, [peer], new Dictionary<string, string> { [peer.RemoteAddress] = " @SUM(1,2)" });
            Assert(ReadCsv(File.ReadAllText(path))[1][1] == "' @SUM(1,2)", "Untrusted hostname may execute as a spreadsheet formula.");
            TcpLogExporter.Write(path, [peer with { SentBytes = 2000 }]);
            rows = ReadCsv(File.ReadAllText(path));
            Assert(rows.Count == 2 && rows[1][1] == "" && rows[1][2] == "2000", "Reverse-DNS-off export leaked names or appended stale rows.");
            var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { TcpLogExporter.Write(path, []); throw new InvalidOperationException("Locked destination unexpectedly replaced."); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            Assert(File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(directory).Length == 1,
                "Failed export damaged the existing file or left temporary content.");
            TcpLogExporter.Write(path, []);
            Assert(ReadCsv(File.ReadAllText(path)).Count == 1, "Empty exports must preserve their header.");
            return 8;
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if (character == '\n' && !quoted)
            {
                row.Add(field.ToString().TrimEnd('\r')); field.Clear(); rows.Add(row); row = [];
            }
            else field.Append(character);
        }
        Assert(!quoted && row.Count == 0 && field.Length == 0, "Malformed CSV.");
        return rows;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TCP log test failed: " + message);
    }
}
