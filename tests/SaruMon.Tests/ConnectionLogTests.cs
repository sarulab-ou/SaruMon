using System.Globalization;
using System.Text;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class ConnectionLogTests
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SaruMon-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "log.csv");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var first = DateTimeOffset.Parse("2026-01-02T03:04:05+09:00", CultureInfo.InvariantCulture);
            var flow = new UdpFlowInfo("192.0.2.1", 50000, "198.51.100.2", 443,
                2, 1200, first, first.AddMinutes(2));
            ConnectionLogExporter.Write(path, [flow], "Wi-Fi,\"試験\"\n二行目",
                new Dictionary<string, string> { [flow.RemoteAddress] = " =1+1" }, "一部未取得");
            Assert(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Excel-compatible UTF-8 BOM missing.");
            var rows = ReadCsv(File.ReadAllText(path));
            Assert(rows.Count == 2 && rows.All(row => row.Count == 12), "CSV record shape/escaped newlines changed.");
            Assert(rows[0][7] == "開始時刻" && rows[0][8] == "最終時刻", "UDP export must label its first/last observation timestamps consistently.");
            Assert(rows[1][0] == "Wi-Fi,\"試験\"\n二行目", "Adapter name was not round-tripped.");
            Assert(rows[1][6] == "' =1+1", "CSV text can be interpreted as a spreadsheet formula.");
            Assert(DateTimeOffset.Parse(rows[1][7], CultureInfo.InvariantCulture) == first
                && DateTimeOffset.Parse(rows[1][8], CultureInfo.InvariantCulture) == first.AddMinutes(2), "Observation timestamps lost their offsets or precision.");
            Assert(rows[1][9] == "1200" && rows[1][10] == "2" && rows[1][11] == "一部未取得", "UDP totals/status depend on locale or were lost.");
            ConnectionLogExporter.Write(path, [flow with { Bytes = 2000 }], "Wi-Fi");
            rows = ReadCsv(File.ReadAllText(path));
            Assert(rows.Count == 2 && rows[1][1] == "UDP" && rows[1][6] == "" && rows[1][9] == "2000", "Replacement appended rows or leaked cached DNS when reverse lookup was off.");
            var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { ConnectionLogExporter.Write(path, [], "Wi-Fi"); throw new Exception("A locked destination was replaced."); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            Assert(File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(directory).Length == 1,
                "A failed export damaged the existing log or left a temporary file.");
            ConnectionLogExporter.Write(path, [], "Wi-Fi");
            Assert(ReadCsv(File.ReadAllText(path)).Count == 1, "Empty exports must retain their header.");
            return 10;
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
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
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
        if (!condition) throw new InvalidOperationException(message);
    }
}
