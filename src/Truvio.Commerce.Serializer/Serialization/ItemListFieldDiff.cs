using Truvio.Commerce.Serializer.Infrastructure;
using YamlDotNet.Serialization;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: what a delivery would change in one list entry, for the dry run.
/// Values are compared in the form a document carries them: each side is written with the
/// document serializer and read back, so a host <c>true</c> and a YAML <c>"true"</c>, or a host
/// number and its YAML text, are the same value. Pure.
/// </summary>
public static class ItemListFieldDiff
{
    private static readonly ISerializer Serializer = YamlConfiguration.BuildSerializer();
    private static readonly IDeserializer Deserializer = YamlConfiguration.BuildDeserializer();

    /// <summary>
    /// The diff of a source-wins write: every document field whose value differs from the host's,
    /// and every host field the document does not carry that holds a value (the write nulls it).
    /// One line per field: <c>Field: 'host' -> 'document'</c>.
    /// </summary>
    public static IReadOnlyList<string> Overwrite(
        IReadOnlyDictionary<string, object> documentFields,
        IReadOnlyDictionary<string, object?> hostFields,
        Func<string, bool> ignored)
    {
        var host = new Dictionary<string, object?>(hostFields, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var (name, value) in documentFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ignored(name)) continue;
            host.TryGetValue(name, out var current);
            var before = Canonical(current);
            var after = Canonical(value);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                lines.Add($"{name}: '{Short(before)}' -> '{Short(after)}'");
        }

        var carried = new HashSet<string>(documentFields.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in hostFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (carried.Contains(name) || ignored(name)) continue;
            var before = Canonical(value);
            if (before.Length > 0)
                lines.Add($"{name}: '{Short(before)}' -> '' (not in the document)");
        }
        return lines;
    }

    /// <summary>
    /// The diff of a Merge fill: document fields with a value whose host field is empty. One line
    /// per field: <c>Field: '' -> 'document'</c>.
    /// </summary>
    public static IReadOnlyList<string> Fill(
        IReadOnlyDictionary<string, object> documentFields,
        IReadOnlyDictionary<string, object?> hostFields,
        Func<string, bool> ignored)
    {
        var host = new Dictionary<string, object?>(hostFields, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var (name, value) in documentFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ignored(name)) continue;
            host.TryGetValue(name, out var current);
            var after = Canonical(value);
            if (after.Length > 0 && MergePredicate.IsUnsetForMerge(current?.ToString()))
                lines.Add($"{name}: '' -> '{Short(after)}'");
        }
        return lines;
    }

    /// <summary>
    /// A value in the form a document carries it: empty for null, DBNull and the empty string,
    /// otherwise the YAML text of the value after one write and read.
    /// </summary>
    public static string Canonical(object? value)
    {
        if (value is null || value is DBNull) return string.Empty;
        if (value is string s && s.Length == 0) return string.Empty;
        try
        {
            var read = Deserializer.Deserialize<object>(Serializer.Serialize(value));
            if (read is null || read is string { Length: 0 }) return string.Empty;
            // A scalar compares as its text; a mapping or sequence (a button, a link list) as its YAML.
            // A cleared button: Dynamicweb stores an empty ButtonData object for it (engine issue
            // #6), the document carries nothing. Both are empty.
            if (read is string json && IsBlankButton(json))
                return string.Empty;
            // A checkbox reads back as True/False from the item API and as true/false from YAML.
            if (read is string text)
                return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? text.ToLowerInvariant()
                    : text;
            return Serializer.Serialize(read).TrimEnd('\r', '\n');
        }
        catch (Exception)
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// True for a ButtonData JSON object with no selected value, label or link: what Dynamicweb
    /// keeps for a cleared button (its link type and style keep their defaults).
    /// </summary>
    internal static bool IsBlankButton(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.Contains("\"SelectedValue\"", StringComparison.Ordinal))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            foreach (var name in new[] { "SelectedValue", "Label", "Link" })
            {
                if (doc.RootElement.TryGetProperty(name, out var v)
                    && v.ValueKind != System.Text.Json.JsonValueKind.Null
                    && !(v.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrEmpty(v.GetString())))
                    return false;
            }
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Short(string value)
    {
        var flat = value.Replace("\r", "").Replace("\n", "\\n");
        return flat.Length <= 80 ? flat : flat[..77] + "...";
    }
}
