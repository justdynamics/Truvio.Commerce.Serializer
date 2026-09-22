using System.Collections;
using Truvio.Commerce.Serializer.Configuration;

namespace Truvio.Commerce.Serializer.Models;

/// <summary>
/// Ownership header carried by every serialized YAML document (<c>area.yml</c>, <c>page.yml</c>,
/// <c>grid-row.yml</c>, <c>paragraph-*.yml</c>, SqlTable <c>_meta.yml</c> and row files) as the
/// first top-level key <c>ownership</c>:
/// <code>
/// ownership:
///   mode: merge
/// </code>
/// Deserialize honors the document's own mode (replace = source-wins, merge = destination-wins
/// field fill). A document without a header falls back to the mode of the pass that reads it,
/// which is the mode the config predicate gave the files when they were serialized.
/// </summary>
public sealed record DocumentHeader
{
    /// <summary>
    /// YAML key of the header. A single lower-case word, so the camelCase naming convention leaves
    /// it unchanged on typed documents and row dictionaries alike.
    /// </summary>
    public const string Key = "ownership";

    /// <summary>"replace" or "merge" (lower case on write, case-insensitive on read).</summary>
    public string? Mode { get; init; }

    public static DocumentHeader For(SerializerMode mode) => new() { Mode = mode.ToString().ToLowerInvariant() };

    /// <summary>The header's mode, or null when the header is absent or carries no valid mode.</summary>
    public static SerializerMode? ParseMode(DocumentHeader? header) =>
        header?.Mode is { } value && Enum.TryParse<SerializerMode>(value.Trim(), ignoreCase: true, out var mode)
            ? mode
            : null;

    /// <summary>Conflict strategy for a document: its own mode when present, else <paramref name="fallback"/>.</summary>
    public static ConflictStrategy StrategyFor(SerializerMode? documentMode, ConflictStrategy fallback) =>
        documentMode switch
        {
            SerializerMode.Replace => ConflictStrategy.SourceWins,
            SerializerMode.Merge => ConflictStrategy.DestinationWins,
            _ => fallback
        };

    public static ConflictStrategy StrategyFor(DocumentHeader? header, ConflictStrategy fallback) =>
        StrategyFor(ParseMode(header), fallback);

    /// <summary>
    /// Reads and removes the <see cref="Key"/> entry from an untyped row document (SqlTable row
    /// files deserialize to a dictionary). Returns the header's mode, or null when absent. Only a
    /// mapping value is a header: a scalar under that key is a real column value and stays.
    /// </summary>
    public static SerializerMode? TakeFromRow(IDictionary<string, object?> row)
    {
        if (!row.TryGetValue(Key, out var raw) || raw is not IDictionary map)
            return null;
        row.Remove(Key);

        foreach (DictionaryEntry kv in map)
        {
            if (string.Equals(kv.Key?.ToString(), "mode", StringComparison.OrdinalIgnoreCase))
                return ParseMode(new DocumentHeader { Mode = kv.Value?.ToString() });
        }
        return null;
    }

    /// <summary>Stamps <paramref name="mode"/> onto an area and every page, grid row and paragraph below it.</summary>
    public static SerializedArea Stamp(SerializedArea area, SerializerMode mode)
    {
        var header = For(mode);
        return area with { Ownership = header, Pages = area.Pages.Select(p => Stamp(p, header)).ToList() };
    }

    private static SerializedPage Stamp(SerializedPage page, DocumentHeader header) =>
        page with
        {
            Ownership = header,
            GridRows = page.GridRows.Select(row => row with
            {
                Ownership = header,
                Columns = row.Columns.Select(col => col with
                {
                    Paragraphs = col.Paragraphs.Select(p => p with { Ownership = header }).ToList()
                }).ToList()
            }).ToList(),
            // Foundry #1315: page-level paragraphs get the same ownership stamp as the rest.
            Paragraphs = page.Paragraphs.Select(p => p with { Ownership = header }).ToList(),
            Children = page.Children.Select(c => Stamp(c, header)).ToList()
        };
}
