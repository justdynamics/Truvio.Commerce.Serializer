namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// A Serializer package version (<c>1.0.9-beta</c>, <c>1.1.0</c>): numeric parts compared as
/// numbers, then a release above any pre-release of the same numbers, then pre-release labels
/// part by part (numbers as numbers, text ordinally), as SemVer orders them. Build metadata
/// (<c>+sha</c>) is ignored.
/// </summary>
public sealed record EngineVersion(IReadOnlyList<int> Numbers, IReadOnlyList<string> PreRelease)
{
    public static bool TryParse(string? text, out EngineVersion version)
    {
        version = new EngineVersion(Array.Empty<int>(), Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        var dash = core.IndexOf('-');
        var numbersPart = dash >= 0 ? core[..dash] : core;
        var pre = dash >= 0 ? core[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var numbers = new List<int>();
        foreach (var part in numbersPart.Split('.'))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return false;
            numbers.Add(n);
        }
        if (numbers.Count is < 1 or > 4) return false;
        while (numbers.Count < 3) numbers.Add(0);
        version = new EngineVersion(numbers, pre);
        return true;
    }

    public static EngineVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version.");

    public static int Compare(EngineVersion a, EngineVersion b)
    {
        for (var i = 0; i < Math.Max(a.Numbers.Count, b.Numbers.Count); i++)
        {
            var x = i < a.Numbers.Count ? a.Numbers[i] : 0;
            var y = i < b.Numbers.Count ? b.Numbers[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        // A release is above every pre-release of the same numbers.
        if (a.PreRelease.Count == 0 && b.PreRelease.Count == 0) return 0;
        if (a.PreRelease.Count == 0) return 1;
        if (b.PreRelease.Count == 0) return -1;
        for (var i = 0; i < Math.Min(a.PreRelease.Count, b.PreRelease.Count); i++)
        {
            var pa = a.PreRelease[i];
            var pb = b.PreRelease[i];
            var na = int.TryParse(pa, out var ia);
            var nb = int.TryParse(pb, out var ib);
            int c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa, pb);
            if (c != 0) return Math.Sign(c);
        }
        return a.PreRelease.Count.CompareTo(b.PreRelease.Count);
    }

    /// <summary>The higher of two floors (null when both are null).</summary>
    public static string? Max(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return string.IsNullOrWhiteSpace(b) ? null : b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        if (!TryParse(a, out var va)) return a;
        if (!TryParse(b, out var vb)) return b;
        return Compare(va, vb) >= 0 ? a : b;
    }
}
