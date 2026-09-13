using Dynamicweb.Content;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.Providers.Content;

/// <summary>
/// ISerializationProvider adapter for content serialization.
/// Wraps existing ContentSerializer/ContentDeserializer without modifying their internals.
/// Routes content YAML to/from _content/ subdirectory under the output/input root.
/// </summary>
public class ContentProvider : ISerializationProvider
{
    private readonly string? _filesRoot;

    public string ProviderType => "Content";
    public string DisplayName => "Content Provider";

    /// <summary>
    /// Creates a new ContentProvider.
    /// </summary>
    /// <param name="filesRoot">
    /// Optional path to the Files/ root directory, needed by ContentDeserializer for template validation.
    /// </param>
    public ContentProvider(string? filesRoot = null)
    {
        _filesRoot = filesRoot;
    }

    /// <summary>
    /// Phase 43 / DESER-03: ValidatePredicate no longer satisfies the
    /// <see cref="ISerializationProvider"/> contract (interface dropped it — validation moves
    /// to manifest read time). Kept as a serialize-side input gate; the <see cref="Serialize"/>
    /// body still calls it.
    /// </summary>
    public ValidationResult ValidatePredicate(ProviderPredicateDefinition predicate)
    {
        if (!string.Equals(predicate.ProviderType, "Content", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Failure("Provider type mismatch: expected 'Content'");

        if (string.IsNullOrWhiteSpace(predicate.Path))
            return ValidationResult.Failure("Path is required for Content predicates");

        if (predicate.AreaId <= 0)
            return ValidationResult.Failure("AreaId must be > 0 for Content predicates");

        return ValidationResult.Success();
    }

    public SerializeResult Serialize(
        ProviderPredicateDefinition predicate,
        string outputRoot,
        Action<string>? log = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        var validation = ValidatePredicate(predicate);
        if (!validation.IsValid)
        {
            return new SerializeResult
            {
                TableName = "Content",
                Errors = validation.Errors
            };
        }

        try
        {
            var contentDir = Path.Combine(outputRoot, "_content");
            Directory.CreateDirectory(contentDir);

            var config = BuildSerializerConfiguration(predicate, contentDir,
                excludeFieldsByItemType, excludeXmlElementsByType);
            var store = new Infrastructure.FileSystemStore { ResolveExistingPageFolders = predicate.IsInlineScope };
            var serializer = new ContentSerializer(config, store, log: log);
            serializer.Serialize();

            // Track THIS predicate run's written files for the per-mode manifest. The store
            // records exactly what it wrote — enumerating the shared _content directory
            // instead made every later predicate's manifest entry absorb all earlier
            // predicates' files (per-entry deserialize pruning then re-processed siblings).
            // templates.manifest.yml is run-level (rewritten by every Content pass) and is
            // included so the manifest cleaner never treats it as stale.
            var writtenFiles = new List<string>(store.WrittenFiles);
            var templatesManifest = Path.Combine(contentDir, Infrastructure.TemplateAssetManifest.ManifestFileName);
            if (File.Exists(templatesManifest))
                writtenFiles.Add(Path.GetFullPath(templatesManifest));

            return new SerializeResult
            {
                RowsSerialized = writtenFiles.Count,
                TableName = "Content",
                WrittenFiles = writtenFiles,
                Entry = BuildManifestEntry(predicate, outputRoot, writtenFiles)
            };
        }
        catch (Exception ex)
        {
            log?.Invoke($"ERROR: Content serialization failed: {ex.Message}");
            return new SerializeResult
            {
                TableName = "Content",
                Errors = new[] { ex.Message }
            };
        }
    }

    /// <summary>
    /// Phase 44 / CONVERGE-01 (D-02): build a <see cref="ContentEntry"/> for an entire DW area
    /// from an <paramref name="areaId"/> + <paramref name="contentRoot"/>, without consulting
    /// any DW services beyond <see cref="Services.Areas"/> for name resolution. Pure helper —
    /// used by both the full deserialize path (via <see cref="BuildManifestEntry"/>, which
    /// projects the predicate down into this shape for whole-area predicates) and
    /// <see cref="AdminUI.Commands.DeserializeFromZipCommand"/> (which calls this directly
    /// with its target area id). Single canonical shape source eliminates the duplicate
    /// construction that previously lived in zip-import's synthetic <c>SerializerConfiguration</c>.
    /// </summary>
    /// <param name="areaId">DW Area id (must be &gt; 0; not validated here — caller's contract).</param>
    /// <param name="contentRoot">Root directory of the YAML tree; files enumerated under here drive <see cref="ContentEntry.Files"/>.</param>
    /// <param name="acknowledgedOrphanPageIds">
    /// Optional acknowledged-orphan list — int-typed per Phase 44 / INFO 8 (no lossy
    /// int→string→int round-trip; <see cref="ProviderPredicateDefinition.AcknowledgedOrphanPageIds"/>
    /// is <c>List&lt;int&gt;</c> and <see cref="ContentEntry.AcknowledgedOrphanPageIds"/> is
    /// <c>IReadOnlyList&lt;int&gt;</c>). Defaults to empty.
    /// </param>
    /// <param name="excludeFields">
    /// Optional item-field exclusion list — projected into <see cref="ContentEntry.ExcludeFields"/>
    /// per Phase 44 / BLOCKER 2 fix. Defaults to empty.
    /// </param>
    public static ContentEntry BuildContentEntryForArea(
        int areaId,
        string contentRoot,
        IEnumerable<int>? acknowledgedOrphanPageIds = null,
        IEnumerable<string>? excludeFields = null)
    {
        var files = Directory.Exists(contentRoot)
            ? Directory.GetFiles(contentRoot, "*.yml", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(contentRoot, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        return new ContentEntry
        {
            EntryId = $"content/area-{areaId}",
            Files = files,
            AreaId = areaId,
            AreaName = ResolveAreaName(areaId),
            Path = "/",
            PageId = 0,
            AcknowledgedOrphanPageIds = (acknowledgedOrphanPageIds ?? Array.Empty<int>()).ToList(),
            ExcludeAreaColumns = Array.Empty<string>().ToList(),
            ExcludeFields = (excludeFields ?? Array.Empty<string>()).ToList()
        };
    }

    /// <summary>
    /// Phase 42-03 / PROVIDER-02 (refactored in Phase 44 / D-02): build a
    /// <see cref="ContentEntry"/> from the predicate that drove the run + the absolute
    /// paths of every file the run emitted. Whole-area predicates (<c>Path == "/"</c> or
    /// empty) reuse <see cref="BuildContentEntryForArea"/> for shape consistency; subtree
    /// predicates construct directly because <see cref="BuildContentEntryForArea"/> is
    /// whole-area only by D-02 contract.
    /// </summary>
    public ManifestEntry BuildManifestEntry(
        ProviderPredicateDefinition predicate,
        string modeRoot,
        IReadOnlyList<string> writtenFiles)
    {
        var projectedFiles = writtenFiles
            .Select(f => Path.GetRelativePath(modeRoot, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // For whole-area predicates (Path = "/" or empty), reuse the shared helper.
        if (string.IsNullOrEmpty(predicate.Path) || predicate.Path == "/")
        {
            var entry = BuildContentEntryForArea(predicate.AreaId, modeRoot,
                predicate.AcknowledgedOrphanPageIds,
                predicate.ExcludeFields);
            return entry with
            {
                // Preserve writtenFiles — modeRoot enumeration may differ from the exact
                // files the serialize-pass emitted (which is what writtenFiles tracks).
                Files = projectedFiles,
                ExcludeAreaColumns = predicate.ExcludeAreaColumns.ToList(),
                ExcludeFields = predicate.ExcludeFields.ToList()
            };
        }

        // Subtree predicate — preserve the original BuildManifestEntry shape (subtree path,
        // PageId, etc.). BuildContentEntryForArea is whole-area only.
        return new ContentEntry
        {
            EntryId = $"content/area-{predicate.AreaId}{(predicate.Path.StartsWith('/') ? predicate.Path : "/" + predicate.Path)}",
            Files = projectedFiles,
            AreaId = predicate.AreaId,
            AreaName = ResolveAreaName(predicate.AreaId),
            Path = predicate.Path,
            PageId = predicate.PageId,
            AcknowledgedOrphanPageIds = predicate.AcknowledgedOrphanPageIds.ToList(),
            ExcludeAreaColumns = predicate.ExcludeAreaColumns.ToList(),
            ExcludeFields = predicate.ExcludeFields.ToList()
        };
    }

    /// <summary>Resolves DW Area name; falls back to "Area {id}" if DW infra is unavailable (test contexts).</summary>
    internal static string ResolveAreaName(int areaId)
    {
        try
        {
            var area = Services.Areas.GetArea(areaId);
            return area?.Name ?? $"Area {areaId}";
        }
        catch
        {
            return $"Area {areaId}";
        }
    }

    public ProviderDeserializeResult Deserialize(
        ManifestEntry entry,
        string inputRoot,
        Action<string>? log = null,
        bool isDryRun = false,
        ConflictStrategy strategy = ConflictStrategy.SourceWins,
        InternalLinkResolver? linkResolver = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // ContentProvider ignores the injected linkResolver — its own deserialize path already
        // builds and applies an InternalLinkResolver for item-field / PropertyItem rewriting.
        // We still accept the parameter to satisfy the ISerializationProvider contract.
        _ = linkResolver;

        // Phase 43 / DESER-03: downcast at the entry-point. Validation moves to manifest
        // read time (Phase 42 ManifestSchema strict-read + ManifestEntry required modifiers);
        // this defensive downcast guards against a misregistered provider being asked to
        // dispatch the wrong entry shape.
        if (entry is not ContentEntry contentEntry)
        {
            return new ProviderDeserializeResult
            {
                TableName = "Content",
                Errors = new[] { $"Expected ContentEntry, got {entry.GetType().Name}" }
            };
        }

        try
        {
            // Clear area cache — when SqlTable predicates insert Area rows before Content
            // runs, DW's cached AreaService may still return stale data
            try { Services.Areas.ClearCache(); }
            catch { /* ignore if cache clear fails */ }

            var contentDir = Path.Combine(inputRoot, "_content");

            // Fall back to inputRoot if _content/ subdirectory doesn't exist
            // (supports zips created by ad-hoc serialize which don't use the _content/ prefix)
            if (!Directory.Exists(contentDir))
                contentDir = inputRoot;

            // Phase 44 / D-04: direct ContentEntry-typed dispatch. The synthetic
            // SerializerConfiguration path (BuildSerializerConfigurationFromEntry) is gone;
            // ContentDeserializer takes the entry + contentRoot directly. excludeXmlElementsByType
            // is unused on the content deserialize side (XML-element exclusions are SqlTable-XML
            // only, applied in SqlTableProvider's XmlMergeHelper). Discarded to satisfy
            // ISerializationProvider contract.
            _ = excludeXmlElementsByType;
            var deserializer = new ContentDeserializer(
                contentEntry,
                contentDir,
                log: log,
                isDryRun: isDryRun,
                filesRoot: _filesRoot,
                conflictStrategy: strategy,
                excludeFieldsByItemType: excludeFieldsByItemType);
            var result = deserializer.Deserialize();

            if (strategy == ConflictStrategy.DestinationWins)
                log?.Invoke($"Content provider running in DestinationWins (Merge) mode — pages whose PageUniqueId is already present on target are preserved.");

            // Phase 37-05 / LINK-02 pass 2: after a successful deserialize, build the
            // source→target page ID map from the YAML tree (SourcePageId) + the target DB
            // (by GUID match) so SqlTable predicates in the same orchestrator run can
            // rewrite Default.aspx?ID=N references in configured columns. Skipped on dry-run
            // or when the predicate didn't run (failed area resolution, etc.).
            IReadOnlyDictionary<int, int>? map = null;
            if (!isDryRun)
            {
                try { map = BuildSourceToTargetMap(contentDir); }
                catch (Exception mapEx)
                {
                    log?.Invoke($"WARNING: Could not build source→target page map after Content deserialize: {mapEx.Message}");
                }
            }

            return new ProviderDeserializeResult
            {
                Created = result.Created,
                Updated = result.Updated,
                Skipped = result.Skipped,
                Failed = result.Failed,
                TableName = "Content",
                Errors = result.Errors.ToList(),
                SourceToTargetPageMap = map
            };
        }
        catch (Exception ex)
        {
            log?.Invoke($"ERROR: Content deserialization failed: {ex.Message}");
            return new ProviderDeserializeResult
            {
                TableName = "Content",
                Errors = new[] { ex.Message }
            };
        }
    }

    /// <summary>
    /// Phase 37-05 / LINK-02 pass 2: construct the cross-environment page ID map by
    /// reading every area's YAML tree under <paramref name="contentDir"/>, pairing each
    /// page's <c>SourcePageId</c> with the target page resolved by <c>PageUniqueId</c>
    /// (GUID lookup against the live DB). Returns an empty map if no YAML areas are
    /// present or no pages matched.
    /// </summary>
    private static IReadOnlyDictionary<int, int> BuildSourceToTargetMap(string contentDir)
    {
        var allYamlPages = new List<SerializedPage>();
        if (Directory.Exists(contentDir))
        {
            var store = new FileSystemStore();
            foreach (var areaDir in Directory.GetDirectories(contentDir))
            {
                var areaYml = Path.Combine(areaDir, "area.yml");
                if (!File.Exists(areaYml)) continue;
                try
                {
                    var areaData = store.ReadTree(contentDir, Path.GetFileName(areaDir));
                    allYamlPages.AddRange(areaData.Pages);
                }
                catch { /* best-effort — individual unreadable areas skipped */ }
            }
        }

        var allGuidCache = new Dictionary<Guid, int>();
        foreach (var masterArea in Services.Areas.GetAreas())
        {
            foreach (var page in Services.Pages.GetPagesByAreaID(masterArea.ID))
                if (page.UniqueId != Guid.Empty)
                    allGuidCache.TryAdd(page.UniqueId, page.ID);
        }

        return InternalLinkResolver.BuildSourceToTargetMap(allYamlPages, allGuidCache);
    }

    /// <summary>
    /// Builds a SerializerConfiguration with a single predicate for delegation to
    /// ContentSerializer (serialize-side only — Phase 44 / D-04 removed the deserialize-side
    /// caller, <c>BuildSerializerConfigurationFromEntry</c>, which constructed a synthetic
    /// predicate; ContentDeserializer now consumes <see cref="ContentEntry"/> directly).
    /// Threads the parent mode's ItemType + XML-type exclusion dicts down into the inner
    /// config so the inner serializer respects all by-type exclusions. Phase 40 D-07: flat
    /// shape — no per-mode ModeConfig wrapper.
    /// </summary>
    private static SerializerConfiguration BuildSerializerConfiguration(
        ProviderPredicateDefinition predicate,
        string outputDirectory,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // Phase 40 D-07: flat config shape. The inner predicate carries its own Mode
        // (preserved from the caller's predicate). The aggregated exclusion dicts move
        // to the top-level config keys (D-04). AcknowledgedOrphanPageIds remains
        // per-predicate (Phase 38 D-38-03, unchanged).
        return new SerializerConfiguration
        {
            OutputDirectory = outputDirectory,
            Predicates = new List<ProviderPredicateDefinition> { predicate },
            ExcludeFieldsByItemType = excludeFieldsByItemType != null
                ? new Dictionary<string, List<string>>(
                    excludeFieldsByItemType.ToDictionary(kv => kv.Key, kv => kv.Value),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(),
            ExcludeXmlElementsByType = excludeXmlElementsByType != null
                ? new Dictionary<string, List<string>>(
                    excludeXmlElementsByType.ToDictionary(kv => kv.Key, kv => kv.Value),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>()
        };
    }
}
