using System.Globalization;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Foundry #1322: the raise-only rule behind a SqlTable entry's <c>raiseOnlyColumns</c>. A
/// payload row whose key matches a target row writes each listed column as the larger of the
/// target value and the shipped value, so a shipped counter (for example
/// <c>EcomNumbers.NumberCounter</c>) only ever raises the target value and never lowers it.
/// Null on either side: the non-null value wins, so a null shipped value never clears the target.
/// A payload row with no target row inserts as shipped.
///
/// The rule rewrites the incoming row against the target snapshot BEFORE the checksum compare
/// and the merge-fill, so the effective row is what is compared, reported and written: a row
/// whose only difference is a lower shipped counter is skipped as unchanged.
/// </summary>
internal sealed class RaiseOnlyColumns
{
    private readonly IReadOnlyList<string> _columns;
    private readonly Dictionary<string, Tally> _tallies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The columns the rule applies to, in declaration order.</summary>
    public IReadOnlyList<string> Columns => _columns;

    public RaiseOnlyColumns(IReadOnlyList<string> columns)
    {
        _columns = columns;
        foreach (var col in columns)
            _tallies[col] = new Tally();
    }

    /// <summary>
    /// Applies the rule to <paramref name="incoming"/> in place against
    /// <paramref name="target"/> (the matched target row, or <c>null</c> when the payload row
    /// has no target row) and returns one detail line per column whose shipped value was raised
    /// or held back.
    /// </summary>
    public IReadOnlyList<string> Apply(
        Dictionary<string, object?> incoming,
        Dictionary<string, object?>? target)
    {
        var details = new List<string>();
        foreach (var col in _columns)
        {
            if (!incoming.TryGetValue(col, out var shippedRaw)) continue;   // nothing shipped
            var tally = _tallies[col];

            if (target == null)
            {
                tally.Inserted++;
                continue;
            }
            if (!target.TryGetValue(col, out var targetRaw)) continue;       // not on the target

            var shipped = Normalize(shippedRaw);
            var current = Normalize(targetRaw);

            if (current == null)
            {
                // A null target never holds a shipped value back.
                if (shipped != null)
                {
                    tally.Raised++;
                    details.Add($"{col}: raised <null> -> {Format(shipped)}");
                }
                else
                {
                    tally.Kept++;
                }
                continue;
            }

            if (shipped == null)
            {
                incoming[col] = targetRaw;
                tally.Kept++;
                details.Add($"{col}: shipped <null>, target {Format(current)} kept");
                continue;
            }

            var comparison = CompareNumeric(shipped, current);
            if (comparison == null)
            {
                tally.NotNumeric++;
                continue;                                                    // written as shipped
            }

            if (comparison > 0)
            {
                tally.Raised++;
                details.Add($"{col}: raised {Format(current)} -> {Format(shipped)}");
            }
            else
            {
                incoming[col] = targetRaw;
                tally.Kept++;
                if (comparison < 0)
                    details.Add($"{col}: shipped {Format(shipped)}, target {Format(current)} kept");
            }
        }
        return details;
    }

    /// <summary>
    /// True when <paramref name="effective"/> (an incoming value after <see cref="Apply"/>) is
    /// higher than <paramref name="target"/>, i.e. the column must be written. Used by the
    /// merge-fill, which otherwise only fills unset target columns.
    /// </summary>
    public static bool IsHigher(object? effective, object? target)
    {
        var e = Normalize(effective);
        var t = Normalize(target);
        if (e == null) return false;
        if (t == null) return true;
        return CompareNumeric(e, t) > 0;
    }

    /// <summary>
    /// The one info line per entry, for example
    /// <c>[EcomNumbers] raiseOnlyColumns NumberCounter: 3 raised, 12 kept (target higher or equal)</c>.
    /// </summary>
    public string Summary(string tableName)
    {
        var parts = _columns.Select(col =>
        {
            var t = _tallies[col];
            var text = $"{col}: {t.Raised} raised, {t.Kept} kept (target higher or equal)";
            if (t.Inserted > 0) text += $", {t.Inserted} inserted as shipped (no target row)";
            if (t.NotNumeric > 0) text += $", {t.NotNumeric} not comparable as numbers (written as shipped)";
            return text;
        });
        return $"[{tableName}] raiseOnlyColumns {string.Join("; ", parts)}";
    }

    internal (int Raised, int Kept, int Inserted) CountsFor(string column)
    {
        var t = _tallies[column];
        return (t.Raised, t.Kept, t.Inserted);
    }

    private static object? Normalize(object? value) => value is DBNull ? null : value;

    private static int? CompareNumeric(object a, object b)
    {
        try
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            try
            {
                return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    private static string Format(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private sealed class Tally
    {
        public int Raised;
        public int Kept;
        public int Inserted;
        public int NotNumeric;
    }
}
