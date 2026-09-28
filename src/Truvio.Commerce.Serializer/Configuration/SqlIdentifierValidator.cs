using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// Validates SQL identifiers (table / column names) against INFORMATION_SCHEMA.
/// Identifiers cannot be parameterized in T-SQL (they're spliced as text), so the
/// only defense against "'; DROP TABLE X;--" as a table name is allowlisting.
/// Per SEED-002. One INFORMATION_SCHEMA query per table lifetime of the instance.
/// </summary>
public class SqlIdentifierValidator
{
    private readonly Dictionary<string, HashSet<string>> _tableColumns =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _tableNames;
    private readonly Func<HashSet<string>> _tableLoader;
    private readonly Func<string, HashSet<string>> _columnLoader;
    private readonly Dictionary<string, Dictionary<string, string>> _tableColumnTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _tablePrimaryKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, Dictionary<string, string>> _columnTypeLoader;
    private readonly Func<string, HashSet<string>>? _primaryKeyLoader;

    /// <summary>Production ctor — uses Database.CreateDataReader against INFORMATION_SCHEMA.</summary>
    public SqlIdentifierValidator()
    {
        _tableLoader = DefaultTableLoader;
        _columnLoader = DefaultColumnLoader;
        _columnTypeLoader = DefaultColumnTypeLoader;
        _primaryKeyLoader = DefaultPrimaryKeyLoader;
    }

    /// <summary>
    /// Test ctor: inject fixture loaders to exercise validation without a live DB. The column
    /// type and primary key loaders are optional: without them a column's SQL type is unknown
    /// (the numeric check of raiseOnlyColumns is skipped) and its primary key is unknown.
    /// </summary>
    public SqlIdentifierValidator(
        Func<HashSet<string>> tableLoader,
        Func<string, HashSet<string>> columnLoader,
        Func<string, Dictionary<string, string>>? columnTypeLoader = null,
        Func<string, HashSet<string>>? primaryKeyLoader = null)
    {
        _tableLoader = tableLoader;
        _columnLoader = columnLoader;
        _columnTypeLoader = columnTypeLoader
            ?? (_ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        _primaryKeyLoader = primaryKeyLoader;
    }

    /// <summary>
    /// Validate a table name exists in INFORMATION_SCHEMA.TABLES. Throws on mismatch.
    /// </summary>
    public void ValidateTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new InvalidOperationException("Empty table name is not a valid identifier.");

        EnsureTableNames();
        if (!_tableNames!.Contains(tableName))
            throw new InvalidOperationException(
                $"Table identifier not in INFORMATION_SCHEMA: '{tableName}'. " +
                "Check the 'table' value in your predicate config.");
    }

    /// <summary>
    /// Validate a column exists on the given table. Assumes <see cref="ValidateTable"/> was
    /// called first — if the table is not in the column cache it is loaded on demand.
    /// </summary>
    public void ValidateColumn(string tableName, string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            throw new InvalidOperationException("Empty column name is not a valid identifier.");

        var cols = GetColumns(tableName);
        if (!cols.Contains(columnName))
            throw new InvalidOperationException(
                $"Column identifier not in INFORMATION_SCHEMA: '[{tableName}].[{columnName}]'. " +
                "Check exclude/include/where fields in your predicate config.");
    }

    /// <summary>
    /// Returns the cached column set for a table. Loads from INFORMATION_SCHEMA on first call
    /// and reuses the cached set on subsequent calls.
    /// </summary>
    public HashSet<string> GetColumns(string tableName)
    {
        if (!_tableColumns.TryGetValue(tableName, out var cols))
        {
            cols = _columnLoader(tableName);
            _tableColumns[tableName] = cols;
        }
        return cols;
    }

    /// <summary>
    /// Returns the cached column name to SQL DATA_TYPE map for a table (for example
    /// <c>NumberCounter</c> to <c>int</c>). Loaded from INFORMATION_SCHEMA on first call.
    /// </summary>
    public Dictionary<string, string> GetColumnTypes(string tableName)
    {
        if (!_tableColumnTypes.TryGetValue(tableName, out var types))
        {
            types = new Dictionary<string, string>(_columnTypeLoader(tableName), StringComparer.OrdinalIgnoreCase);
            _tableColumnTypes[tableName] = types;
        }
        return types;
    }

    /// <summary>
    /// Returns the cached PRIMARY KEY column set for a table (empty for a heap), or <c>null</c>
    /// when this validator has no primary key source (a test fixture without one).
    /// </summary>
    public HashSet<string>? GetPrimaryKeyColumns(string tableName)
    {
        if (_primaryKeyLoader == null) return null;
        if (!_tablePrimaryKeys.TryGetValue(tableName, out var keys))
        {
            keys = new HashSet<string>(_primaryKeyLoader(tableName), StringComparer.OrdinalIgnoreCase);
            _tablePrimaryKeys[tableName] = keys;
        }
        return keys;
    }

    private void EnsureTableNames()
    {
        _tableNames ??= _tableLoader();
    }

    private static HashSet<string> DefaultTableLoader()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cb = new CommandBuilder();
        cb.Add("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'");
        using var reader = Database.CreateDataReader(cb);
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    private static HashSet<string> DefaultColumnLoader(string tableName)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cb = new CommandBuilder();
        // Parameterized via CommandBuilder {0} placeholder to block injection through tableName itself.
        cb.Add("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0}", tableName);
        using var reader = Database.CreateDataReader(cb);
        while (reader.Read()) cols.Add(reader.GetString(0));
        return cols;
    }

    private static Dictionary<string, string> DefaultColumnTypeLoader(string tableName)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cb = new CommandBuilder();
        cb.Add("SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0}", tableName);
        using var reader = Database.CreateDataReader(cb);
        while (reader.Read()) types[reader.GetString(0)] = reader.GetString(1);
        return types;
    }

    private static HashSet<string> DefaultPrimaryKeyLoader(string tableName)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cb = new CommandBuilder();
        cb.Add(
            "SELECT kcu.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " +
            "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " +
            "ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_NAME = kcu.TABLE_NAME " +
            "WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_NAME = {0}", tableName);
        using var reader = Database.CreateDataReader(cb);
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }
}
