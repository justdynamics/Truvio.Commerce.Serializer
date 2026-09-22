namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Engine issue #12: validate the <c>serviceCaches</c> of every manifest entry against
/// <see cref="DwCacheServiceRegistry"/> BEFORE any row is written.
///
/// <para><see cref="Configuration.ConfigLoader"/> gates the <c>serviceCaches</c> of the
/// predicates in <c>Serializer.config.json</c>, but a deserialize run reads its entries from a
/// composed <c>{mode}-manifest.json</c> that never passes through that loader. An unknown name
/// therefore survived to <c>InvalidateCaches</c>, i.e. after the entry's rows were already
/// written, where strict mode turned the invalidation warning into an entry failure — a failed
/// run with the write already done.</para>
///
/// <para>Run this at manifest read, before dispatch: an unknown name then fails the call with
/// zero rows written and the offending entry named.</para>
/// </summary>
public static class ManifestCacheValidation
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming every entry whose
    /// <c>serviceCaches</c> carries a name <see cref="DwCacheServiceRegistry"/> cannot resolve.
    /// No-op when every name resolves (and when no entry declares any).
    /// </summary>
    public static void ValidateServiceCaches(IEnumerable<ManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var errors = new List<string>();

        foreach (var entry in entries)
        {
            if (entry is not SqlTableEntry sql || sql.ServiceCaches.Count == 0) continue;

            foreach (var name in sql.ServiceCaches)
            {
                if (DwCacheServiceRegistry.Resolve(name) is null)
                    errors.Add($"entry '{entry.EntryId}' (table '{sql.Table}'): cache service '{name}' is not in DwCacheServiceRegistry.");
            }
        }

        if (errors.Count == 0) return;

        var supported = DwCacheServiceRegistry.AllSupportedNames;
        var previewCount = Math.Min(20, supported.Count);
        var preview = string.Join(", ", supported.Take(previewCount));
        var suffix = supported.Count > previewCount ? $" (+{supported.Count - previewCount} more)" : "";

        throw new InvalidOperationException(
            "Manifest is invalid — serviceCaches validation failed (nothing was written):\n  - " +
            string.Join("\n  - ", errors) +
            $"\nSupported ({supported.Count} total): {preview}{suffix}.\n" +
            "A table with no registry cache — the EcomProducts / variant family among " +
            "them — has no invalidation route from a deserialize run: drop the serviceCaches entry " +
            "and recycle the application pool after the run. See docs/sql-tables.md#servicecaches.");
    }
}
