using System.Text.Json;
using System.Text.RegularExpressions;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// Reads <see cref="SerializerConfiguration"/> from JSON. Flat shape:
/// a single top-level <c>predicates</c> array where every entry carries its own <c>mode</c>
/// (Replace/Merge). Top-level <c>replace</c> / <c>merge</c> objects are HARD-REJECTED with a clear
/// actionable error. Top-level <c>predicates</c> entries missing the
/// <c>mode</c> field are likewise rejected. Per-predicate <c>mode</c> values must parse
/// case-insensitively to <see cref="SerializerMode"/> (i.e. "Replace" or "Merge").
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // T-37-01-02: OutputSubfolder path-traversal guard. Alphanumeric + underscore + dash, 1..32 chars.
    private static readonly Regex _safeSubfolder = new("^[a-zA-Z0-9_-]{1,32}$", RegexOptions.Compiled);

    /// <summary>
    /// Engine issue #16: every top-level key the loader actually reads. A key outside this set
    /// (and outside <see cref="_renamedTopLevelKeys"/>) is dropped by
    /// <see cref="JsonSerializerOptions"/> without a trace, so a config that names a setting
    /// the engine no longer has appears to work while the built-in default silently applies.
    /// <c>replace</c> / <c>merge</c> are listed because <see cref="Validate"/> rejects them with
    /// their own section-shape message — this gate must not pre-empt it.
    /// </summary>
    private static readonly HashSet<string> _knownTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "outputDirectory",
        "replaceOutputSubfolder",
        "mergeOutputSubfolder",
        "excludeFieldsByItemType",
        "excludeXmlElementsByType",
        "showMergeIndicators",
        "showReplaceIndicators",
        "predicates",
        "replace",
        "merge"
    };

    /// <summary>
    /// Engine issue #16: keys renamed in 0.9.0-beta. A config still carrying one is reading the
    /// built-in default, NOT the value it names, so these are a hard reject naming the new key —
    /// a warning would let a config that points somewhere else keep passing by coincidence.
    /// </summary>
    private static readonly Dictionary<string, string> _renamedTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deployOutputSubfolder"] = "replaceOutputSubfolder",
        ["seedOutputSubfolder"] = "mergeOutputSubfolder"
    };

    /// <summary>
    /// Test-only sink for loader warnings (unknown top-level key, missing OutputDirectory).
    /// Null routes to <see cref="Console.Error"/>, which is production behaviour.
    /// <see cref="AsyncLocal{T}"/> so parallel xUnit workers do not leak sinks between tests.
    /// </summary>
    internal static readonly AsyncLocal<Action<string>?> _testWarningSink = new();

    private static void Warn(string message)
    {
        var sink = _testWarningSink.Value;
        if (sink != null)
            sink(message);
        else
            Console.Error.WriteLine(message);
    }

    /// <summary>
    /// True when the candidate is a safe per-mode subfolder name (matches
    /// <c>[a-zA-Z0-9_-]{1,32}</c> — no path separators, no '..', no absolute paths).
    /// Save paths validate with this BEFORE writing so a bad name fails at save time with
    /// a screen-level error instead of poisoning every subsequent <see cref="Load(string)"/>.
    /// </summary>
    public static bool IsValidSubfolderName(string candidate) =>
        !string.IsNullOrEmpty(candidate) && _safeSubfolder.IsMatch(candidate);

    /// <summary>
    /// Test-only override of the default SqlIdentifierValidator used by the 1-arg
    /// <see cref="Load(string)"/> overload. When non-null, <see cref="Load(string)"/>
    /// delegates to the 2-arg overload with THIS validator; when null, it constructs
    /// a fresh <see cref="SqlIdentifierValidator"/> (which queries INFORMATION_SCHEMA
    /// via the live Dynamicweb DB connection).
    ///
    /// Uses <see cref="AsyncLocal{T}"/> so parallel xUnit test workers do not leak
    /// overrides between tests. Mirrors the pattern in
    /// <see cref="ConfigPathResolver.TestOverridePath"/>. NOT intended for production.
    /// </summary>
    private static readonly AsyncLocal<SqlIdentifierValidator?> _testOverrideIdentifierValidator = new();
    public static SqlIdentifierValidator? TestOverrideIdentifierValidator
    {
        get => _testOverrideIdentifierValidator.Value;
        set => _testOverrideIdentifierValidator.Value = value;
    }

    /// <summary>
    /// Test-only spy hook invoked by the 1-arg <see cref="Load(string)"/> overload
    /// when it constructs a DEFAULT <see cref="SqlIdentifierValidator"/> (i.e.
    /// <see cref="TestOverrideIdentifierValidator"/> was null). Exists so a structural
    /// test can prove the default validator was built without relying on catching a
    /// non-specific DB-layer exception.
    /// </summary>
    internal static readonly AsyncLocal<Action?> _testDefaultValidatorConstructedCallback = new();

    /// <summary>
    /// Load a serializer config with default identifier validation enabled. Phase 37-06
    /// gap closure: this overload constructs a default <see cref="SqlIdentifierValidator"/>
    /// (or uses <see cref="TestOverrideIdentifierValidator"/> when tests install one) and
    /// delegates to the 2-arg overload with a NON-NULL validator. All production call sites
    /// of <c>ConfigLoader.Load(path)</c> therefore receive the identifier-validation gate
    /// by default, closing Phase-37 SC-3.
    /// </summary>
    public static SerializerConfiguration Load(string filePath)
    {
        var overrideValidator = TestOverrideIdentifierValidator;
        SqlIdentifierValidator validator;
        if (overrideValidator != null)
        {
            validator = overrideValidator;
        }
        else
        {
            validator = new SqlIdentifierValidator();
            _testDefaultValidatorConstructedCallback.Value?.Invoke();
        }
        return Load(filePath, validator);
    }

    /// <summary>
    /// Load a serializer config. When <paramref name="identifierValidator"/> is non-null,
    /// every SqlTable predicate is checked: Table / NameColumn / ExcludeFields / IncludeFields /
    /// XmlColumns / ResolveLinksInColumns identifiers must exist in INFORMATION_SCHEMA, and any
    /// Where clause must pass <see cref="SqlWhereClauseValidator"/>. Errors across multiple
    /// predicates are aggregated and thrown as a single <see cref="InvalidOperationException"/>.
    ///
    /// Passing <c>null</c> explicitly SKIPS identifier validation — this path is intended
    /// for unit tests that exercise non-validation behavior. Production code should call
    /// the parameterless <see cref="Load(string)"/> overload.
    /// </summary>
    public static SerializerConfiguration Load(string filePath, SqlIdentifierValidator? identifierValidator)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Configuration file not found: '{filePath}'", filePath);

        var json = File.ReadAllText(filePath);

        // Engine issue #16: unknown top-level keys are silently dropped by STJ. Name them
        // BEFORE the typed read so a dead key is a diagnostic, not a coincidence.
        ValidateTopLevelKeys(json, filePath);

        var raw = JsonSerializer.Deserialize<RawSerializerConfiguration>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize configuration file — result was null.");

        Validate(raw);

        if (!Directory.Exists(raw.OutputDirectory))
        {
            Warn(
                $"[Serializer] Warning: OutputDirectory '{raw.OutputDirectory}' does not exist. " +
                "Serialization will create it; deserialization requires it to exist.");
        }

        var predicates = raw.Predicates?.Select(BuildPredicate).ToList() ?? new List<ProviderPredicateDefinition>();

        var config = new SerializerConfiguration
        {
            OutputDirectory = raw.OutputDirectory!,
            ReplaceOutputSubfolder = string.IsNullOrEmpty(raw.ReplaceOutputSubfolder) ? "replace" : raw.ReplaceOutputSubfolder!,
            MergeOutputSubfolder = string.IsNullOrEmpty(raw.MergeOutputSubfolder) ? "merge" : raw.MergeOutputSubfolder!,
            ExcludeFieldsByItemType = raw.ExcludeFieldsByItemType ?? new Dictionary<string, List<string>>(),
            ExcludeXmlElementsByType = raw.ExcludeXmlElementsByType ?? new Dictionary<string, List<string>>(),
            ShowMergeIndicators = raw.ShowMergeIndicators,
            ShowReplaceIndicators = raw.ShowReplaceIndicators ?? true,
            Predicates = predicates
        };

        if (identifierValidator != null)
            ValidateIdentifiers(config, identifierValidator, new SqlWhereClauseValidator());

        // Phase 37-04 / CACHE-01: every ServiceCaches entry must resolve against
        // DwCacheServiceRegistry. Unknown names would otherwise only surface mid-run.
        ValidateServiceCaches(config);

        return config;
    }

    /// <summary>
    /// Engine issue #16: name every top-level key the loader does not read.
    /// <list type="bullet">
    /// <item>A key renamed in 0.9.0-beta (<c>deployOutputSubfolder</c>, <c>seedOutputSubfolder</c>)
    /// is a hard reject naming its replacement: the config names an output subfolder the engine
    /// never reads, and the default silently applies instead.</item>
    /// <item>Any other unrecognised key produces a warning naming the key.</item>
    /// <item>Keys starting with <c>_</c> are the file-comment convention
    /// (<c>ecommerce-predicates-example.json</c> ships <c>_comment</c>) and are ignored.</item>
    /// </list>
    /// </summary>
    internal static void ValidateTopLevelKeys(string json, string filePath)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        var renamed = new List<string>();
        var unknown = new List<string>();

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var key = property.Name;
            if (key.StartsWith('_')) continue;
            if (_knownTopLevelKeys.Contains(key)) continue;

            if (_renamedTopLevelKeys.TryGetValue(key, out var replacement))
                renamed.Add($"'{key}' was renamed to '{replacement}' in 0.9.0-beta and is no longer read.");
            else
                unknown.Add(key);
        }

        foreach (var key in unknown)
        {
            Warn(
                $"[Serializer] Warning: unknown top-level configuration key '{key}' in '{filePath}'. " +
                "It is not read by the serializer, so its value has no effect. " +
                $"Known keys: {string.Join(", ", _knownTopLevelKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}.");
        }

        if (renamed.Count > 0)
        {
            throw new InvalidOperationException(
                "Configuration is invalid — dead top-level key(s):\n  - " +
                string.Join("\n  - ", renamed) +
                "\nRename the key(s); leaving them in place reads the built-in default " +
                "('replace' / 'merge') instead of the value the config names.");
        }
    }

    /// <summary>
    /// Phase 37-04: resolve every <c>serviceCaches</c> entry against
    /// <see cref="DwCacheServiceRegistry"/>. Errors accumulate and throw as a
    /// single aggregated <see cref="InvalidOperationException"/>.
    /// </summary>
    private static void ValidateServiceCaches(SerializerConfiguration config)
    {
        var errors = new List<string>();

        foreach (var p in config.Predicates)
        {
            if (p.ServiceCaches.Count == 0) continue;
            foreach (var name in p.ServiceCaches)
            {
                if (DwCacheServiceRegistry.Resolve(name) is null)
                    errors.Add($"predicates '{p.Name}': cache service '{name}' is not in DwCacheServiceRegistry.");
            }
        }

        if (errors.Count == 0) return;

        var supported = DwCacheServiceRegistry.AllSupportedNames;
        var previewCount = Math.Min(20, supported.Count);
        var preview = string.Join(", ", supported.Take(previewCount));
        var suffix = supported.Count > previewCount
            ? $" (+{supported.Count - previewCount} more)"
            : "";

        throw new InvalidOperationException(
            "Configuration is invalid — ServiceCaches validation failed:\n  - " +
            string.Join("\n  - ", errors) +
            $"\nSupported ({supported.Count} total): {preview}{suffix}.\n" +
            "See DwCacheServiceRegistry.cs — add new entries by PR.");
    }

    /// <summary>
    /// Phase 37-03: validate every SqlTable predicate identifier (table, columns in Exclude/
    /// Include/Xml/NameColumn, and Where-clause references) against INFORMATION_SCHEMA via
    /// the provided validator. Errors accumulate; a single aggregated exception is thrown at
    /// the end if any predicate failed.
    /// </summary>
    private static void ValidateIdentifiers(
        SerializerConfiguration config,
        SqlIdentifierValidator idValidator,
        SqlWhereClauseValidator whereValidator)
    {
        var errors = new List<string>();

        foreach (var p in config.Predicates)
            CollectIdentifierErrors(p, idValidator, whereValidator, "predicates", errors);

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Configuration is invalid — identifier / WHERE-clause validation failed:\n  - " +
                string.Join("\n  - ", errors));
    }

    /// <summary>
    /// Checks one SqlTable predicate's identifiers (table, NameColumn, Exclude/Include/Xml/
    /// ResolveLinks columns) and its WHERE clause, appending one message per failure to
    /// <paramref name="errors"/>. Non-SqlTable predicates are ignored. Shared by config load and
    /// the inline API scope (<see cref="InlineScopeResolver"/>), so both pass the same gate.
    /// </summary>
    internal static void CollectIdentifierErrors(
        ProviderPredicateDefinition p,
        SqlIdentifierValidator idValidator,
        SqlWhereClauseValidator whereValidator,
        string scope,
        List<string> errors)
    {
        if (!string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(p.Table))
        {
            errors.Add($"{scope}: SqlTable predicate '{p.Name}' is missing 'table'.");
            return;
        }

        // 1. Table identifier.
        try { idValidator.ValidateTable(p.Table!); }
        catch (InvalidOperationException ex)
        {
            errors.Add($"{scope} '{p.Name}': {ex.Message}");
            return;
        }

        // 2. Column-level identifiers — NameColumn, each ExcludeFields/IncludeFields/XmlColumns entry.
        var columns = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.NameColumn))
            columns.Add(p.NameColumn!);
        columns.AddRange(p.ExcludeFields);
        columns.AddRange(p.IncludeFields);
        columns.AddRange(p.XmlColumns);
        columns.AddRange(p.ResolveLinksInColumns);
        foreach (var col in columns)
        {
            try { idValidator.ValidateColumn(p.Table!, col); }
            catch (InvalidOperationException ex) { errors.Add($"{scope} '{p.Name}': {ex.Message}"); }
        }

        // 3. WHERE clause — must parse + every identifier must be an existing column.
        if (!string.IsNullOrWhiteSpace(p.Where))
        {
            try
            {
                var cols = idValidator.GetColumns(p.Table!);
                whereValidator.Validate(p.Where!, cols);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{scope} '{p.Name}': {ex.Message}");
            }
        }
    }

    private static void Validate(RawSerializerConfiguration raw)
    {
        if (string.IsNullOrWhiteSpace(raw.OutputDirectory))
            throw new InvalidOperationException("Configuration is invalid: 'outputDirectory' is required and must not be empty.");

        // A section-level shape (top-level 'replace' or 'merge' object) is hard-rejected.
        // Configs use the flat shape: a single top-level 'predicates' array where each entry
        // carries its own 'mode' field. See docs/baselines/Swift2.2-baseline.md.
        if (raw.Replace != null)
            throw new InvalidOperationException(
                "Configuration is invalid: Section-level shape detected — a top-level 'replace' object is not supported. " +
                "Use the flat shape: put every predicate in the top-level 'predicates' array and add \"mode\": \"Replace\" to each. " +
                "See docs/baselines/Swift2.2-baseline.md for the shape.");
        if (raw.Merge != null)
            throw new InvalidOperationException(
                "Configuration is invalid: Section-level shape detected — a top-level 'merge' object is not supported. " +
                "Use the flat shape: put every predicate in the top-level 'predicates' array and add \"mode\": \"Merge\" to each. " +
                "See docs/baselines/Swift2.2-baseline.md for the shape.");

        if (raw.Predicates != null)
            ValidatePredicates(raw.Predicates, "predicates");

        ValidateSubfolder(raw.ReplaceOutputSubfolder, "replaceOutputSubfolder");
        ValidateSubfolder(raw.MergeOutputSubfolder, "mergeOutputSubfolder");
    }

    private static void ValidatePredicates(List<RawPredicateDefinition> predicates, string scope)
    {
        for (var i = 0; i < predicates.Count; i++)
        {
            var p = predicates[i];
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new InvalidOperationException($"Configuration is invalid: {scope}[{i}] is missing required field 'name'.");

            // Every predicate must declare its mode.
            if (string.IsNullOrWhiteSpace(p.Mode))
                throw new InvalidOperationException(
                    $"Configuration is invalid: {scope}[{i}] (name='{p.Name}') is missing required field 'mode' " +
                    "(expected 'Replace' or 'Merge', case-insensitive).");
            if (!Enum.TryParse<SerializerMode>(p.Mode, ignoreCase: true, out _))
                throw new InvalidOperationException(
                    $"Unknown mode '{p.Mode}' for predicate '{p.Name}' — valid values: Replace, Merge.");

            var isContentPredicate = string.IsNullOrEmpty(p.ProviderType)
                || string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase);
            if (isContentPredicate)
            {
                if (string.IsNullOrWhiteSpace(p.Path))
                    throw new InvalidOperationException($"Configuration is invalid: {scope}[{i}] is missing required field 'path'.");
                if (p.AreaId <= 0)
                    throw new InvalidOperationException($"Configuration is invalid: {scope}[{i}] is missing required field 'areaId' (must be > 0).");
            }
        }
    }

    private static void ValidateSubfolder(string? candidate, string scope)
    {
        if (string.IsNullOrEmpty(candidate)) return;
        if (!_safeSubfolder.IsMatch(candidate))
        {
            throw new InvalidOperationException(
                $"Configuration is invalid: {scope} '{candidate}' must match [a-zA-Z0-9_-]{{1,32}} " +
                "(no path separators, no '..', no absolute paths).");
        }
    }

    private static ProviderPredicateDefinition BuildPredicate(RawPredicateDefinition raw)
    {
        // Mode parse is safe — ValidatePredicates already verified the value parses.
        Enum.TryParse<SerializerMode>(raw.Mode, ignoreCase: true, out var mode);
        return new ProviderPredicateDefinition
        {
            Name = raw.Name!,
            Mode = mode,
            ProviderType = string.IsNullOrEmpty(raw.ProviderType) ? "Content" : raw.ProviderType,
            Path = raw.Path ?? "",
            AreaId = raw.AreaId,
            PageId = raw.PageId,
            Excludes = raw.Excludes ?? new List<string>(),
            Table = raw.Table,
            NameColumn = raw.NameColumn,
            CompareColumns = raw.CompareColumns,
            ServiceCaches = raw.ServiceCaches ?? new List<string>(),
            SchemaSync = raw.SchemaSync,
            XmlColumns = raw.XmlColumns ?? new List<string>(),
            ExcludeFields = raw.ExcludeFields ?? new List<string>(),
            ExcludeXmlElements = raw.ExcludeXmlElements ?? new List<string>(),
            ExcludeAreaColumns = raw.ExcludeAreaColumns ?? new List<string>(),
            Where = string.IsNullOrWhiteSpace(raw.Where) ? null : raw.Where,
            IncludeFields = raw.IncludeFields ?? new List<string>(),
            ResolveLinksInColumns = raw.ResolveLinksInColumns ?? new List<string>(),
            AcknowledgedOrphanPageIds = raw.AcknowledgedOrphanPageIds ?? new List<int>(),
            IncludeLanguageLayers = raw.IncludeLanguageLayers
        };
    }

    // -------------------------------------------------------------------------
    // Raw DTOs for deserialization — nullable everywhere so we can produce clear
    // validation errors rather than generic JSON ones.
    // -------------------------------------------------------------------------

    private sealed class RawSerializerConfiguration
    {
        public string? OutputDirectory { get; set; }

        // Top-level subfolder names + flat exclusion dictionaries.
        public string? ReplaceOutputSubfolder { get; set; }
        public string? MergeOutputSubfolder { get; set; }
        public Dictionary<string, List<string>>? ExcludeFieldsByItemType { get; set; }
        public Dictionary<string, List<string>>? ExcludeXmlElementsByType { get; set; }

        /// <summary>Admin UI: show merge indicators (tree flower icons + merge message on edit
        /// screens). Off by default — broad merge coverage turns every node green and drowns
        /// the replace icons, which are the ones that signal "your edit will be overwritten".</summary>
        public bool ShowMergeIndicators { get; set; }

        /// <summary>Admin UI: show replace indicators (tree sync icons + replace warnings on
        /// content and commerce edit screens). Nullable so an absent key defaults to ON.</summary>
        public bool? ShowReplaceIndicators { get; set; }

        // SINGLE flat predicate list with per-entry Mode.
        public List<RawPredicateDefinition>? Predicates { get; set; }

        // Detection-only fields. If either is non-null after deserialize the section shape
        // was used and Validate() throws. They are NEVER read for content. Using `object?`
        // makes them match any JSON shape (object, array, primitive) without us caring about
        // contents — Validate() throws on any non-null value.
        public object? Replace { get; set; }
        public object? Merge { get; set; }
    }

    private sealed class RawPredicateDefinition
    {
        public string? Name { get; set; }
        public string? ProviderType { get; set; }

        /// <summary>Required per-predicate mode ("Replace" or "Merge", case-insensitive).</summary>
        public string? Mode { get; set; }

        public string? Path { get; set; }
        public int AreaId { get; set; }
        public int PageId { get; set; }
        public List<string>? Excludes { get; set; }
        public string? Table { get; set; }
        public string? NameColumn { get; set; }
        public string? CompareColumns { get; set; }
        public List<string>? ServiceCaches { get; set; }
        public string? SchemaSync { get; set; }
        public List<string>? XmlColumns { get; set; }
        public List<string>? ExcludeFields { get; set; }
        public List<string>? ExcludeXmlElements { get; set; }
        public List<string>? ExcludeAreaColumns { get; set; }
        public string? Where { get; set; }
        public List<string>? IncludeFields { get; set; }
        public List<string>? ResolveLinksInColumns { get; set; }
        public List<int>? AcknowledgedOrphanPageIds { get; set; }
        public bool IncludeLanguageLayers { get; set; }
    }
}
