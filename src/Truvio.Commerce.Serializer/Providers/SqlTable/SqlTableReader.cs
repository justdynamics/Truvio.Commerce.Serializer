using System.Security.Cryptography;
using System.Text;
using Truvio.Commerce.Serializer.Models;
using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Reads all rows from a SQL table via ISqlExecutor and provides identity resolution
/// and checksum calculation following DW Deployment tool patterns.
/// </summary>
public class SqlTableReader
{
    private const string IdentitySeparator = "$$";
    private readonly ISqlExecutor _sqlExecutor;

    public SqlTableReader(ISqlExecutor sqlExecutor) => _sqlExecutor = sqlExecutor;

    /// <summary>
    /// Read all rows from the specified table, mapping DBNull to null.
    /// <para>
    /// Phase 37-03 (FILTER-01): when <paramref name="whereClause"/> is non-empty, the SELECT
    /// is composed as <c>SELECT * FROM [{table}] WHERE {whereClause}</c>. The whereClause
    /// MUST have already passed <see cref="Configuration.SqlWhereClauseValidator"/>; this
    /// method trusts its caller and composes the clause literally. Identifier values cannot
    /// be parameterized in T-SQL so validation is the only defense.
    /// </para>
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> ReadAllRows(string tableName, string? whereClause = null)
    {
        var cb = new CommandBuilder();
        if (string.IsNullOrWhiteSpace(whereClause))
            cb.Add($"SELECT * FROM [{tableName}]");
        else
            cb.Add($"SELECT * FROM [{tableName}] WHERE {whereClause}");

        using var reader = _sqlExecutor.ExecuteReader(cb);
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }

            yield return row;
        }
    }

    /// <summary>
    /// Engine issue #27 (serialize side): <c>PageUniqueId</c> for each of <paramref name="pageIds"/>
    /// that exists in <c>[Page]</c>. Ids with no page row are absent from the result.
    /// </summary>
    public virtual Dictionary<int, Guid> ReadPageUniqueIds(IReadOnlyCollection<int> pageIds)
    {
        var result = new Dictionary<int, Guid>();
        var ids = pageIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return result;

        var cb = new CommandBuilder();
        cb.Add("SELECT [PageID], [PageUniqueId] FROM [Page] WHERE [PageID] IN (");
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) cb.Add(",");
            cb.Add("{0}", ids[i]);
        }
        cb.Add(")");

        using var reader = _sqlExecutor.ExecuteReader(cb);
        while (reader.Read())
        {
            var id = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            var raw = reader.GetValue(1);
            if (raw is Guid g) result[id] = g;
            else if (Guid.TryParse(raw?.ToString(), out var parsed)) result[id] = parsed;
        }
        return result;
    }

    /// <summary>
    /// Engine issue #27 (deserialize side): this host's <c>PageID</c> for the page whose
    /// <c>PageUniqueId</c> is <paramref name="pageUniqueId"/>, or null when no such page exists.
    /// </summary>
    public virtual int? FindPageIdByUniqueId(Guid pageUniqueId)
    {
        if (pageUniqueId == Guid.Empty) return null;

        var cb = new CommandBuilder();
        cb.Add("SELECT TOP 1 [PageID] FROM [Page] WHERE [PageUniqueId] = {0}", pageUniqueId);

        using var reader = _sqlExecutor.ExecuteReader(cb);
        if (!reader.Read()) return null;
        var value = reader.GetValue(0);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Generate a row identity string following DW Deployment tool patterns (D-10/D-11).
    /// If NameColumn is set, use its value. Otherwise, use composite PK with $$ separator.
    /// Key columns are sorted alphabetically (OrdinalIgnoreCase).
    /// </summary>
    public string GenerateRowIdentity(Dictionary<string, object?> row, TableMetadata metadata)
    {
        if (!string.IsNullOrEmpty(metadata.NameColumn))
        {
            return row.TryGetValue(metadata.NameColumn, out var nameValue)
                ? nameValue?.ToString()?.Trim() ?? ""
                : "";
        }

        if (metadata.KeyColumns.Count > 0)
        {
            // Composite PK: sort key columns alphabetically, join values with $$
            var sortedKeys = metadata.KeyColumns
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            var parts = sortedKeys.Select(key =>
                row.TryGetValue(key, out var val) ? val?.ToString()?.Trim() ?? "" : "");

            return string.Join(IdentitySeparator, parts);
        }

        // Keyless table: use all column values as identity
        var allParts = metadata.AllColumns
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(col => row.TryGetValue(col, out var val) ? val?.ToString()?.Trim() ?? "" : "");

        return string.Join(IdentitySeparator, allParts);
    }

    /// <summary>
    /// Calculate MD5 checksum for change detection (D-15).
    /// Uses CompareColumns if specified, otherwise all columns except identity columns.
    /// </summary>
    public string CalculateChecksum(Dictionary<string, object?> row, TableMetadata metadata)
    {
        IEnumerable<string> columnsToUse;

        if (!string.IsNullOrEmpty(metadata.CompareColumns))
        {
            columnsToUse = metadata.CompareColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim());
        }
        else
        {
            var identitySet = new HashSet<string>(metadata.IdentityColumns, StringComparer.OrdinalIgnoreCase);
            columnsToUse = metadata.AllColumns.Where(c => !identitySet.Contains(c));
        }

        var sortedColumns = columnsToUse.OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        var first = true;
        foreach (var col in sortedColumns)
        {
            if (!first) sb.Append('|');
            first = false;
            var value = row.TryGetValue(col, out var v) ? NormalizeValue(v) : "";
            sb.Append(col.ToUpperInvariant());
            sb.Append('=');
            sb.Append(value);
        }

        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Normalize a value to a stable string for checksum comparison.
    /// Handles type differences between DB reads (C# bool True) and YAML reads (string "true").
    /// </summary>
    private static string NormalizeValue(object? v)
    {
        if (v is null) return "";
        if (v is bool b) return b ? "true" : "false";
        // Whitespace-only strings normalize to empty (matches DW Deployment tool)
        var s = v.ToString() ?? "";
        return string.IsNullOrWhiteSpace(s) ? "" : s;
    }
}
