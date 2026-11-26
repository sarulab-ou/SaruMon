using System.Globalization;
using System.Text;
using SaruMon.Core;

namespace SaruMon;

internal static class TcpLogExporter
{
    internal static void Write(string path, IReadOnlyList<TcpPeerInfo> peers,
        IReadOnlyDictionary<string, string>? names = null)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".SaruMon-tcp-log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.NewLine = "\r\n";
                WriteRow(writer, ["相手IP", "相手名", "上り合計（バイト）", "下り合計（バイト）", "接続回数", "開始時刻", "最終時刻"]);
                foreach (var peer in peers)
                {
                    var host = names is not null && names.TryGetValue(peer.RemoteAddress, out var value) ? value : "";
                    WriteRow(writer, [peer.RemoteAddress, host, peer.SentBytes.ToString(CultureInfo.InvariantCulture),
                        peer.ReceivedBytes.ToString(CultureInfo.InvariantCulture), peer.ConnectionCount.ToString(CultureInfo.InvariantCulture),
                        peer.FirstSeen.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                        peer.LastSeen.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)]);
                }
            }
            // A failed write never replaces the user's existing CSV with a partial log.
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string> fields) =>
        writer.WriteLine(string.Join(",", fields.Select(Escape)));

    private static string Escape(string value)
    {
        var significant = value.AsSpan().TrimStart();
        if (significant.Length > 0 && significant[0] is '=' or '+' or '-' or '@'
            || value.Length > 0 && value[0] is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
