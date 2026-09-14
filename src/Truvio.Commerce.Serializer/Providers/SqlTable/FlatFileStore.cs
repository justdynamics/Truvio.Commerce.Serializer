using System.Security.Cryptography;
using System.Text;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// Per-row YAML file I/O in _sql/{TableName}/ layout.
/// Uses a YAML serializer that preserves nulls for SQL NULL fidelity: a NULL column is written
/// as an empty plain scalar (<c>"Column": </c>) and reads back as null; an empty string is
/// written as <c>""</c> and reads back as "".
/// </summary>
public class FlatFileStore
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public FlatFileStore()
    {
        // SQL-specific serializer: preserves null values as empty scalars (NOT OmitNull like content YAML)
        // ForceStringScalarEmitter selects Literal block style for LF-only multiline strings
        // (pretty-printed XML from XmlFormatter uses LF-only, so this emits readable YAML blocks)
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithEventEmitter(next => new ForceStringScalarEmitter(next))
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        _deserializer = YamlConfiguration.BuildDeserializer();
    }

    /// <summary>
    /// Write a single row as a YAML file to _sql/{tableName}/{rowIdentity}.yml. When a non-null
    /// <paramref name="writtenFiles"/> list is supplied, the resolved absolute path is appended
    /// (Phase 37-01 Task 2 — fuels per-mode manifest cleanup). When <paramref name="mode"/> is
    /// set, the document starts with the <see cref="DocumentHeader"/> ownership header.
    /// </summary>
    public void WriteRow(string outputRoot, string tableName, string rowIdentity,
        Dictionary<string, object?> rowData, HashSet<string>? usedNames = null,
        List<string>? writtenFiles = null, Configuration.SerializerMode? mode = null)
    {
        var directory = Path.Combine(outputRoot, "_sql", tableName);
        Directory.CreateDirectory(directory);

        var sanitized = SanitizeFileName(rowIdentity);
        var fileName = DeduplicateFileName(sanitized, rowIdentity, usedNames);
        var filePath = Path.Combine(directory, fileName + ".yml");

        var document = rowData;
        if (mode is not null)
        {
            document = new Dictionary<string, object?>
            {
                [DocumentHeader.Key] = new Dictionary<string, object?> { ["mode"] = DocumentHeader.For(mode.Value).Mode }
            };
            foreach (var kv in rowData)
                document[kv.Key] = kv.Value;
        }

        var yaml = _serializer.Serialize(document);
        File.WriteAllText(filePath, yaml, Encoding.UTF8);

        writtenFiles?.Add(Path.GetFullPath(filePath));
    }

    /// <summary>
    /// Write table metadata as _meta.yml. Appends the path to <paramref name="writtenFiles"/>
    /// when supplied.
    /// </summary>
    public void WriteMeta(string outputRoot, string tableName, TableMetadata metadata,
        List<string>? writtenFiles = null)
    {
        var directory = Path.Combine(outputRoot, "_sql", tableName);
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, "_meta.yml");
        var yaml = _serializer.Serialize(metadata);
        File.WriteAllText(filePath, yaml, Encoding.UTF8);

        writtenFiles?.Add(Path.GetFullPath(filePath));
    }

    /// <summary>
    /// Read all row YAML files from _sql/{tableName}/, excluding _meta.yml. The ownership header
    /// is stripped from each row; use <see cref="ReadAllDocuments"/> to read it.
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> ReadAllRows(string inputRoot, string tableName) =>
        ReadAllDocuments(inputRoot, tableName).Select(d => d.Row);

    /// <summary>
    /// Read all row YAML files from _sql/{tableName}/, excluding _meta.yml, with each document's
    /// header mode (null when the file carries no <see cref="DocumentHeader"/>). The header key is
    /// removed from the returned row so it never reaches column matching or checksums.
    /// </summary>
    public IEnumerable<(Dictionary<string, object?> Row, Configuration.SerializerMode? Mode)> ReadAllDocuments(
        string inputRoot, string tableName)
    {
        var directory = Path.Combine(inputRoot, "_sql", tableName);
        if (!Directory.Exists(directory))
            yield break;

        var files = Directory.EnumerateFiles(directory, "*.yml")
            .Where(f => !Path.GetFileName(f).Equals("_meta.yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f);

        foreach (var file in files)
        {
            var yaml = File.ReadAllText(file, Encoding.UTF8);
            var row = _deserializer.Deserialize<Dictionary<string, object?>>(yaml);
            // Ensure case-insensitive lookup for column name matching with DB schema
            var caseInsensitive = new Dictionary<string, object?>(
                row ?? new Dictionary<string, object?>(),
                StringComparer.OrdinalIgnoreCase);
            var mode = DocumentHeader.TakeFromRow(caseInsensitive);
            yield return (caseInsensitive, mode);
        }
    }

    /// <summary>
    /// Read table metadata from _meta.yml.
    /// </summary>
    public TableMetadata ReadMeta(string inputRoot, string tableName)
    {
        var filePath = Path.Combine(inputRoot, "_sql", tableName, "_meta.yml");
        var yaml = File.ReadAllText(filePath, Encoding.UTF8);
        return _deserializer.Deserialize<TableMetadata>(yaml);
    }

    /// <summary>
    /// Sanitize a file name by replacing invalid characters with underscore.
    /// Follows FileSystemStore.SanitizeFolderName pattern.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "_unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(trimmed.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string DeduplicateFileName(string sanitized, string originalIdentity, HashSet<string>? usedNames)
    {
        if (usedNames == null)
            return sanitized;

        if (usedNames.Add(sanitized))
            return sanitized;

        // Phase 38 C.1 (D-38-10): monotonic-counter dedup. For N rows with
        // identical originalIdentity (e.g., N empty-name EcomProducts), emit
        // N distinct filenames instead of collapsing to 1. The previous
        // hash-of-identity approach silently dropped duplicates via file overwrite
        // (HashSet.Add returned false and was ignored).
        // Scope note: ORDER BY in SqlTableReader is OUT OF SCOPE for this fix
        // (deferred per RESEARCH Open Question 4 RESOLVED + checker warning W1).
        // This fix stays within DeduplicateFileName only.
        var hashPrefix = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(originalIdentity))).ToLowerInvariant()[..6];
        for (int n = 1; n < 100_000; n++)
        {
            var candidate = $"{sanitized} [{hashPrefix}-{n}]";
            if (usedNames.Add(candidate))
                return candidate;
        }
        throw new InvalidOperationException(
            $"Exhausted 100000 filename variants for identity '{originalIdentity}' — refuse to silently drop rows.");
    }
}
