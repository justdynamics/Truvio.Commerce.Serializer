using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.Providers.SqlTable;

/// <summary>
/// ISerializationProvider implementation for SQL tables.
/// Reads DataGroup XML metadata, reads SQL table rows, resolves row identity,
/// and writes per-row YAML files to _sql/{TableName}/.
/// Supports full round-trip: Serialize (DB to YAML) and Deserialize (YAML to DB via MERGE).
/// </summary>
public class SqlTableProvider : SerializationProviderBase
{
    private readonly DataGroupMetadataReader _metadataReader;
    private readonly SqlTableReader _tableReader;
    private readonly FlatFileStore _fileStore;
    private readonly SqlTableWriter _writer;
    private readonly TargetSchemaCache _schemaCache;

    public override string ProviderType => "SqlTable";
    public override string DisplayName => "SQL Table Provider";

    /// <summary>
    /// Creates the provider. <paramref name="schemaCache"/> is the Phase 37-02 unified target
    /// schema / type coercion cache; defaults to a fresh instance backed by the live
    /// INFORMATION_SCHEMA loader. Pass a shared instance to coalesce schema queries across
    /// providers within the same deserialize run.
    /// </summary>
    public SqlTableProvider(
        DataGroupMetadataReader metadataReader,
        SqlTableReader tableReader,
        FlatFileStore fileStore,
        SqlTableWriter writer,
        TargetSchemaCache? schemaCache = null)
    {
        _metadataReader = metadataReader;
        _tableReader = tableReader;
        _fileStore = fileStore;
        _writer = writer;
        _schemaCache = schemaCache ?? new TargetSchemaCache();
    }

    public override SerializeResult Serialize(
        ProviderPredicateDefinition predicate,
        string outputRoot,
        Action<string>? log = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // SqlTable doesn't use the by-type dicts today — row-level field exclusions are
        // configured per-predicate via excludeFields / includeFields / excludeXmlElements.
        // Accept the parameters to satisfy the base contract; a future extension could apply
        // them against each row's XML column blobs.
        _ = excludeFieldsByItemType;
        _ = excludeXmlElementsByType;

        var validation = ValidatePredicate(predicate);
        if (!validation.IsValid)
        {
            return new SerializeResult
            {
                Errors = validation.Errors
            };
        }

        var metadata = _metadataReader.GetTableMetadata(predicate, includeColumnDefinitions: true);
        Log($"Serializing table {metadata.TableName}", log);

        // Phase 37-03 (FILTER-01): forward predicate.Where to the reader. The clause is
        // pre-validated at config-load / admin-UI save; the reader composes it literally.
        var rows = _tableReader.ReadAllRows(metadata.TableName, predicate.Where).ToList();
        Log($"Read {rows.Count} rows from {metadata.TableName}", log);

        var writtenFiles = new List<string>();
        _fileStore.WriteMeta(outputRoot, metadata.TableName,
            metadata with { Ownership = DocumentHeader.For(predicate.Mode) }, writtenFiles);

        var xmlColumns = new HashSet<string>(predicate.XmlColumns, StringComparer.OrdinalIgnoreCase);

        // Phase 37-03 (RUNTIME-COLS-01): runtime-only columns (e.g. UrlPathVisitsCount,
        // EcomShops.ShopIndex*) are auto-excluded unless the predicate opts in via IncludeFields.
        var autoExcluded = RuntimeExcludes.GetAutoExcludedColumns(metadata.TableName)
            .Except(predicate.IncludeFields, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (autoExcluded.Count > 0)
        {
            Log(
                $"Auto-excluding {autoExcluded.Count} runtime-only column(s) for [{metadata.TableName}]: " +
                string.Join(", ", autoExcluded),
                log);
        }

        var effectiveExcludes = new HashSet<string>(
            predicate.ExcludeFields.Concat(autoExcluded),
            StringComparer.OrdinalIgnoreCase);
        var excludeFields = effectiveExcludes.Count > 0 ? effectiveExcludes : null;

        // Engine issue #27: integer page-id columns listed in resolveLinksInColumns carry the
        // target page's PageUniqueId alongside the id (the row's pageRefs block), so the row can
        // bind to the page on any host — including pages with no source page id in the id map.
        var pageIdGuids = ReadPageIdGuids(rows, predicate.ResolveLinksInColumns, metadata.TableName, log);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Step 1: Pretty-print XML columns
            if (xmlColumns.Count > 0)
            {
                foreach (var col in xmlColumns)
                {
                    if (row.TryGetValue(col, out var val) && val is string strVal)
                    {
                        row[col] = XmlFormatter.PrettyPrint(strVal);
                    }
                }
            }

            // Step 2: Strip excluded XML elements from XML columns
            if (predicate.ExcludeXmlElements.Count > 0 && xmlColumns.Count > 0)
            {
                foreach (var col in xmlColumns)
                {
                    if (row.TryGetValue(col, out var val) && val is string strVal)
                    {
                        row[col] = XmlFormatter.RemoveElements(strVal, predicate.ExcludeXmlElements);
                    }
                }
            }

            // Step 3: Remove excluded columns from row
            if (excludeFields != null)
            {
                foreach (var field in excludeFields)
                    row.Remove(field);
            }

            // Step 4 (engine issue #27): record PageUniqueId for integer page-id columns.
            if (pageIdGuids is not null)
                AddPageReferences(row, predicate.ResolveLinksInColumns, pageIdGuids, metadata.TableName, log);

            var identity = _tableReader.GenerateRowIdentity(row, metadata);
            _fileStore.WriteRow(outputRoot, metadata.TableName, identity, row, usedNames, writtenFiles, predicate.Mode);
        }

        Log($"Serialized {rows.Count} rows to _sql/{metadata.TableName}/", log);

        return new SerializeResult
        {
            RowsSerialized = rows.Count,
            TableName = metadata.TableName,
            WrittenFiles = writtenFiles,
            Entry = BuildManifestEntry(predicate, outputRoot, writtenFiles)
        };
    }

    /// <summary>
    /// Engine issue #27: PageUniqueId for every integer page id held by a
    /// <paramref name="columns"/> column across <paramref name="rows"/>. Null when no listed
    /// column holds an integer page id (string-only link columns, or none listed).
    /// </summary>
    private Dictionary<int, Guid>? ReadPageIdGuids(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> columns,
        string tableName,
        Action<string>? log)
    {
        if (columns.Count == 0) return null;
        var ids = rows
            .SelectMany(r => PageReferences.IntegerPageIds(r, columns).Values)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return null;

        try
        {
            return _tableReader.ReadPageUniqueIds(ids);
        }
        catch (Exception ex)
        {
            Log($"WARNING: [{tableName}] could not read PageUniqueId for {ids.Count} page id(s) " +
                $"in resolveLinksInColumns: {ex.Message}. Rows are written without a pageRefs block.", log);
            return new Dictionary<int, Guid>();
        }
    }

    private static void AddPageReferences(
        Dictionary<string, object?> row,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<int, Guid> pageIdGuids,
        string tableName,
        Action<string>? log)
    {
        if (row.ContainsKey(PageReferences.Key)) return;   // a real column of that name wins

        var refs = new Dictionary<string, object?>();
        foreach (var (column, pageId) in PageReferences.IntegerPageIds(row, columns))
        {
            if (pageIdGuids.TryGetValue(pageId, out var guid))
                refs[column] = guid.ToString();
            else
                Log($"  [{tableName}].[{column}] holds page ID {pageId}, which has no [Page] row on the " +
                    "source; no PageUniqueId recorded — it resolves through the page id map only.", log);
        }
        if (refs.Count > 0)
            row[PageReferences.Key] = refs;
    }

    /// <summary>
    /// Phase 42-03 / PROVIDER-03: build a <see cref="SqlTableEntry"/> from the predicate that
    /// drove the run. EntryId pattern: <c>"sql/{Table}"</c>. Carries every deserialize-affecting
    /// SqlTable post-processing field (ServiceCaches, SchemaSync, ResolveLinksInColumns, XmlColumns)
    /// — defends pitfall #2 silent-skip class. The 8-field round-trip property test in Plan 04
    /// asserts no field is forgotten.
    /// </summary>
    public override ManifestEntry BuildManifestEntry(
        ProviderPredicateDefinition predicate,
        string modeRoot,
        IReadOnlyList<string> writtenFiles)
    {
        return new SqlTableEntry
        {
            EntryId = $"sql/{predicate.Table}",
            Files = writtenFiles
                .Select(f => Path.GetRelativePath(modeRoot, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Table = predicate.Table!,
            NameColumn = predicate.NameColumn,
            KeyColumns = predicate.KeyColumns.ToList(),
            ReplaceStrategy = predicate.ReplaceStrategy,
            CompareColumns = predicate.CompareColumns,
            XmlColumns = predicate.XmlColumns.ToList(),
            ResolveLinksInColumns = predicate.ResolveLinksInColumns.ToList(),
            ServiceCaches = predicate.ServiceCaches.ToList(),
            SchemaSync = predicate.SchemaSync
        };
    }

    public override ProviderDeserializeResult Deserialize(
        ManifestEntry entry,
        string inputRoot,
        Action<string>? log = null,
        bool isDryRun = false,
        ConflictStrategy strategy = ConflictStrategy.SourceWins,
        InternalLinkResolver? linkResolver = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        _ = excludeFieldsByItemType;
        _ = excludeXmlElementsByType;

        // Phase 43 / DESER-03: downcast at the entry-point. Validation moves to manifest
        // read time (Phase 42 ManifestSchema strict-read + ManifestEntry/SqlTableEntry
        // required-modifier on Table); this defensive downcast guards against a
        // misregistered provider being asked to dispatch the wrong entry shape.
        if (entry is not SqlTableEntry sqlEntry)
        {
            return new ProviderDeserializeResult
            {
                Errors = new[] { $"Expected SqlTableEntry, got {entry.GetType().Name}" }
            };
        }

        var tableName = sqlEntry.Table;

        // Phase 43 / DESER-03: synthesise a transient predicate carrying the deserialize-affecting
        // SqlTableEntry fields so existing predicate-typed helpers (DataGroupMetadataReader.
        // GetTableMetadata) keep working. The predicate never escapes this method.
        var syntheticPredicate = new ProviderPredicateDefinition
        {
            Name = sqlEntry.EntryId,
            ProviderType = "SqlTable",
            Table = sqlEntry.Table,
            NameColumn = sqlEntry.NameColumn,
            KeyColumns = sqlEntry.KeyColumns.ToList(),
            ReplaceStrategy = sqlEntry.ReplaceStrategy,
            CompareColumns = sqlEntry.CompareColumns,
            XmlColumns = sqlEntry.XmlColumns.ToList(),
            ResolveLinksInColumns = sqlEntry.ResolveLinksInColumns.ToList(),
            ServiceCaches = sqlEntry.ServiceCaches.ToList(),
            SchemaSync = sqlEntry.SchemaSync
        };

        // If table doesn't exist in target, create it from serialized metadata
        if (!_metadataReader.TableExists(tableName))
        {
            Log($"Table [{tableName}] does not exist in target — creating from serialized schema", log);

            if (!isDryRun)
            {
                try
                {
                    var serializedMeta = _fileStore.ReadMeta(inputRoot, tableName);
                    _writer.CreateTableFromMetadata(serializedMeta);
                    Log($"Created table [{tableName}]", log);
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Failed to create table [{tableName}]: {ex.Message}", log);
                    return new ProviderDeserializeResult
                    {
                        TableName = tableName,
                        Errors = [$"Failed to create table [{tableName}]: {ex.Message}"]
                    };
                }
            }
        }

        var metadata = _metadataReader.GetTableMetadata(syntheticPredicate);
        // Engine issue #20: hand the entry's files[] to the store so a document in the directory
        // that no manifest entry names is reported rather than applied unnoticed, and the read
        // order is the ordinal file-name order the contract documents.
        var yamlDocuments = _fileStore
            .ReadAllDocuments(inputRoot, metadata.TableName, sqlEntry.Files, log)
            .ToList();
        var yamlRows = yamlDocuments.Select(d => d.Row).ToList();

        // Engine issue #27: take each row's pageRefs block (PageUniqueId per integer page-id
        // column) off the row before any column handling — it is not a table column. Rows
        // written before 1.0.4 carry none and resolve through the id map alone.
        var pageRefsByRow = new Dictionary<Dictionary<string, object?>, IReadOnlyDictionary<string, Guid>>(
            ReferenceEqualityComparer.Instance);
        foreach (var row in yamlRows)
        {
            var refs = PageReferences.TakeFromRow(row);
            if (refs.Count > 0) pageRefsByRow[row] = refs;
        }
        if (linkResolver != null)
            linkResolver.CurrentEntry ??= sqlEntry.EntryId;
        var localPageIdByGuid = new Dictionary<Guid, int?>();
        int? LocalPageId(Guid pageUniqueId)
        {
            if (localPageIdByGuid.TryGetValue(pageUniqueId, out var cached)) return cached;
            int? found;
            try { found = _tableReader.FindPageIdByUniqueId(pageUniqueId); }
            catch (Exception ex)
            {
                Log($"  [{tableName}] page lookup by PageUniqueId {pageUniqueId} failed: {ex.Message}", log);
                found = null;
            }
            localPageIdByGuid[pageUniqueId] = found;
            return found;
        }
        Log($"Deserializing {yamlRows.Count} rows into {metadata.TableName} (isDryRun={isDryRun})", log);

        // Ownership header: each row document carries its own mode; a row without one runs
        // with the pass strategy. Keyed by reference because rows are mutated below.
        var rowModes = yamlDocuments.ToDictionary(d => d.Row, d => d.Mode, ReferenceEqualityComparer.Instance);
        var headerOverrides = yamlDocuments.Count(d => DocumentHeader.StrategyFor(d.Mode, strategy) != strategy);
        if (headerOverrides > 0)
            Log($"  [{metadata.TableName}] {headerOverrides} row(s) carry a different mode in their document header and run with that mode.", log);

        // LRN-hosted-publish-10: identity-PK relation tables. Auto-ids are environment-local —
        // matching/inserting by the payload's explicit auto-id collides with the target's own
        // rows and the relation rows silently never land (the customer-visible casualty was
        // add-to-cart, refused because the variant combination didn't exist). For known relation
        // tables, switch row identity to the NATURAL KEY and strip the identity column from the
        // payload rows so the target assigns its own auto-id. Guarded by the live schema (see
        // IdentityPkRelationTables.GetNaturalKey) — when the guards don't hold, legacy behavior
        // is preserved and the collision WARNING below covers the gap.
        var identityOnlyPk = IdentityPkRelationTables.IsIdentityOnlyPk(metadata);
        var naturalKey = IdentityPkRelationTables.GetNaturalKey(metadata);
        if (naturalKey != null)
        {
            Log(
                $"  [{metadata.TableName}] identity-PK relation table — matching rows by natural key " +
                $"({string.Join(", ", naturalKey)}); payload auto-ids are ignored and the target assigns its own.",
                log);
            foreach (var row in yamlRows)
                foreach (var identityCol in metadata.IdentityColumns)
                    row.Remove(identityCol);
            metadata = metadata with { KeyColumns = naturalKey.ToList() };
        }

        // Foundry #1305: a table with no PRIMARY KEY used to take a truncate-and-insert path
        // in EVERY mode, so a Merge of one row deleted every target row the payload did not
        // carry and the run still reported "N created, 0 failed". Resolve a match key instead
        // (primary key, the entry's keyColumns, a unique index, then the full column tuple)
        // and write the table through the same MERGE upsert path a keyed table takes.
        var entryWarnings = new List<string>();
        KeyResolution keyResolution;
        try
        {
            keyResolution = KeyResolution.Resolve(
                metadata,
                sqlEntry.KeyColumns,
                () => _metadataReader.GetUniqueIndexes(metadata.TableName),
                yamlRows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (InvalidOperationException ex)
        {
            Log($"  ERROR: {ex.Message}", log);
            return new ProviderDeserializeResult
            {
                TableName = metadata.TableName,
                Failed = yamlRows.Count,
                Errors = [ex.Message]
            };
        }

        if (keyResolution.Source == KeyResolutionSource.DeclaredKeyColumns)
        {
            // Engine issue #30: a key the entry declares is as deliberate as a PRIMARY KEY, so it
            // is an info line, never a WARNING. A WARNING here failed every strict-mode API
            // deserialize of a heap whose author had already stated the key.
            metadata = metadata with { KeyColumns = keyResolution.KeyColumns.ToList() };
            Log($"  [{metadata.TableName}] has no primary key; rows matched by the declared keyColumns " +
                $"({string.Join(", ", keyResolution.KeyColumns)}); target rows not in the payload are preserved.", log);
        }
        else if (keyResolution.IsInferred)
        {
            metadata = metadata with { KeyColumns = keyResolution.KeyColumns.ToList() };
            var keyWarning =
                $"[{metadata.TableName}] has no primary key; rows matched by {keyResolution.Describe()}; " +
                "target rows not in the payload are preserved.";
            Log($"  WARNING: {keyWarning}", log);
            entryWarnings.Add(keyWarning);
        }

        // replaceStrategy: the only opt-in that still deletes, and only under Replace.
        var wantsTruncate = false;
        if (!string.IsNullOrWhiteSpace(sqlEntry.ReplaceStrategy))
        {
            if (!string.Equals(sqlEntry.ReplaceStrategy, "truncate", StringComparison.OrdinalIgnoreCase))
            {
                var error =
                    $"[{metadata.TableName}] declares replaceStrategy '{sqlEntry.ReplaceStrategy}', which is not " +
                    "a supported value. The only supported value is 'truncate'; omit the field for the default " +
                    "upsert behaviour.";
                Log($"  ERROR: {error}", log);
                return new ProviderDeserializeResult
                {
                    TableName = metadata.TableName,
                    Failed = yamlRows.Count,
                    Errors = [error]
                };
            }

            if (strategy == ConflictStrategy.SourceWins)
            {
                wantsTruncate = true;
                var truncateWarning =
                    $"[{metadata.TableName}] declares replaceStrategy: truncate — every target row is deleted " +
                    "before the payload is written.";
                Log($"  WARNING: {truncateWarning}", log);
                entryWarnings.Add(truncateWarning);
            }
            else
            {
                var ignoredWarning =
                    $"[{metadata.TableName}] declares replaceStrategy: truncate, which is ignored under Merge — " +
                    "Merge never deletes.";
                Log($"  WARNING: {ignoredWarning}", log);
                entryWarnings.Add(ignoredWarning);
            }
        }

        // Phase 37-02: unified schema-drift + type coercion via TargetSchemaCache.
        // Target columns absent from the live target schema are stripped from each row
        // before composing MERGE SQL (prevents "Invalid column name" on cross-environment syncs);
        // remaining string values are coerced to proper .NET types for SQL parameterization.
        var targetCols = _schemaCache.GetColumns(metadata.TableName);
        var columnTypes = _schemaCache.GetColumnTypes(metadata.TableName);
        var notNullColumns = _metadataReader.GetNotNullColumns(metadata.TableName);
        // FixNotNullDefaults takes a mutable Dictionary<string,string> — materialize once.
        var columnTypesDict = columnTypes.Count > 0
            ? new Dictionary<string, string>(columnTypes, StringComparer.OrdinalIgnoreCase)
            : _metadataReader.GetColumnTypes(metadata.TableName);
        foreach (var row in yamlRows)
        {
            // Filter target-missing columns (warn once per missing column across all rows).
            if (targetCols.Count > 0)
            {
                var keysToRemove = row.Keys.Where(k => !targetCols.Contains(k)).ToList();
                foreach (var k in keysToRemove)
                {
                    _schemaCache.LogMissingColumnOnce(metadata.TableName, k, log);
                    row.Remove(k);
                }
            }

            // Coerce remaining column values via the shared cache.
            foreach (var col in row.Keys.ToList())
            {
                var coerced = _schemaCache.Coerce(metadata.TableName, col, row[col]);
                // Coerce returns DBNull.Value for null/DBNull/empty-non-string cases; the downstream
                // row shape uses null (not DBNull) to represent "no value", so re-normalize here —
                // preserves the pre-refactor semantic contract of the row dictionary.
                row[col] = coerced == DBNull.Value ? null : coerced;
            }

            FixNotNullDefaults(row, columnTypesDict, notNullColumns);
            if (sqlEntry.XmlColumns.Count > 0)
                CompactXmlColumns(row, sqlEntry.XmlColumns);

            // Phase 37-05 / LINK-02 pass 2 (D-22): rewrite Default.aspx?ID=N in opted-in
            // string columns using the cross-environment page ID map built by preceding
            // Content provider runs. Engine issue #27: integer page-id columns resolve too —
            // by the row's recorded PageUniqueId first, then by the id map. String columns
            // are a no-op when no resolver was threaded through by the orchestrator.
            if (sqlEntry.ResolveLinksInColumns.Count > 0)
            {
                pageRefsByRow.TryGetValue(row, out var rowPageRefs);
                _writer.ApplyLinkResolution(row, sqlEntry.ResolveLinksInColumns, linkResolver,
                    rowPageRefs, LocalPageId, log, metadata.TableName);
            }
        }

        if (sqlEntry.ResolveLinksInColumns.Count > 0)
        {
            var status = linkResolver != null
                ? "active"
                : "entry configured but no map available; int columns resolve by pageRefs only";
            Log(
                $"Link resolution for [{metadata.TableName}] ({status}): " +
                string.Join(", ", sqlEntry.ResolveLinksInColumns),
                log);
        }

        // Engine issue #20: two documents in one directory can carry the SAME row identity — a
        // layered composition puts a partial override row (only some columns) next to a full row.
        // Applied one after the other, both took the "not in the target snapshot" path and the
        // file that happened to sort last won, so correctness rested on a naming convention and
        // on the host's string comparer. The rule is now explicit: same identity in one pass is
        // merged later-layer-wins, column by column, before anything is written. On a blank
        // target that also stops a partial document inserting a row carrying only its own columns.
        if (metadata.KeyColumns.Count > 0 && yamlRows.Count > 1)
        {
            var winnerByIdentity = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var collapsedRows = new List<Dictionary<string, object?>>(yamlRows.Count);
            var duplicateIdentities = 0;

            foreach (var row in yamlRows)
            {
                var rowIdentity = _tableReader.GenerateRowIdentity(row, metadata);
                if (winnerByIdentity.TryGetValue(rowIdentity, out var winner))
                {
                    duplicateIdentities++;
                    foreach (var column in row)
                        winner[column.Key] = column.Value;   // later document wins, per column
                    rowModes[winner] = rowModes[row];        // and its ownership header wins too
                    Log(
                        $"  [{metadata.TableName}] identity '{rowIdentity}' is carried by more than one " +
                        $"document — merged later-layer-wins ({row.Count} column(s) from the later document).",
                        log);
                    continue;
                }

                winnerByIdentity[rowIdentity] = row;
                collapsedRows.Add(row);
            }

            if (duplicateIdentities > 0)
            {
                Log(
                    $"  [{metadata.TableName}] {duplicateIdentities} duplicate-identity document(s) merged; " +
                    $"{collapsedRows.Count} row(s) will be written. Read order is ordinal by file name.",
                    log);
                yamlRows = collapsedRows;
            }
        }

        // Disable FK constraints during deserialization to avoid ordering issues
        if (!isDryRun)
        {
            try { _writer.DisableForeignKeys(metadata.TableName); }
            catch { /* Table may not have FK constraints */ }
        }

        int created = 0, updated = 0, skipped = 0, failed = 0, deleted = 0;
        var errors = new List<string>();

        // The opted-in whole-table replacement. Every other path below only ever upserts.
        if (wantsTruncate)
        {
            // Identity values are only re-inserted when they are the match key themselves.
            var preserveIdentityValues = keyResolution.Source == KeyResolutionSource.PrimaryKey
                && metadata.IdentityColumns.Any(ic => metadata.KeyColumns.Contains(ic, StringComparer.OrdinalIgnoreCase));

            if (!isDryRun)
            {
                try
                {
                    deleted = _writer.TruncateAndInsertAll(yamlRows, metadata, preserveIdentityValues, log);
                    created = yamlRows.Count;
                }
                catch (Exception ex)
                {
                    Log($"  ERROR: truncate+insert failed for [{metadata.TableName}]: {ex.Message}", log);
                    failed = yamlRows.Count;
                    errors.Add($"Truncate+insert failed: {ex.Message}");
                }
            }
            else
            {
                created = yamlRows.Count;
            }
        }
        else
        {
            // Build checksum lookup from existing DB rows for skip-on-unchanged detection.
            // Phase 39 D-17: also capture the full row dict keyed by identity — zero extra
            // round-trips since we're already enumerating every row here. The merge branch
            // below needs per-column target values to drive MergePredicate + XmlMergeHelper.
            var existingChecksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var existingRowsByIdentity =
                new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var existingRow in _tableReader.ReadAllRows(metadata.TableName))
            {
                var identity = _tableReader.GenerateRowIdentity(existingRow, metadata);
                var checksum = _tableReader.CalculateChecksum(existingRow, metadata);
                existingChecksums[identity] = checksum;
                existingRowsByIdentity[identity] = existingRow;
            }

            int autoIdCollisions = 0;

            foreach (var yamlRow in yamlRows)
            {
                var identity = _tableReader.GenerateRowIdentity(yamlRow, metadata);
                var incomingChecksum = _tableReader.CalculateChecksum(yamlRow, metadata);

                // Skip if existing row has identical checksum (no actual change)
                if (existingChecksums.TryGetValue(identity, out var existingChecksum)
                    && string.Equals(incomingChecksum, existingChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    Log($"  Skipped {identity} (unchanged)", log);
                    continue;
                }

                // LRN-hosted-publish-10 (warn path): an identity-PK relation table WITHOUT a
                // natural-key mapping is about to bind this row by its environment-local auto-id,
                // and the target already has a DIFFERENT row under that auto-id (checksum differs
                // — the identical case skipped above). This is the silent class that zeroed
                // add-to-cart: the write hits an unrelated row or never lands, and the run still
                // reports 0 failed. WARNING prefix rides the orchestrator's strict-mode escalator.
                if (identityOnlyPk && naturalKey == null
                    && string.IsNullOrEmpty(metadata.NameColumn)
                    && IdentityPkRelationTables.LooksLikeRelationTable(metadata.TableName)
                    && existingChecksums.ContainsKey(identity))
                {
                    autoIdCollisions++;
                    if (autoIdCollisions == 1)
                    {
                        Log(
                            $"  WARNING: [{metadata.TableName}] auto-id collision: payload row auto-id " +
                            $"'{identity}' matches an existing target row with different content. Auto-ids " +
                            "are environment-local — this write binds by auto-id and may hit an unrelated " +
                            "row or silently never land (LRN-hosted-publish-10). Add the table to " +
                            "IdentityPkRelationTables to match by natural key instead.",
                            log);
                    }
                }

                // Merge mode: field-level fill. When identity matches an existing
                // target row, we diff YAML values against target per-column using the
                // MergePredicate (scalar) and XmlMergeHelper (xml data type) predicates, and
                // only UPDATE the subset of columns where target is "unset" per D-01/D-22.
                // Identity non-match falls through to the existing _writer.WriteRow MERGE path.
                var rowStrategy = DocumentHeader.StrategyFor(rowModes[yamlRow], strategy);
                if (rowStrategy == ConflictStrategy.DestinationWins
                    && existingRowsByIdentity.TryGetValue(identity, out var currentRow))
                {
                    var sqlColumnTypes = _schemaCache.GetColumnTypes(metadata.TableName);
                    var mergedRow = new Dictionary<string, object?>(currentRow, StringComparer.OrdinalIgnoreCase);
                    var columnsToUpdate = new List<string>();
                    var xmlFills = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    var scalarFills = new Dictionary<string, (object? target, object? fill)>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in yamlRow)
                    {
                        var col = kvp.Key;
                        var yamlValue = kvp.Value;

                        // D-05: never overwrite identity/key columns.
                        if (metadata.KeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
                        if (metadata.IdentityColumns.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;

                        // D-12: column missing from target schema -> silent drop (already logged
                        // once during the schema-drift filter above for non-identity cols; repeat
                        // defensively to catch YAML keys that survived the initial filter).
                        if (!currentRow.TryGetValue(col, out var targetValue))
                        {
                            _schemaCache.LogMissingColumnOnce(metadata.TableName, col, log);
                            continue;
                        }

                        var sqlType = sqlColumnTypes.TryGetValue(col, out var t) ? t : null;

                        // D-21 + D-23: XML columns get element-level merge (D-22 rule), not scalar.
                        if (IsXmlColumn(sqlType))
                        {
                            var targetXml = targetValue as string;
                            var sourceXml = yamlValue as string;
                            var (merged, fills) = XmlMergeHelper.MergeWithDiagnostics(targetXml, sourceXml);
                            if (fills.Count > 0 && !string.Equals(merged, targetXml, StringComparison.Ordinal))
                            {
                                mergedRow[col] = merged;
                                columnsToUpdate.Add(col);
                                xmlFills[col] = fills;
                            }
                            continue;
                        }

                        // D-01 via IsUnsetForMergeBySqlType: scalar merge.
                        if (MergePredicate.IsUnsetForMergeBySqlType(targetValue, sqlType))
                        {
                            mergedRow[col] = yamlValue;
                            columnsToUpdate.Add(col);
                            scalarFills[col] = (targetValue, yamlValue);
                        }
                    }

                    if (columnsToUpdate.Count == 0)
                    {
                        skipped++;
                        Log($"  Merge-fill: [{metadata.TableName}].{identity} - 0 filled, all set", log);
                        continue;
                    }

                    if (isDryRun)
                    {
                        foreach (var col in columnsToUpdate)
                        {
                            if (xmlFills.TryGetValue(col, out var fills))
                            {
                                foreach (var fill in fills)
                                    Log($"    would fill [{metadata.TableName}.{col}, {fill}]", log);
                            }
                            else if (scalarFills.TryGetValue(col, out var pair))
                            {
                                Log(
                                    $"    would fill [{metadata.TableName}.{col}]: target=<unset> -> fill='{pair.fill}'",
                                    log);
                            }
                        }
                        updated++;
                        Log(
                            $"  [DRY-RUN] Merge-fill: [{metadata.TableName}].{identity} - {columnsToUpdate.Count} would-fill",
                            log);
                        continue;
                    }

                    var mergeOutcome = _writer.UpdateColumnSubset(
                        metadata.TableName, metadata.KeyColumns, mergedRow,
                        columnsToUpdate, isDryRun: false, log);
                    switch (mergeOutcome)
                    {
                        case WriteOutcome.Updated:
                            updated++;
                            var remaining = Math.Max(0, currentRow.Count - columnsToUpdate.Count - metadata.KeyColumns.Count);
                            Log(
                                $"  Merge-fill: [{metadata.TableName}].{identity} - {columnsToUpdate.Count} filled, {remaining} left",
                                log);
                            break;
                        case WriteOutcome.Failed:
                            failed++;
                            errors.Add($"Merge-fill failed: [{metadata.TableName}].{identity}");
                            break;
                    }
                    continue;
                }

                var outcome = _writer.WriteRow(yamlRow, metadata, isDryRun, log, notNullColumns);
                switch (outcome)
                {
                    case WriteOutcome.Created:
                        created++;
                        break;
                    case WriteOutcome.Updated:
                        updated++;
                        break;
                    case WriteOutcome.Failed:
                        failed++;
                        errors.Add($"Failed to write row: {identity}");
                        break;
                }

                Log($"  {outcome} {identity}", log);
            }

            if (autoIdCollisions > 1)
                Log($"  [{metadata.TableName}] {autoIdCollisions} auto-id collision row(s) total (first one warned above).", log);
        }

        // Re-enable FK constraints
        if (!isDryRun)
        {
            try { _writer.EnableForeignKeys(metadata.TableName); }
            catch (Exception ex) { Log($"  WARNING: Could not re-enable FK constraints for [{metadata.TableName}]: {ex.Message}", log); }
        }

        Log(
            $"Deserialization complete: {created} created, {updated} updated, {skipped} skipped, " +
            $"{failed} failed, {deleted} deleted",
            log);

        return new ProviderDeserializeResult
        {
            Created = created,
            Updated = updated,
            Skipped = skipped,
            Failed = failed,
            Deleted = deleted,
            TableName = metadata.TableName,
            Errors = errors,
            Warnings = entryWarnings
        };
    }

    /// <summary>
    /// Phase 39 D-21: columns whose SQL DATA_TYPE is <c>xml</c> get element-level merge
    /// via <see cref="XmlMergeHelper"/> instead of scalar <see cref="MergePredicate"/>.
    /// INFORMATION_SCHEMA reports <c>"xml"</c> for T-SQL xml columns.
    /// </summary>
    private static bool IsXmlColumn(string? sqlDataType)
        => !string.IsNullOrEmpty(sqlDataType)
           && string.Equals(sqlDataType, "xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replace null values with type-appropriate defaults for NOT NULL columns.
    /// Prevents "cannot insert NULL" errors during MERGE upsert.
    /// </summary>
    private static void FixNotNullDefaults(Dictionary<string, object?> row, Dictionary<string, string> columnTypes, HashSet<string> notNullColumns)
    {
        foreach (var col in notNullColumns)
        {
            if (!row.ContainsKey(col)) continue;
            if (row[col] is not null) continue;

            // Substitute appropriate default for NOT NULL columns with null YAML values
            if (columnTypes.TryGetValue(col, out var sqlType))
            {
                row[col] = sqlType.ToLowerInvariant() switch
                {
                    "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" or "xml" => "",
                    "int" or "bigint" or "smallint" or "tinyint" => 0,
                    "bit" => false,
                    "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => 0m,
                    _ => row[col] // leave as null for types we can't default (let SQL fail with a clear error)
                };
            }
        }
    }

    /// <summary>
    /// Compact XML columns back to single-line before DB write.
    /// Restores compact format so serialize->deserialize->serialize is idempotent.
    /// </summary>
    private static void CompactXmlColumns(Dictionary<string, object?> row, IReadOnlyCollection<string> xmlColumns)
    {
        foreach (var col in xmlColumns)
        {
            if (row.TryGetValue(col, out var val) && val is string strVal)
            {
                row[col] = XmlFormatter.Compact(strVal);
            }
        }
    }

    /// <summary>
    /// Phase 43 / DESER-03: ValidatePredicate no longer satisfies the
    /// <see cref="ISerializationProvider"/> contract (interface dropped it — validation moves
    /// to manifest read time). Kept as a serialize-side input gate; the <see cref="Serialize"/>
    /// body still calls it.
    /// </summary>
    public ValidationResult ValidatePredicate(ProviderPredicateDefinition predicate)
    {
        if (!string.Equals(predicate.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Failure("Provider type mismatch");

        if (string.IsNullOrEmpty(predicate.Table))
            return ValidationResult.Failure("Table is required for SqlTable predicates");

        return ValidationResult.Success();
    }
}
