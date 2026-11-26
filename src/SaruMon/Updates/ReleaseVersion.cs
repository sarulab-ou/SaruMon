using System.Text.RegularExpressions;

namespace SaruMon.Updates;

/// <summary>Semantic versions; build metadata does not affect precedence.</summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly string[] _core;
    private readonly string[] _prerelease;
    public string Text { get; }
    public bool IsPrerelease => _prerelease.Length != 0;

    private ReleaseVersion(string text, string[] core, string[] prerelease)
    {
        Text = text;
        _core = core;
        _prerelease = prerelease;
    }

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (value is null || value.Length > 200) return false;
        var text = value.Trim();
        if (text.StartsWith('v')) text = text[1..];
        var match = VersionPattern().Match(text);
        if (!match.Success) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(p => p.Length > 1 && p[0] == '0' && IsNumeric(p))) return false;
        version = new ReleaseVersion(text,
            [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumber(_core[i], other._core[i]);
            if (comparison != 0) return comparison;
        }
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumber = IsNumeric(left);
            var rightNumber = IsNumeric(right);
            var comparison = leftNumber && rightNumber ? CompareNumber(left, right)
                : leftNumber != rightNumber ? (leftNumber ? -1 : 1)
                : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.All(c => c is >= '0' and <= '9');
    private static int CompareNumber(string left, string right) => left.Length == right.Length
        ? string.CompareOrdinal(left, right) : left.Length.CompareTo(right.Length);
    public override string ToString() => Text;

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
