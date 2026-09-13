using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>Outcome of <see cref="InlineScopeResolver.Resolve"/>.</summary>
public sealed record InlineScopeResolution(
    ProviderPredicateDefinition? Predicate,
    ProviderPredicateDefinition? Fence,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Predicate is not null && Errors.Count == 0;
}

/// <summary>
/// Turns an <see cref="InlineScope"/> into the effective predicate one API call runs, after the
/// boundary check against <c>Serializer.config.json</c>:
/// <list type="bullet">
/// <item>The scope must fall inside a configured predicate of the SAME mode (the fence). Content:
/// same area, scope root included by the fence path and not carved out by a fence exclude.
/// SqlTable: same table.</item>
/// <item>The scope can only narrow the fence. Excludes and field/element exclusions are unioned
/// with the fence's; a SqlTable <c>where</c> is ANDed with the fence's; <c>includeFields</c> must be
/// a subset of the fence's; <c>includeLanguageLayers</c> cannot be switched on when the fence has it
/// off. Fields that define identity or post-processing (name column, xml columns, caches, link
/// columns, acknowledged orphans) are owned by the fence and must be omitted or equal.</item>
/// <item>SqlTable identifiers and the combined WHERE clause go through the same
/// <see cref="SqlIdentifierValidator"/> / <see cref="SqlWhereClauseValidator"/> gate as config load.</item>
/// </list>
/// Pure apart from the injected validator and page resolver, so it is unit-testable without a host.
/// </summary>
public static class InlineScopeResolver
{
    /// <param name="scope">The inline scope from the request.</param>
    /// <param name="mode">The mode of the call. Fences are looked up among predicates of this mode only.</param>
    /// <param name="config">The loaded configuration (the fence).</param>
    /// <param name="identifierValidator">SQL identifier gate; null skips identifier checks (unit tests).</param>
    /// <param name="forDeserialize">Deserialize scopes select manifest entries: SqlTable serialize-time
    /// filters (<c>where</c>, include/exclude fields, xml elements) are rejected there.</param>
    /// <param name="resolvePage">Resolves <see cref="InlineScope.PageId"/> to (areaId, content path); null when unknown.</param>
    public static InlineScopeResolution Resolve(
        InlineScope scope,
        SerializerMode mode,
        SerializerConfiguration config,
        SqlIdentifierValidator? identifierValidator,
        bool forDeserialize,
        Func<int, (int AreaId, string Path)?>? resolvePage = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(config);

        var providerType = string.IsNullOrWhiteSpace(scope.ProviderType)
            ? (string.IsNullOrWhiteSpace(scope.Table) ? "Content" : "SqlTable")
            : scope.ProviderType.Trim();

        if (string.Equals(providerType, "Content", StringComparison.OrdinalIgnoreCase))
            return ResolveContent(scope, mode, config, resolvePage);
        if (string.Equals(providerType, "SqlTable", StringComparison.OrdinalIgnoreCase))
            return ResolveSqlTable(scope, mode, config, identifierValidator, forDeserialize);

        return Fail($"scope.providerType '{scope.ProviderType}' is not supported. Expected 'Content' or 'SqlTable'.");
    }

    // -------------------------------------------------------------------------
    // Content
    // -------------------------------------------------------------------------

    private static InlineScopeResolution ResolveContent(
        InlineScope scope, SerializerMode mode, SerializerConfiguration config,
        Func<int, (int AreaId, string Path)?>? resolvePage)
    {
        var errors = new List<string>();

        foreach (var (field, supplied) in new (string, bool)[]
                 {
                     ("table", !string.IsNullOrWhiteSpace(scope.Table)),
                     ("where", !string.IsNullOrWhiteSpace(scope.Where)),
                     ("includeFields", scope.IncludeFields is { Count: > 0 }),
                     ("nameColumn", !string.IsNullOrWhiteSpace(scope.NameColumn)),
                     ("compareColumns", !string.IsNullOrWhiteSpace(scope.CompareColumns)),
                     ("xmlColumns", scope.XmlColumns is { Count: > 0 }),
                     ("serviceCaches", scope.ServiceCaches is { Count: > 0 }),
                     ("schemaSync", !string.IsNullOrWhiteSpace(scope.SchemaSync)),
                     ("resolveLinksInColumns", scope.ResolveLinksInColumns is { Count: > 0 })
                 })
        {
            if (supplied)
                errors.Add($"scope.{field} is not valid for a Content scope.");
        }

        var areaId = scope.AreaId;
        var path = scope.Path?.Trim();

        if (string.IsNullOrEmpty(path) && scope.PageId > 0)
        {
            var page = resolvePage?.Invoke(scope.PageId);
            if (page is null)
                errors.Add($"scope.pageId {scope.PageId} does not resolve to a page on this host.");
            else
            {
                if (areaId > 0 && areaId != page.Value.AreaId)
                    errors.Add($"scope.pageId {scope.PageId} lives in area {page.Value.AreaId}, not scope.areaId {areaId}.");
                areaId = page.Value.AreaId;
                path = page.Value.Path;
            }
        }

        if (areaId <= 0)
            errors.Add("scope.areaId is required for a Content scope (or pass scope.pageId).");
        if (string.IsNullOrEmpty(path))
            errors.Add("scope.path is required for a Content scope (or pass scope.pageId).");
        else if (!path.StartsWith('/'))
            errors.Add($"scope.path '{path}' must start with '/'.");

        if (errors.Count > 0)
            return new InlineScopeResolution(null, null, errors);

        path = NormalizePath(path!);

        var excludes = (scope.Excludes ?? new List<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => NormalizePath(e.Trim()))
            .ToList();
        foreach (var e in excludes)
        {
            if (string.Equals(e, path, StringComparison.OrdinalIgnoreCase) || !ContentPredicate.IsUnderPath(e, path))
                errors.Add($"scope.excludes '{e}' is not below scope.path '{path}'.");
        }

        var sameMode = ContentPredicates(config, mode).ToList();
        var fence = FindContentFence(scope.Name, sameMode, areaId, path, errors);
        if (fence is null)
        {
            if (errors.Count == 0)
                errors.Add(ExplainMissingContentFence(config, mode, areaId, path));
            return new InlineScopeResolution(null, null, errors);
        }

        if (scope.IncludeLanguageLayers == true && !fence.IncludeLanguageLayers)
            errors.Add($"scope.includeLanguageLayers cannot be switched on: configured predicate '{fence.Name}' has it off.");
        CheckFenceOwnedInts("acknowledgedOrphanPageIds", scope.AcknowledgedOrphanPageIds, fence.AcknowledgedOrphanPageIds, fence, errors);

        if (errors.Count > 0)
            return new InlineScopeResolution(null, fence, errors);

        // Fence excludes that carve out part of the scope's subtree still apply.
        var inheritedExcludes = fence.Excludes
            .Where(e => ContentPredicate.IsUnderPath(e, path) && !string.Equals(e, path, StringComparison.OrdinalIgnoreCase));

        var effective = fence with
        {
            Name = string.IsNullOrWhiteSpace(scope.Name) ? $"{fence.Name} [inline {path}]" : scope.Name!,
            Mode = mode,
            ProviderType = "Content",
            AreaId = areaId,
            Path = path,
            PageId = scope.PageId > 0 ? scope.PageId : 0,
            Excludes = Union(excludes, inheritedExcludes),
            ExcludeFields = Union(fence.ExcludeFields, scope.ExcludeFields),
            ExcludeXmlElements = Union(fence.ExcludeXmlElements, scope.ExcludeXmlElements),
            ExcludeAreaColumns = Union(fence.ExcludeAreaColumns, scope.ExcludeAreaColumns),
            IncludeLanguageLayers = scope.IncludeLanguageLayers ?? fence.IncludeLanguageLayers,
            IsInlineScope = true
        };

        return new InlineScopeResolution(effective, fence, errors);
    }

    private static ProviderPredicateDefinition? FindContentFence(
        string? name, List<ProviderPredicateDefinition> sameMode, int areaId, string path, List<string> errors)
    {
        bool Covers(ProviderPredicateDefinition p) =>
            p.AreaId == areaId && new ContentPredicate(p).ShouldInclude(path, areaId);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var named = sameMode.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                if (!Covers(named))
                    errors.Add($"scope.path '{path}' in area {areaId} is outside configured predicate '{named.Name}' " +
                               $"(area {named.AreaId}, path '{named.Path}'{DescribeExcludes(named)}).");
                return named;
            }
        }

        // Most specific covering predicate: its excludes and exclusions are the closest defaults.
        return sameMode
            .Where(Covers)
            .OrderByDescending(p => p.Path.Length)
            .FirstOrDefault();
    }

    private static string ExplainMissingContentFence(SerializerConfiguration config, SerializerMode mode, int areaId, string path)
    {
        var modeWord = mode.ToString().ToLowerInvariant();
        var message = $"scope area {areaId} path '{path}' is outside what Serializer.config.json allows for mode {modeWord}: " +
                      "no Content predicate of that mode includes it.";

        var coverage = new ContentCoverageEvaluator(ContentPredicates(config, mode), modeWord).Evaluate(path, areaId);
        if (!string.IsNullOrEmpty(coverage.Explanation))
            message += $" Coverage: {coverage.Explanation}.";

        var otherMode = mode == SerializerMode.Replace ? SerializerMode.Merge : SerializerMode.Replace;
        var other = ContentPredicates(config, otherMode)
            .FirstOrDefault(p => new ContentPredicate(p).ShouldInclude(path, areaId));
        if (other is not null)
            message += $" It is {otherMode.ToString().ToLowerInvariant()}-owned by predicate '{other.Name}'; " +
                       $"call with mode={otherMode.ToString().ToLowerInvariant()}.";

        return message;
    }

    // -------------------------------------------------------------------------
    // SqlTable
    // -------------------------------------------------------------------------

    private static InlineScopeResolution ResolveSqlTable(
        InlineScope scope, SerializerMode mode, SerializerConfiguration config,
        SqlIdentifierValidator? identifierValidator, bool forDeserialize)
    {
        var errors = new List<string>();

        foreach (var (field, supplied) in new (string, bool)[]
                 {
                     ("areaId", scope.AreaId > 0),
                     ("path", !string.IsNullOrWhiteSpace(scope.Path)),
                     ("pageId", scope.PageId > 0),
                     ("excludes", scope.Excludes is { Count: > 0 }),
                     ("includeLanguageLayers", scope.IncludeLanguageLayers is not null),
                     ("excludeAreaColumns", scope.ExcludeAreaColumns is { Count: > 0 })
                 })
        {
            if (supplied)
                errors.Add($"scope.{field} is not valid for a SqlTable scope.");
        }

        if (forDeserialize)
        {
            foreach (var (field, supplied) in new (string, bool)[]
                     {
                         ("where", !string.IsNullOrWhiteSpace(scope.Where)),
                         ("excludeFields", scope.ExcludeFields is { Count: > 0 }),
                         ("includeFields", scope.IncludeFields is { Count: > 0 }),
                         ("excludeXmlElements", scope.ExcludeXmlElements is { Count: > 0 })
                     })
            {
                if (supplied)
                    errors.Add($"scope.{field} filters rows at serialize time and is not valid on Deserialize; " +
                               "a SqlTable deserialize scope selects the table's serialized rows.");
            }
        }

        if (string.IsNullOrWhiteSpace(scope.Table))
            errors.Add("scope.table is required for a SqlTable scope.");

        if (errors.Count > 0)
            return new InlineScopeResolution(null, null, errors);

        var table = scope.Table!.Trim();
        var candidates = config.Predicates
            .Where(p => p.Mode == mode
                        && string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Table, table, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ProviderPredicateDefinition? fence = null;
        if (!string.IsNullOrWhiteSpace(scope.Name))
            fence = candidates.FirstOrDefault(p => string.Equals(p.Name, scope.Name, StringComparison.OrdinalIgnoreCase));
        if (fence is null)
        {
            var unfiltered = candidates.Where(p => string.IsNullOrWhiteSpace(p.Where)).ToList();
            if (unfiltered.Count > 0)
                fence = unfiltered[0];
            else if (candidates.Count == 1)
                fence = candidates[0];
            else if (candidates.Count > 1)
                errors.Add($"scope.table '{table}' matches {candidates.Count} {mode.ToString().ToLowerInvariant()} predicates " +
                           $"({string.Join(", ", candidates.Select(c => $"'{c.Name}'"))}); pass scope.name to pick one.");
        }

        if (fence is null)
        {
            if (errors.Count == 0)
            {
                var message = $"scope.table '{table}' is outside what Serializer.config.json allows for mode " +
                              $"{mode.ToString().ToLowerInvariant()}: no SqlTable predicate of that mode names it.";
                var other = config.Predicates.FirstOrDefault(p => p.Mode != mode
                    && string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Table, table, StringComparison.OrdinalIgnoreCase));
                if (other is not null)
                    message += $" It is {other.Mode.ToString().ToLowerInvariant()}-owned by predicate '{other.Name}'; " +
                               $"call with mode={other.Mode.ToString().ToLowerInvariant()}.";
                errors.Add(message);
            }
            return new InlineScopeResolution(null, null, errors);
        }

        CheckFenceOwned("nameColumn", scope.NameColumn, fence.NameColumn, fence, errors);
        CheckFenceOwned("compareColumns", scope.CompareColumns, fence.CompareColumns, fence, errors);
        CheckFenceOwned("schemaSync", scope.SchemaSync, fence.SchemaSync, fence, errors);
        CheckFenceOwnedList("xmlColumns", scope.XmlColumns, fence.XmlColumns, fence, errors);
        CheckFenceOwnedList("serviceCaches", scope.ServiceCaches, fence.ServiceCaches, fence, errors);
        CheckFenceOwnedList("resolveLinksInColumns", scope.ResolveLinksInColumns, fence.ResolveLinksInColumns, fence, errors);
        CheckFenceOwnedInts("acknowledgedOrphanPageIds", scope.AcknowledgedOrphanPageIds, fence.AcknowledgedOrphanPageIds, fence, errors);

        var widened = (scope.IncludeFields ?? new List<string>())
            .Where(f => !fence.IncludeFields.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (widened.Count > 0)
            errors.Add($"scope.includeFields {string.Join(", ", widened)} widen configured predicate '{fence.Name}' " +
                       "(runtime-only columns stay excluded unless the config opts them in).");

        if (errors.Count > 0)
            return new InlineScopeResolution(null, fence, errors);

        var effective = fence with
        {
            Name = string.IsNullOrWhiteSpace(scope.Name) ? $"{fence.Name} [inline]" : scope.Name!,
            Mode = mode,
            ProviderType = "SqlTable",
            Table = fence.Table,
            Where = CombineWhere(fence.Where, scope.Where),
            ExcludeFields = Union(fence.ExcludeFields, scope.ExcludeFields),
            ExcludeXmlElements = Union(fence.ExcludeXmlElements, scope.ExcludeXmlElements),
            IncludeFields = scope.IncludeFields is { Count: > 0 } ? scope.IncludeFields.ToList() : fence.IncludeFields.ToList(),
            IsInlineScope = true
        };

        if (identifierValidator is not null)
        {
            ConfigLoader.CollectIdentifierErrors(effective, identifierValidator, new SqlWhereClauseValidator(), "scope", errors);
            if (errors.Count > 0)
                return new InlineScopeResolution(null, fence, errors);
        }

        return new InlineScopeResolution(effective, fence, errors);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IEnumerable<ProviderPredicateDefinition> ContentPredicates(SerializerConfiguration config, SerializerMode mode) =>
        config.Predicates.Where(p => p.Mode == mode
                                     && string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase));

    internal static string? CombineWhere(string? fenceWhere, string? scopeWhere)
    {
        var f = string.IsNullOrWhiteSpace(fenceWhere) ? null : fenceWhere.Trim();
        var s = string.IsNullOrWhiteSpace(scopeWhere) ? null : scopeWhere.Trim();
        if (f is null) return s;
        if (s is null) return f;
        return $"({f}) AND ({s})";
    }

    private static string NormalizePath(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;

    private static List<string> Union(IEnumerable<string>? first, IEnumerable<string>? second) =>
        (first ?? Enumerable.Empty<string>())
            .Concat(second ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void CheckFenceOwned(string field, string? supplied, string? configured,
        ProviderPredicateDefinition fence, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return;
        if (!string.Equals(supplied.Trim(), configured?.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add($"scope.{field} '{supplied}' differs from configured predicate '{fence.Name}' ('{configured}'). " +
                       "This field is owned by the configuration: omit it or pass the configured value.");
    }

    private static void CheckFenceOwnedList(string field, List<string>? supplied, List<string> configured,
        ProviderPredicateDefinition fence, List<string> errors)
    {
        if (supplied is not { Count: > 0 }) return;
        if (!new HashSet<string>(supplied, StringComparer.OrdinalIgnoreCase).SetEquals(configured))
            errors.Add($"scope.{field} [{string.Join(", ", supplied)}] differs from configured predicate '{fence.Name}' " +
                       $"([{string.Join(", ", configured)}]). This field is owned by the configuration: omit it or pass the configured value.");
    }

    private static void CheckFenceOwnedInts(string field, List<int>? supplied, List<int> configured,
        ProviderPredicateDefinition fence, List<string> errors)
    {
        if (supplied is not { Count: > 0 }) return;
        if (!new HashSet<int>(supplied).SetEquals(configured))
            errors.Add($"scope.{field} [{string.Join(", ", supplied)}] differs from configured predicate '{fence.Name}' " +
                       $"([{string.Join(", ", configured)}]). This field is owned by the configuration: omit it or pass the configured value.");
    }

    private static string DescribeExcludes(ProviderPredicateDefinition p) =>
        p.Excludes.Count == 0 ? "" : $", excludes {string.Join(", ", p.Excludes)}";

    private static InlineScopeResolution Fail(string error) => new(null, null, new[] { error });
}
