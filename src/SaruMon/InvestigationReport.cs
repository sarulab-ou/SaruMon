using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Win32;
using SaruMon.Monitoring;

namespace SaruMon;

internal sealed record InvestigationGroup(string Key, string Label, InvestigationRow[] Rows)
{
    internal long Sent { get; } = InvestigationReport.Sum(Rows, row => row.Sent);
    internal long Received { get; } = InvestigationReport.Sum(Rows, row => row.Received);
    internal bool IncludesTcp { get; } = Rows.Any(row => row.Key.Protocol == InvestigationProtocol.Tcp);
    internal DateTimeOffset LastSeen { get; } = Rows.Max(row => row.LastSeen);
}

internal sealed record InvestigationRecord(string Interface, DateTimeOffset Started, DateTimeOffset Ended,
    IReadOnlyList<InvestigationRow> Rows, long DroppedEvents, bool Incomplete);

internal static class InvestigationReport
{
    internal const int MaxMarkdownBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static InvestigationGroup[] Applications(IEnumerable<InvestigationRow> rows) =>
        rows.GroupBy(ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InvestigationGroup(group.Key, ApplicationLabel(group.First()), group.ToArray())).ToArray();

    internal static InvestigationGroup[] Destinations(IEnumerable<InvestigationRow> rows) =>
        rows.GroupBy(row => row.Key.Address, StringComparer.Ordinal)
            .Select(group => new InvestigationGroup(group.Key, group.Key, group.ToArray())).ToArray();

    internal static string ApplicationKey(InvestigationRow row) =>
        row.Path ?? $"#{row.Key.Process.Pid}:{row.Key.Process.StartedUtcTicks}";

    internal static string ApplicationLabel(InvestigationRow row) => row.Path is not null
        ? Path.GetFileName(row.Path)
        : row.Key.Process.Pid > 0 ? $"発生元未確認 (PID {row.Key.Process.Pid})" : "発生元未確認";

    internal static long Sum(IEnumerable<InvestigationRow> rows, Func<InvestigationRow, long> select) =>
        rows.Aggregate(0L, (total, row) => InvestigationCapture.Add(total, select(row)));

    internal static string Markdown(InvestigationRecord record, InvestigationGroup? application, string? startup)
    {
        var rows = application?.Rows ?? record.Rows;
        var destinations = Destinations(rows);
        var ranked = destinations.OrderByDescending(item => item.Sent).ThenBy(item => item.Key).Take(25)
            .Concat(destinations.Where(item => item.IncludesTcp).OrderByDescending(item => item.Received).ThenBy(item => item.Key).Take(25))
            .Concat(destinations.OrderByDescending(item => item.LastSeen).ThenBy(item => item.Key).Take(25))
            .DistinctBy(item => item.Key).ToArray();
        var report = new StringBuilder();
        var bytes = 0;
        var destinationsWritten = 0;
        var detailsWritten = 0;
        Append("# SaruMon 通信調査\n\n");
        Append($"- 対象: {Cell(record.Interface, 256)} / {Cell(application?.Label ?? "全体", 1024)}\n"
            + $"- 観測: {Time(record.Started)} ～ {Time(record.Ended)}\n"
            + $"- 集計: 宛先 {destinations.Length} 件、内訳 {rows.Count} 行、発生元未確認 {rows.Count(row => row.Path is null)} 行\n"
            + $"- 上り（TCP+UDP）: {Number(Sum(rows, row => row.Sent))} B / 下り（TCP）: {(rows.Any(row => row.Key.Protocol == InvestigationProtocol.Tcp) ? Number(Sum(rows, row => row.Received)) + " B" : "未観測")}\n"
            + $"- 記録状態: {(record.Incomplete ? "部分記録" : "観測範囲内で集計")} / 未記録イベント（判明分）: {Number(record.DroppedEvents)}\n\n");
        if (startup is not null)
            Append($"## 自動起動（保存時点の Run / RunOnce）\n\n{Cell(startup, 1024)}\n\n");
        Append("## 宛先別\n\n| 宛先IP | 上り B | 下り B (TCP) | アプリ数 | 最終観測 |\n|---|---:|---:|---:|---|\n");
        foreach (var group in ranked)
        {
            if (!Append($"| {Cell(group.Key)} | {Number(group.Sent)} | {(group.IncludesTcp ? Number(group.Received) : "—")} | "
                + $"{group.Rows.Select(ApplicationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()} | {Time(group.LastSeen)} |\n"))
                break;
            destinationsWritten++;
        }
        Append("\n## 主な内訳\n\n| 宛先IP | プロトコル | ポート | PID | 実行ファイル | 上り B | 下り B (TCP) | イベント数 | 初回 | 最終 |\n|---|---|---:|---:|---|---:|---:|---:|---|---|\n");
        // Give each included destination one detail before adding further flows.
        var groups = ranked.Take(destinationsWritten).Select(group => group.Rows
            .OrderByDescending(row => InvestigationCapture.Add(row.Sent, row.Received)).ThenByDescending(row => row.LastSeen)
            .ThenBy(row => row.Key.Port).ThenBy(row => row.Key.Protocol).ThenBy(row => row.Key.Process.Pid).ToArray()).ToArray();
        foreach (var row in groups.Select(group => group[0]).Concat(groups.SelectMany(group => group.Skip(1))))
        {
            if (!Append($"| {Cell(row.Key.Address)} | {Protocol(row.Key.Protocol)} | {row.Key.Port} | {Pid(row)} | "
                + $"{Cell(row.Path ?? "発生元未確認", InvestigationCapture.MaxPathLength)} | {Number(row.Sent)} | "
                + $"{(row.Key.Protocol == InvestigationProtocol.Tcp ? Number(row.Received) : "—")} | {Number(row.Events)} | "
                + $"{Time(row.FirstSeen)} | {Time(row.LastSeen)} |\n", reserve: 512))
                break;
            detailsWritten++;
        }
        Append($"\n省略した宛先: {destinations.Length - destinationsWritten} 件。省略した内訳: {rows.Count - detailsWritten} 行。\n");
        return report.ToString();

        bool Append(string line, int reserve = 0)
        {
            var size = Utf8.GetByteCount(line);
            if (bytes + size + reserve > MaxMarkdownBytes) return false;
            report.Append(line);
            bytes += size;
            return true;
        }
    }

    internal static void SaveMarkdown(string path, string report) => ConnectionLogExporter.WriteAtomically(path, writer => writer.Write(report), Utf8);

    internal static void SaveCsv(string path, InvestigationRecord record) =>
        ConnectionLogExporter.WriteAtomically(path, writer =>
        {
            Write("インタフェース", "観測開始", "観測終了", "記録状態", "未記録イベント（判明分）",
                "アプリ", "PID", "プロセス起動UTC", "実行ファイル", "宛先IP", "ポート", "プロトコル",
                "上りバイト", "下りバイト（TCP）", "イベント数", "初回観測", "最終観測");
            var metadata = new[] { record.Interface, Time(record.Started), Time(record.Ended), record.Incomplete ? "部分記録" : "観測範囲内で集計", Number(record.DroppedEvents) };
            foreach (var row in record.Rows.OrderByDescending(row => row.LastSeen))
            {
                Write([.. metadata, ApplicationLabel(row), Pid(row), row.Key.Process.StartedUtcTicks == 0 ? "" :
                    Time(new DateTimeOffset(row.Key.Process.StartedUtcTicks, TimeSpan.Zero)), row.Path ?? "", row.Key.Address,
                    Number(row.Key.Port), Protocol(row.Key.Protocol), Number(row.Sent),
                    row.Key.Protocol == InvestigationProtocol.Tcp ? Number(row.Received) : "", Number(row.Events), Time(row.FirstSeen), Time(row.LastSeen)]);
            }

            void Write(params string[] fields) => writer.WriteLine(string.Join(",", fields.Select(ConnectionLogExporter.Escape)));
        }, new UTF8Encoding(true));

    internal static string StartupMatches(string? path)
    {
        if (path is null) return "実行ファイルのパスを確認できなかったため未取得";
        var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = false;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var name in new[] { "Run", "RunOnce" })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\{name}");
                if (key is null) continue;
                foreach (var value in key.GetValueNames())
                {
                    if (key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command
                        && MatchesCommand(command, path))
                        entries.Add($"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{name} · {value}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { unreadable = true; }
        }
        var result = entries.Count == 0 ? "一致する登録は見つかりませんでした" : string.Join(" / ", entries.Take(10));
        if (entries.Count > 10) result += $"（ほか {entries.Count - 10} 件）";
        return unreadable ? result + "（一部のキーは未取得）" : result;
    }

    internal static bool MatchesCommand(string command, string path)
    {
        var text = command.TrimStart();
        if (text.Length == 0) return false;
        int end;
        if (text[0] == '"')
        {
            end = text.IndexOf('"', 1);
            if (end < 0 || end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])) return false;
            text = text[1..end];
        }
        else
        {
            end = text.IndexOfAny([' ', '\t']);
            if (end >= 0) text = text[..end];
        }
        try
        {
            var executable = Environment.ExpandEnvironmentVariables(text);
            return Path.IsPathFullyQualified(executable) && string.Equals(Path.GetFullPath(executable),
                Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    internal static string Protocol(InvestigationProtocol protocol) => protocol == InvestigationProtocol.Tcp ? "TCP" : "UDP";
    internal static string Pid(InvestigationRow row) => row.Key.Process.Pid > 0 ? Number(row.Key.Process.Pid) : "—";
    private static string Number(long number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static string Cell(string text, int limit = 80)
    {
        if (text.Length > limit) text = text[..limit] + "…";
        return new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "&#124;").Replace("\x60", "&#96;");
    }
}
