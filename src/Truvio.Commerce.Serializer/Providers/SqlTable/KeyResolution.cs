using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Where a table's row-match key came from. Anything other than
/// <see cref="PrimaryKey"/> is an inferred key and carries a WARNING into the run log.
/// </summary>
public enum KeyResolutionSource
{
    /// <summary>The table's declared PRIMARY KEY (sp_pkeys). The unchanged, silent case.</summary>
    PrimaryKey,

    /// <summary>The entry's own <c>keyColumns</c> field. Explicit beats inference.</summary>
    DeclaredKeyColumns,

    /// <summary>A UNIQUE index or UNIQUE constraint read from sys.indexes.</summary>
    UniqueIndex,

    /// <summary>The full non-identity column tuple. The last resort, and an exact-row match.</summary>
    AllColumns
}

/// <summary>
/// One UNIQUE index or UNIQUE constraint on a table, as read from sys.indexes /
/// sys.index_columns. Key columns only, in key ordinal order.
/// </summary>
public sealed record UniqueIndexDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>
/// The columns a SqlTable write matches target rows on, and where they came from.
///
/// <para>
/// A table with no PRIMARY KEY (a heap) used to take a truncate-and-insert path in every
/// mode, so a Merge of one row deleted every target row the payload did not carry
/// (Foundry #1305). The engine now resolves a match key instead, and a heap goes through
/// the same MERGE upsert path a keyed table does. Nothing is deleted unless the entry asks
/// for it with <c>replaceStrategy: truncate</c>.
/// </para>
///
/// <para>Resolution order, first match wins:</para>
/// <list type="number">
/// <item>the declared PRIMARY KEY;</item>
/// <item>the entry's <c>keyColumns</c> field;</item>
/// <item>a single-column or composite UNIQUE index or constraint (not filtered, not
/// disabled, no nullable column);</item>
/// <item>the full column tuple, identity columns excluded.</item>
/// </list>
///
/// <para>
/// An identity (auto-increment) column is never used as a match key on its own: auto-ids
/// are environment-local and matching on one re-opens the LRN-hosted-publish-10 class that
/// <see cref="IdentityPkRelationTables"/> exists to close.
/// </para>
/// </summary>
public sealed record KeyResolution
{
    /// <summary>The columns target rows are matched on. Never empty.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Where <see cref="KeyColumns"/> came from.</summary>
    public required KeyResolutionSource Source { get; init; }

    /// <summary>Name of the UNIQUE index, when <see cref="Source"/> is <see cref="KeyResolutionSource.UniqueIndex"/>.</summary>
    public string? IndexName { get; init; }

    /// <summary>True when the key was inferred rather than declared by the table itself.</summary>
    public bool IsInferred => Source != KeyResolutionSource.PrimaryKey;

    /// <summary>Human-readable resolution, as it appears in the WARNING line and the entry warnings.</summary>
    public string Describe() => Source switch
    {
        KeyResolutionSource.PrimaryKey => "primary key",
        KeyResolutionSource.DeclaredKeyColumns => "keyColumns",
        KeyResolutionSource.UniqueIndex => $"unique index ({IndexName})",
        KeyResolutionSource.AllColumns => "all columns",
        _ => Source.ToString()
    };

    /// <summary>
    /// Resolve the match key for <paramref name="metadata"/>'s table.
    /// <paramref name="uniqueIndexes"/> is a lazy reader: it only runs when the table has no
    /// PRIMARY KEY and the entry declared no <c>keyColumns</c>, so a keyed table costs no
    /// extra round-trip.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A declared key column does not exist on the live table, or the table offers no column
    /// that can act as a key at all.
    /// </exception>
    /// <param name="payloadColumns">
    /// Columns the payload rows actually carry. The all-columns fallback narrows to these, so
    /// a tree serialized with <c>excludeFields</c> still matches its target rows instead of
    /// comparing a column the payload never carried. Ignored when null or empty.
    /// </param>
    public static KeyResolution Resolve(
        TableMetadata metadata,
        IReadOnlyList<string>? declaredKeyColumns,
        Func<IReadOnlyList<UniqueIndexDefinition>>? uniqueIndexes = null,
        IReadOnlyCollection<string>? payloadColumns = null)
    {
        if (metadata.KeyColumns.Count > 0)
        {
            return new KeyResolution
            {
                KeyColumns = metadata.KeyColumns.ToList(),
                Source = KeyResolutionSource.PrimaryKey
            };
        }

        if (declaredKeyColumns is { Count: > 0 })
        {
            var canonical = new List<string>();
            foreach (var declared in declaredKeyColumns)
            {
                var live = metadata.AllColumns
                    .FirstOrDefault(c => string.Equals(c, declared, StringComparison.OrdinalIgnoreCase));
                if (live is null)
                {
                    throw new InvalidOperationException(
                        $"[{metadata.TableName}] declares keyColumns entry '{declared}', which does not exist " +
                        "on the target table. Correct the entry's keyColumns or re-serialize against a target " +
                        "that carries the column.");
                }
                canonical.Add(live);
            }

            return new KeyResolution
            {
                KeyColumns = canonical,
                Source = KeyResolutionSource.DeclaredKeyColumns
            };
        }

        var candidates = uniqueIndexes?.Invoke() ?? Array.Empty<UniqueIndexDefinition>();
        var usable = candidates
            .Where(ix => ix.Columns.Count > 0)
            .Where(ix => ix.Columns.All(c => metadata.AllColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            // An index over nothing but identity columns is an auto-id key: environment-local,
            // so it is no safer than the truncate it replaces.
            .Where(ix => !ix.Columns.All(c => metadata.IdentityColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(ix => ix.Columns.Count)
            .ThenBy(ix => ix.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (usable is not null)
        {
            return new KeyResolution
            {
                KeyColumns = usable.Columns.ToList(),
                Source = KeyResolutionSource.UniqueIndex,
                IndexName = usable.Name
            };
        }

        var allColumnsKey = metadata.AllColumns
            .Where(c => !metadata.IdentityColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => payloadColumns is null
                        || payloadColumns.Count == 0
                        || payloadColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (allColumnsKey.Count == 0)
        {
            throw new InvalidOperationException(
                $"[{metadata.TableName}] has no primary key, no declared keyColumns, no usable unique index " +
                "and no non-identity column to match on. Declare keyColumns on the entry; refusing to write " +
                "(a truncate would delete target rows).");
        }

        return new KeyResolution
        {
            KeyColumns = allColumnsKey,
            Source = KeyResolutionSource.AllColumns
        };
    }
}
