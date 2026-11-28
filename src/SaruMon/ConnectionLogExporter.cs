using System.Globalization;
using System.Text;
using SaruMon.Core;

namespace SaruMon;

internal static class ConnectionLogExporter
{
    // A selected file is replaced only after the entire snapshot has been written.
    internal static void Write(string path, IReadOnlyList<UdpFlowInfo> flows, string interfaceName,
        IReadOnlyDictionary<string, string>? names = null, string observationStatus = "監視中に観測した UDP 送信のみ")
    {
        WriteAtomically(path, writer =>
        {
            WriteRow(writer, ["インタフェース", "プロトコル", "ローカルIP", "ローカルポート", "宛先IP", "宛先ポート",
                "逆引きホスト名", "開始時刻", "最終時刻", "総送信量（バイト）", "送信イベント数", "記録範囲"]);
            foreach (var flow in flows)
            {
                var host = names is not null && names.TryGetValue(flow.RemoteAddress, out var value) ? value : "";
                WriteRow(writer, [interfaceName, "UDP",
                    flow.LocalAddress, Number(flow.LocalPort), flow.RemoteAddress, Number(flow.RemotePort), host,
                    flow.FirstSeen.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                    flow.LastSeen.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                    Number(flow.Bytes), Number(flow.Packets), observationStatus]);
            }
        }, new UTF8Encoding(true));
    }

    internal static void WriteAtomically(string path, Action<TextWriter> write, Encoding encoding)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".SaruMon-log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.NewLine = "\r\n";
                write(writer);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter writer, IEnumerable<string> fields) =>
        writer.WriteLine(string.Join(",", fields.Select(Escape)));

    internal static string Escape(string value)
    {
        // Adapter names and DNS names are untrusted text when opened in spreadsheet software.
        var significant = value.AsSpan().TrimStart();
        if (significant.Length > 0 && significant[0] is '=' or '+' or '-' or '@'
            || value.Length > 0 && value[0] is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
