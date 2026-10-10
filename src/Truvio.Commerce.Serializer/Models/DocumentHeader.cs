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
///   onExisting: keep
/// </code>
/// Deserialize honors the document's own mode (replace = source-wins, merge = destination-wins
/// field fill). A document without a header falls back to the mode of the pass that reads it,
/// which is the mode the config predicate gave the files when they were serialized.
/// <para>
/// <c>onExisting</c> (optional) is the document's lifecycle on a host that already has it:
/// <c>overwrite</c>, <c>fill</c> or <c>keep</c> (<see cref="ExistingDocumentLifecycle"/>). When
/// present it decides what happens to an existing document; when absent the mode decides
/// (replace = overwrite, merge = fill), exactly as before the key existed. A missing document is
/// created in every lifecycle.
/// </para>
/// </summary>
public sealed record DocumentHeader
{
    /// <summary>
    /// YAML key of the header. A single lower-case word, so the camelCase naming convention leaves
    /// it unchanged on typed documents and row dictionaries alike.
    /// </summary>
    public const string Key = "ownership";

    /// <summary>YAML key of the lifecycle inside the header.</summary>
    public const string OnExistingKey = "onExisting";

    /// <summary>"replace" or "merge" (lower case on write, case-insensitive on read).</summary>
    public string? Mode { get; init; }

    /// <summary>
    /// "overwrite", "fill" or "keep" (lower case on write, case-insensitive on read), or null when
    /// the document's mode decides. Omitted from YAML when null, so documents serialized without a
    /// lifecycle are byte-identical to those written before the key existed.
    /// </summary>
    public string? OnExisting { get; init; }

    public static DocumentHeader For(SerializerMode mode) => new() { Mode = mode.ToString().ToLowerInvariant() };

    /// <summary>Header with a mode and an optional lifecycle (null leaves <see cref="OnExisting"/> unset).</summary>
    public static DocumentHeader For(SerializerMode mode, ExistingDocumentLifecycle? onExisting) => new()
    {
        Mode = mode.ToString().ToLowerInvariant(),
        OnExisting = onExisting?.ToString().ToLowerInvariant()
    };

    /// <summary>The header's mode, or null when the header is absent or carries no valid mode.</summary>
    public static SerializerMode? ParseMode(DocumentHeader? header) =>
        header?.Mode is { } value && Enum.TryParse<SerializerMode>(value.Trim(), ignoreCase: true, out var mode)
            ? mode
            : null;

    /// <summary>
    /// Parses a lifecycle value ("overwrite", "fill", "keep", case-insensitive). Null for null,
    /// blank or unknown text; use <see cref="IsValidLifecycle"/> to tell blank from unknown.
    /// </summary>
    public static ExistingDocumentLifecycle? ParseLifecycle(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse<ExistingDocumentLifecycle>(value.Trim(), ignoreCase: true, out var lifecycle)
        && Enum.IsDefined(lifecycle)
        && !int.TryParse(value.Trim(), out _)
            ? lifecycle
            : null;

    /// <summary>True for null/blank (unset) or a known lifecycle name.</summary>
    public static bool IsValidLifecycle(string? value) =>
        string.IsNullOrWhiteSpace(value) || ParseLifecycle(value) is not null;

    /// <summary>The header's own lifecycle, or null when it carries none (or an unknown value).</summary>
    public static ExistingDocumentLifecycle? ParseOnExisting(DocumentHeader? header) => ParseLifecycle(header?.OnExisting);

    /// <summary>
    /// Effective lifecycle of a document on a host that already has it: its <c>onExisting</c> when
    /// set, else its mode (replace = overwrite, merge = fill), else the pass strategy.
    /// </summary>
    public static ExistingDocumentLifecycle LifecycleFor(DocumentHeader? header, ConflictStrategy fallback) =>
        ParseOnExisting(header)
        ?? (StrategyFor(ParseMode(header), fallback) == ConflictStrategy.DestinationWins
            ? ExistingDocumentLifecycle.Fill
            : ExistingDocumentLifecycle.Overwrite);

    /// <summary>True when an existing copy of this document must be left untouched.</summary>
    public static bool IsKeep(DocumentHeader? header) => ParseOnExisting(header) == ExistingDocumentLifecycle.Keep;

    /// <summary>Conflict strategy for a document: its own mode when present, else <paramref name="fallback"/>.</summary>
    public static ConflictStrategy StrategyFor(SerializerMode? documentMode, ConflictStrategy fallback) =>
        documentMode switch
        {
            SerializerMode.Replace => ConflictStrategy.SourceWins,
            SerializerMode.Merge => ConflictStrategy.DestinationWins,
            _ => fallback
        };

    /// <summary>
    /// Conflict strategy for a document. A lifecycle in the header wins over its mode
    /// (overwrite = source-wins, fill and keep = destination-wins); without one the mode decides,
    /// and without either the pass strategy.
    /// </summary>
    public static ConflictStrategy StrategyFor(DocumentHeader? header, ConflictStrategy fallback) =>
        ParseOnExisting(header) switch
        {
            ExistingDocumentLifecycle.Overwrite => ConflictStrategy.SourceWins,
            ExistingDocumentLifecycle.Fill => ConflictStrategy.DestinationWins,
            ExistingDocumentLifecycle.Keep => ConflictStrategy.DestinationWins,
            _ => StrategyFor(ParseMode(header), fallback)
        };

    /// <summary>
    /// Reads and removes the <see cref="Key"/> entry from an untyped row document (SqlTable row
    /// files deserialize to a dictionary). Returns the header's mode, or null when absent. Only a
    /// mapping value is a header: a scalar under that key is a real column value and stays.
    /// </summary>
    public static SerializerMode? TakeFromRow(IDictionary<string, object?> row) => ParseMode(TakeHeaderFromRow(row));

    /// <summary>
    /// Reads and removes the whole <see cref="Key"/> entry from an untyped row document, mode and
    /// lifecycle. Null when the row carries no header mapping.
    /// </summary>
    public static DocumentHeader? TakeHeaderFromRow(IDictionary<string, object?> row)
    {
        if (!row.TryGetValue(Key, out var raw) || raw is not IDictionary map)
            return null;
        row.Remove(Key);

        string? mode = null, onExisting = null;
        foreach (DictionaryEntry kv in map)
        {
            var key = kv.Key?.ToString();
            if (string.Equals(key, "mode", StringComparison.OrdinalIgnoreCase))
                mode = kv.Value?.ToString();
            else if (string.Equals(key, OnExistingKey, StringComparison.OrdinalIgnoreCase))
                onExisting = kv.Value?.ToString();
        }
        return new DocumentHeader { Mode = mode, OnExisting = onExisting };
    }

    /// <summary>Stamps <paramref name="mode"/> onto an area and every page, grid row and paragraph below it.</summary>
    public static SerializedArea Stamp(SerializedArea area, SerializerMode mode) =>
        Stamp(area, OwnershipStampPolicy.ForMode(mode));

    /// <summary>
    /// Stamps the area and every page, grid row and paragraph below it with the header the
    /// <paramref name="policy"/> gives each document (content ownership R1 and R4): the predicate's
    /// mode and <c>onExisting</c> on pages, its <c>bodyOnExisting</c> on grid rows and paragraphs,
    /// and <c>ownershipOverrides</c> on single pages, rows and paragraphs. The most specific rule
    /// wins. A policy with no lifecycle keys writes exactly the mode-only headers of before.
    /// </summary>
    public static SerializedArea Stamp(SerializedArea area, OwnershipStampPolicy policy) =>
        area with
        {
            Ownership = For(policy.Mode, policy.OnExisting),
            Pages = area.Pages.Select(p => Stamp(p, policy)).ToList()
        };

    private static SerializedPage Stamp(SerializedPage page, OwnershipStampPolicy policy)
    {
        var pageHeader = policy.PageHeader(page);
        var bodyHeader = policy.BodyHeader(page, pageHeader);
        return page with
        {
            Ownership = pageHeader,
            GridRows = page.GridRows.Select(row =>
            {
                var rowHeader = policy.GridRowHeader(row, bodyHeader);
                return row with
                {
                    Ownership = rowHeader,
                    Columns = row.Columns.Select(col => col with
                    {
                        Paragraphs = col.Paragraphs.Select(p => p with { Ownership = policy.ParagraphHeader(p, rowHeader) }).ToList()
                    }).ToList()
                };
            }).ToList(),
            // Foundry #1315: page-level paragraphs get the same ownership stamp as the rest.
            Paragraphs = page.Paragraphs.Select(p => p with { Ownership = policy.ParagraphHeader(p, bodyHeader) }).ToList(),
            Children = page.Children.Select(c => Stamp(c, policy)).ToList()
        };
    }
}
