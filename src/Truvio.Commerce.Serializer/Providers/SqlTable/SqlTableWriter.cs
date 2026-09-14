using System.Data;
using System.Text;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Outcome of writing a single row to the target SQL table.
/// </summary>
public enum WriteOutcome
{
    Created,
    Updated,
    Skipped,
    Failed
}

/// <summary>
/// Builds and executes MERGE upsert commands for SQL table deserialization.
/// Follows DW10 SqlDataItemWriter.BuildMergeCommand pattern (D-12/D-14)
/// with IDENTITY_INSERT handling and dry-run safety.
/// </summary>
public class SqlTableWriter
{
    private readonly ISqlExecutor _sqlExecutor;

    public SqlTableWriter(ISqlExecutor sqlExecutor) => _sqlExecutor = sqlExecutor;

    /// <summary>
    /// Build a parameterized MERGE command following the DW10 pattern exactly.
    /// Uses CommandBuilder {0} placeholders for SQL parameter safety.
    /// </summary>
    public CommandBuilder BuildMergeCommand(Dictionary<string, object?> row, TableMetadata metadata, HashSet<string>? notNullColumns = null)
    {
        var keyColumns = metadata.KeyColumns;
        var allColumns = metadata.AllColumns;

        // Determine which columns are present in the row data
        var itemColumns = allColumns
            .Where(col => row.ContainsKey(col))
            .ToList();

        // Identity insert required when identity column is also a key column
        var enableIdentityInsert = metadata.IdentityColumns
            .Any(ic => keyColumns.Contains(ic, StringComparer.OrdinalIgnoreCase));

        // Update columns: exclude key columns and identity columns (matching DW10 pattern)
        var updateColumns = itemColumns
            .Where(col => !keyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)
                       && !metadata.IdentityColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Insert columns: exclude identity columns unless identity insert is enabled
        var insertColumns = enableIdentityInsert
            ? itemColumns
            : itemColumns.Where(col => !metadata.IdentityColumns.Contains(col, StringComparer.OrdinalIgnoreCase)).ToList();

        // A NULL is never bound as a parameter (see CommandBuilderValues): null non-key columns
        // stay out of the source row and are written as the NULL literal in both branches. A
        // bare NULL cannot sit in the source SELECT either, because it types as int and int does
        // not convert to date, datetime2, time, uniqueidentifier or xml.
        var nullColumns = new HashSet<string>(
            itemColumns.Where(col => !keyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)
                                     && CommandBuilderValues.IsNull(row.TryGetValue(col, out var v) ? v : null)),
            StringComparer.OrdinalIgnoreCase);
        var sourceColumns = itemColumns.Where(col => !nullColumns.Contains(col)).ToList();

        var cb = new CommandBuilder();

        if (enableIdentityInsert)
        {
            cb.Add($"SET IDENTITY_INSERT [{metadata.TableName}] ON;");
        }

        cb.Add($"MERGE [{metadata.TableName}] AS target");
        cb.Add("USING (SELECT ");

        // Add parameterized values for each source column
        var count = 0;
        foreach (var column in sourceColumns)
        {
            if (count > 0)
            {
                cb.Add(",");
            }

            var value = row.TryGetValue(column, out var v) ? v : null;
            // Key columns are NOT NULL: a missing key value matches and inserts as empty string
            if (CommandBuilderValues.IsNull(value))
                value = "";
            cb.Add("{0}", value);
            count++;
        }

        cb.Add(") AS source (");
        cb.Add(string.Join(",", sourceColumns.Select(col => $"[{col}]")));
        cb.Add(")");

        // ON clause: match on key columns
        cb.Add("ON (");
        cb.Add(string.Join(" AND ", keyColumns.Select(col => $"target.[{col}] = source.[{col}]")));
        cb.Add(")");

        // WHEN MATCHED: update non-key, non-identity columns
        if (updateColumns.Count > 0)
        {
            cb.Add("WHEN MATCHED THEN UPDATE SET");
            cb.Add(string.Join(",", updateColumns.Select(col => nullColumns.Contains(col)
                ? $"[{col}] = NULL"
                : $"[{col}] = source.[{col}]")));
        }

        // WHEN NOT MATCHED: insert all eligible columns
        // NOT NULL columns get '' instead of NULL (ISNULL guard for a source value, literal for a null)
        cb.Add("WHEN NOT MATCHED THEN INSERT (");
        cb.Add(string.Join(",", insertColumns.Select(col => $"[{col}]")));
        cb.Add(")");
        cb.Add("VALUES(");
        cb.Add(string.Join(",", insertColumns.Select(col =>
        {
            var notNull = notNullColumns != null && notNullColumns.Contains(col);
            if (nullColumns.Contains(col))
                return notNull ? "''" : "NULL";
            return notNull ? $"ISNULL(source.[{col}], '')" : $"source.[{col}]";
        })));
        cb.Add(");");

        if (enableIdentityInsert)
        {
            cb.Add($"SET IDENTITY_INSERT [{metadata.TableName}] OFF;");
        }

        return cb;
    }

    /// <summary>
    /// Write a single row to the target table via MERGE upsert.
    /// In dry-run mode, checks existence but does NOT execute any SQL writes.
    /// </summary>
    public virtual WriteOutcome WriteRow(Dictionary<string, object?> row, TableMetadata metadata, bool isDryRun, Action<string>? log = null, HashSet<string>? notNullColumns = null)
    {
        try
        {
            // Check if row already exists to determine Created vs Updated
            var exists = RowExistsInTarget(metadata, row);

            if (isDryRun)
            {
                // Dry-run: report what would happen without executing MERGE
                return exists ? WriteOutcome.Updated : WriteOutcome.Created;
            }

            // Execute MERGE upsert
            var cb = BuildMergeCommand(row, metadata, notNullColumns);
            _sqlExecutor.ExecuteNonQuery(cb);

            return exists ? WriteOutcome.Updated : WriteOutcome.Created;
        }
        catch (Exception ex)
        {
            // Log the failing column values for debugging
            if (ex.Message.Contains("does not allow nulls"))
            {
                var nullCols = row.Where(kv => kv.Value is null || kv.Value == DBNull.Value)
                    .Select(kv => kv.Key).ToList();
                var missingCols = metadata.AllColumns.Where(c => !row.ContainsKey(c)).ToList();
                log?.Invoke($"    ERROR [{metadata.TableName}]: {ex.Message} | NullCols=[{string.Join(",", nullCols)}] MissingCols=[{string.Join(",", missingCols)}]");
            }
            else
            {
                log?.Invoke($"    ERROR [{metadata.TableName}]: {ex.Message}");
            }
            return WriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Issue a targeted UPDATE statement writing only the requested column subset,
    /// scoped by the key-column identity predicate. Used by <see cref="SqlTableProvider"/>'s
    /// Merge-mode fill branch after per-column <see cref="Infrastructure.MergePredicate"/>
    /// planning has decided which target columns are "unset" and therefore
    /// eligible to fill. UPDATE path — no IDENTITY_INSERT wrapping (irrelevant for UPDATE).
    /// </summary>
    /// <param name="tableName">
    /// Target table — identifier already whitelisted at config-load by Phase 37-03
    /// <c>SqlIdentifierValidator</c>; bracketed <c>[tableName]</c> emission keeps the
    /// same injection-safety guarantee as BuildMergeCommand (T-39-02-01 mitigated).
    /// </param>
    /// <param name="keyColumns">Key columns forming the WHERE identity predicate (AND-joined).</param>
    /// <param name="fullRow">
    /// Complete row dict — identity column values are read from here, plus every column
    /// listed in <paramref name="columnsToUpdate"/>. Values bind via CommandBuilder <c>{0}</c>
    /// placeholder (T-39-02-02 mitigated).
    /// </param>
    /// <param name="columnsToUpdate">
    /// The subset of columns to write (pre-filtered by caller per the merge predicate).
    /// Empty subset is a no-op and returns <see cref="WriteOutcome.Updated"/> to keep
    /// caller counter semantics consistent.
    /// </param>
    /// <param name="isDryRun">
    /// When true, logs the would-be SQL via <paramref name="log"/> and returns Updated
    /// without executing.
    /// </param>
    /// <param name="log">Optional log sink for the dry-run trace line or error message.</param>
    /// <remarks>
    /// Dry-run logs include actual values — see 39-CONTEXT.md D-19 / threat T-39-02-06.
    /// Not intended for log sinks shared with untrusted parties.
    /// </remarks>
    public virtual WriteOutcome UpdateColumnSubset(
        string tableName,
        IReadOnlyList<string> keyColumns,
        Dictionary<string, object?> fullRow,
        IEnumerable<string> columnsToUpdate,
        bool isDryRun,
        Action<string>? log = null)
    {
        var colList = columnsToUpdate.ToList();
        if (colList.Count == 0)
        {
            // Caller pre-filtered to empty — no-op. Return Updated so caller's counter
            // semantics stay consistent (merge planner already logged the "N filled" line).
            return WriteOutcome.Updated;
        }

        try
        {
            var cb = new CommandBuilder();
            cb.Add($"UPDATE [{tableName}] SET ");

            for (int i = 0; i < colList.Count; i++)
            {
                if (i > 0) cb.Add(",");
                var col = colList[i];
                CommandBuilderValues.AddValue(cb, $"[{col}]=", fullRow.TryGetValue(col, out var v) ? v : null);
            }

            cb.Add(" WHERE ");
            for (int i = 0; i < keyColumns.Count; i++)
            {
                if (i > 0) cb.Add(" AND ");
                var keyCol = keyColumns[i];
                cb.Add($"[{keyCol}]=");
                cb.Add("{0}", KeyValue(fullRow, keyCol));
            }

            if (isDryRun)
            {
                log?.Invoke(
                    $"    [DRY-RUN] UPDATE [{tableName}] SET " +
                    string.Join(",", colList.Select(c => $"[{c}]=?")) +
                    " WHERE " +
                    string.Join(" AND ", keyColumns.Select(k => $"[{k}]=?")));
                return WriteOutcome.Updated;
            }

            _sqlExecutor.ExecuteNonQuery(cb);
            return WriteOutcome.Updated;
        }
        catch (Exception ex)
        {
            log?.Invoke($"    ERROR [{tableName}].UpdateColumnSubset: {ex.Message}");
            return WriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Phase 37-05 / LINK-02 pass 2 (D-22): rewrite <c>Default.aspx?ID=N</c> and
    /// <c>"SelectedValue": "N"</c> references inside the listed string columns using the
    /// supplied resolver. Runs BEFORE <see cref="BuildMergeCommand"/> so the rewritten
    /// value flows through the existing parameterized MERGE — no SQL composition path
    /// sees the raw rewrite (T-37-05-03 mitigated by the parameterized-binding layer).
    /// No-op when <paramref name="resolver"/> is null or <paramref name="resolveInColumns"/>
    /// is empty/null. Non-string values, missing columns, and empty strings are all skipped.
    /// </summary>
    public void ApplyLinkResolution(
        Dictionary<string, object?> row,
        IEnumerable<string>? resolveInColumns,
        InternalLinkResolver? resolver)
    {
        if (resolver is null || resolveInColumns is null) return;

        foreach (var col in resolveInColumns)
        {
            if (!row.TryGetValue(col, out var existing)) continue;
            if (existing is not string s || s.Length == 0) continue;

            var rewritten = resolver.ResolveInStringColumn(s);
            if (!ReferenceEquals(rewritten, s) && rewritten != s)
                row[col] = rewritten;
        }
    }

    /// <summary>
    /// Check whether a row with the given key values exists in the target table.
    /// Used for both dry-run reporting and Created/Updated determination.
    /// </summary>
    /// <summary>
    /// Create a table from column definitions stored in serialized metadata.
    /// Used when the target database is missing a table that exists in the source.
    /// </summary>
    public void CreateTableFromMetadata(TableMetadata metadata)
    {
        if (metadata.ColumnDefinitions.Count == 0)
            throw new InvalidOperationException(
                $"Cannot create table [{metadata.TableName}]: no column definitions in metadata. " +
                "Re-serialize from the source to capture column schema.");

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{metadata.TableName}] (");

        for (int i = 0; i < metadata.ColumnDefinitions.Count; i++)
        {
            var col = metadata.ColumnDefinitions[i];
            sb.Append($"  [{col.Name}] {MapColumnType(col)}");
            if (col.IsIdentity)
                sb.Append(" IDENTITY(1,1)");
            sb.Append(col.IsNullable ? " NULL" : " NOT NULL");
            if (i < metadata.ColumnDefinitions.Count - 1)
                sb.Append(',');
            sb.AppendLine();
        }

        // Add primary key constraint
        if (metadata.KeyColumns.Count > 0)
        {
            sb.Append($"  ,CONSTRAINT [PK_{metadata.TableName}] PRIMARY KEY CLUSTERED (");
            sb.Append(string.Join(", ", metadata.KeyColumns.Select(k => $"[{k}]")));
            sb.AppendLine(")");
        }

        sb.AppendLine(")");

        var cb = new CommandBuilder();
        cb.Add(sb.ToString());
        _sqlExecutor.ExecuteNonQuery(cb);
    }

    private static string MapColumnType(ColumnDefinition col)
    {
        return col.DataType.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => col.MaxLength == -1
                ? $"{col.DataType}(max)"
                : $"{col.DataType}({col.MaxLength})",
            "varchar" or "char" => col.MaxLength == -1
                ? $"{col.DataType}(max)"
                : $"{col.DataType}({col.MaxLength})",
            "varbinary" => col.MaxLength == -1
                ? "varbinary(max)"
                : $"varbinary({col.MaxLength})",
            "decimal" or "numeric" => $"{col.DataType}({col.Precision},{col.Scale})",
            "float" => col.Precision > 0 ? $"float({col.Precision})" : "float",
            "datetime2" or "datetimeoffset" or "time" => col.Scale > 0
                ? $"{col.DataType}({col.Scale})"
                : col.DataType,
            _ => col.DataType
        };
    }

    /// <summary>
    /// For tables without primary keys: truncate the table and insert all rows.
    /// </summary>
    public void TruncateAndInsertAll(List<Dictionary<string, object?>> rows, TableMetadata metadata, Action<string>? log = null)
    {
        // Truncate existing data
        var truncateCb = new CommandBuilder();
        truncateCb.Add($"DELETE FROM [{metadata.TableName}]");
        _sqlExecutor.ExecuteNonQuery(truncateCb);

        // Check for identity column
        var hasIdentity = metadata.IdentityColumns.Count > 0;

        foreach (var row in rows)
        {
            var itemColumns = metadata.AllColumns
                .Where(col => row.ContainsKey(col))
                .ToList();

            // Exclude identity columns from INSERT unless we need to preserve IDs
            var insertColumns = hasIdentity ? itemColumns : itemColumns;

            var cb = new CommandBuilder();
            if (hasIdentity)
                cb.Add($"SET IDENTITY_INSERT [{metadata.TableName}] ON;");

            cb.Add($"INSERT INTO [{metadata.TableName}] (");
            cb.Add(string.Join(",", insertColumns.Select(col => $"[{col}]")));
            cb.Add(") VALUES (");

            for (int i = 0; i < insertColumns.Count; i++)
            {
                CommandBuilderValues.AddValue(cb, i > 0 ? "," : "",
                    row.TryGetValue(insertColumns[i], out var v) ? v : null);
            }

            cb.Add(")");

            if (hasIdentity)
                cb.Add($";SET IDENTITY_INSERT [{metadata.TableName}] OFF;");

            _sqlExecutor.ExecuteNonQuery(cb);
        }

        log?.Invoke($"  Truncate+insert: {rows.Count} rows inserted into [{metadata.TableName}]");
    }

    /// <summary>
    /// Disable all foreign key constraints on a table. Used during bulk deserialization
    /// to prevent FK ordering issues, then re-enabled after all tables are processed.
    /// </summary>
    public void DisableForeignKeys(string tableName)
    {
        var cb = new CommandBuilder();
        cb.Add($"ALTER TABLE [{tableName}] NOCHECK CONSTRAINT ALL");
        _sqlExecutor.ExecuteNonQuery(cb);
    }

    /// <summary>
    /// Re-enable all foreign key constraints on a table.
    /// </summary>
    public void EnableForeignKeys(string tableName)
    {
        var cb = new CommandBuilder();
        cb.Add($"ALTER TABLE [{tableName}] WITH CHECK CHECK CONSTRAINT ALL");
        _sqlExecutor.ExecuteNonQuery(cb);
    }

    public bool RowExistsInTarget(TableMetadata metadata, Dictionary<string, object?> row)
    {
        var cb = new CommandBuilder();
        cb.Add($"SELECT 1 FROM [{metadata.TableName}] WHERE ");

        for (int i = 0; i < metadata.KeyColumns.Count; i++)
        {
            if (i > 0) cb.Add(" AND ");
            var keyCol = metadata.KeyColumns[i];
            cb.Add($"[{keyCol}] = ");
            cb.Add("{0}", KeyValue(row, keyCol));
        }

        using var reader = _sqlExecutor.ExecuteReader(cb);
        return reader.Read();
    }

    /// <summary>
    /// Key value as MERGE matches it: a missing or null key is the empty string. Keys are NOT NULL,
    /// and a NULL key parameter would share the '' parameter anyway (see <see cref="CommandBuilderValues"/>).
    /// </summary>
    private static object KeyValue(Dictionary<string, object?> row, string keyColumn) =>
        row.TryGetValue(keyColumn, out var value) && !CommandBuilderValues.IsNull(value) ? value! : "";
}
