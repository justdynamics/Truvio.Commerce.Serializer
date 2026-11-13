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
    /// <param name="documentFields">The fields the write is handed.</param>
    /// <param name="hostFields">The item's fields on the host.</param>
    /// <param name="ignored">Fields the write does not touch (system fields, exclusions, lists).</param>
    /// <param name="onlyFieldsTheHostHas">
    /// True for an item write through the item API, which ignores a document field the item type
    /// does not declare: such a field is no change.
    /// </param>
    public static IReadOnlyList<string> Overwrite(
        IReadOnlyDictionary<string, object> documentFields,
        IReadOnlyDictionary<string, object?> hostFields,
        Func<string, bool> ignored,
        bool onlyFieldsTheHostHas = false)
    {
        var host = new Dictionary<string, object?>(hostFields, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var (name, value) in documentFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ignored(name)) continue;
            if (onlyFieldsTheHostHas && !host.ContainsKey(name)) continue;
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
            // Line endings: a document carries LF, a host may hold CRLF for the same text.
            // A JSON value (a button) compares as its data, not its formatting: the host holds it
            // compact with \u escapes, a document may carry it indented or with other spacing.
            if (read is string jsonText && CanonicalJson(jsonText) is { } normalized)
                return normalized;
            if (read is string text)
                return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? text.ToLowerInvariant()
                    : text.Replace("\r\n", "\n");
            return Serializer.Serialize(read).TrimEnd('\r', '\n');
        }
        catch (Exception)
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// A JSON object or array as compact text with sorted keys and unescaped characters; null when
    /// the text is not a JSON object or array.
    /// </summary>
    internal static string? CanonicalJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || !((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions
                   { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                WriteSorted(writer, doc.RootElement);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void WriteSorted(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
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
