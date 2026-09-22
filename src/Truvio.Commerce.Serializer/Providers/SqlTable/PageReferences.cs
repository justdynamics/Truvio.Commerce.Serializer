using System.Collections;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Engine issue #27: the host-independent binding for integer page-id columns in SqlTable rows.
/// <para>On serialize, every column listed in <c>resolveLinksInColumns</c> whose value is an
/// integer page id is recorded with the target page's <c>PageUniqueId</c> in a reserved
/// top-level mapping of the row document:</para>
/// <code>
/// EmailPageId: 5120
/// EmailUnsubscribePageId: 5121
/// pageRefs:
///   EmailPageId: 3f1c...-...
///   EmailUnsubscribePageId: 9ab2...-...
/// </code>
/// <para>On deserialize the block is removed from the row before any column handling and the
/// GUID resolves to this host's page id, which also covers layer pages that carry
/// <c>sourcePageId: 0</c> and are therefore absent from the source-to-target id map. A document
/// without the block (written before 1.0.4) still reads; its int columns resolve through the id
/// map alone. Like the <c>ownership</c> header, only a MAPPING under the key is the block: a scalar
/// under <c>pageRefs</c> is a real column value and stays.</para>
/// </summary>
public static class PageReferences
{
    /// <summary>YAML key of the block. camelCase-stable so the naming convention leaves it as is.</summary>
    public const string Key = "pageRefs";

    /// <summary>
    /// Reads and removes the <see cref="Key"/> block from a row document. Returns column -> GUID
    /// (case-insensitive); entries whose value is not a GUID are ignored. Empty when absent.
    /// </summary>
    public static IReadOnlyDictionary<string, Guid> TakeFromRow(IDictionary<string, object?> row)
    {
        var refs = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (!row.TryGetValue(Key, out var raw) || raw is not IDictionary map)
            return refs;
        row.Remove(Key);

        foreach (DictionaryEntry kv in map)
        {
            var column = kv.Key?.ToString();
            if (string.IsNullOrWhiteSpace(column)) continue;
            if (kv.Value is Guid g) refs[column] = g;
            else if (Guid.TryParse(kv.Value?.ToString(), out var parsed)) refs[column] = parsed;
        }
        return refs;
    }

    /// <summary>
    /// Integer page ids (&gt; 0) held by the listed columns of <paramref name="row"/>, keyed by column.
    /// </summary>
    public static Dictionary<string, int> IntegerPageIds(
        IReadOnlyDictionary<string, object?> row, IEnumerable<string> columns)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            if (!row.TryGetValue(col, out var value) || value is null) continue;
            int? id = value switch
            {
                int i => i,
                long l when l is > 0 and <= int.MaxValue => (int)l,
                short s => s,
                byte b => b,
                _ => null
            };
            if (id is > 0) ids[col] = id.Value;
        }
        return ids;
    }
}
