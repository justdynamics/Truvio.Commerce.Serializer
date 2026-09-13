using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Manifest bookkeeping for inline-scope runs. A scoped serialize writes a subset of a mode's
/// files, so it must neither replace the mode manifest with its own entries nor clean the files
/// other entries own; a scoped deserialize dispatches only the entries, or the part of an entry,
/// that fall inside the scope.
/// </summary>
public static class ScopedManifest
{
    /// <summary>
    /// Folds the entries of a scoped serialize into the existing mode manifest. An existing entry
    /// with the same id, or one that covers the new entry (Content: same area and the new path is
    /// under the existing path; SqlTable: same table), keeps its shape and gains the new files.
    /// Anything else is appended. Files are never removed here: stale-file cleanup is left to the
    /// next full serialize of the mode.
    /// </summary>
    public static List<ManifestEntry> Merge(IReadOnlyList<ManifestEntry>? existing, IEnumerable<ManifestEntry> scoped)
    {
        var merged = (existing ?? Array.Empty<ManifestEntry>()).ToList();

        foreach (var entry in scoped)
        {
            var index = merged.FindIndex(e => string.Equals(e.EntryId, entry.EntryId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                index = merged.FindIndex(e => Covers(e, entry));

            if (index < 0)
            {
                merged.Add(entry);
                continue;
            }

            var files = merged[index].Files
                .Concat(entry.Files)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged[index] = merged[index] with { Files = files };
        }

        return merged;
    }

    private static bool Covers(ManifestEntry existing, ManifestEntry scoped) => (existing, scoped) switch
    {
        (ContentEntry e, ContentEntry s) => e.AreaId == s.AreaId
                                            && ContentPredicate.IsUnderPath(NormalizePath(s.Path), NormalizePath(e.Path)),
        (SqlTableEntry e, SqlTableEntry s) => string.Equals(e.Table, s.Table, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>
    /// Selects what a scoped deserialize dispatches. SqlTable: the entries for the scope's table.
    /// Content: entries of the scope's area that lie inside the scope pass unchanged; entries that
    /// only partly overlap (the scope is a subtree of the entry, or a scope exclude cuts into it) are
    /// narrowed to the page files inside the scope, and their ancestors run as structural stubs.
    /// </summary>
    /// <param name="entries">Manifest entries of the mode.</param>
    /// <param name="scope">Effective scope predicate (after <see cref="InlineScopeResolver"/>).</param>
    /// <param name="readPagePaths">Returns (manifest file key of page.yml, content path) for every
    /// page of a Content entry's YAML tree. The content path is the menu-text chain from the area root.</param>
    public static List<ManifestEntry> SelectForScope(
        IReadOnlyList<ManifestEntry> entries,
        ProviderPredicateDefinition scope,
        Func<ContentEntry, IEnumerable<(string FileKey, string ContentPath)>> readPagePaths)
    {
        var selected = new List<ManifestEntry>();

        if (string.Equals(scope.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase))
        {
            selected.AddRange(entries.OfType<SqlTableEntry>()
                .Where(e => string.Equals(e.Table, scope.Table, StringComparison.OrdinalIgnoreCase)));
            return selected;
        }

        var scopePath = NormalizePath(scope.Path);
        var scopeFilter = new ContentPredicate(scope);

        foreach (var entry in entries.OfType<ContentEntry>().Where(e => e.AreaId == scope.AreaId))
        {
            var entryPath = NormalizePath(entry.Path);

            var entryInsideScope = ContentPredicate.IsUnderPath(entryPath, scopePath);
            var scopeInsideEntry = ContentPredicate.IsUnderPath(scopePath, entryPath);
            if (!entryInsideScope && !scopeInsideEntry)
                continue;

            var excludeCutsIn = scope.Excludes.Any(x =>
                ContentPredicate.IsUnderPath(NormalizePath(x), entryPath) || ContentPredicate.IsUnderPath(entryPath, NormalizePath(x)));

            var excludeFields = entry.ExcludeFields
                .Concat(scope.ExcludeFields)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entryInsideScope && !excludeCutsIn)
            {
                selected.Add(entry with { ExcludeFields = excludeFields });
                continue;
            }

            var entryFiles = new HashSet<string>(entry.Files, StringComparer.OrdinalIgnoreCase);
            var keep = readPagePaths(entry)
                .Where(p => entryFiles.Contains(p.FileKey) && scopeFilter.ShouldInclude(p.ContentPath, scope.AreaId))
                .Select(p => p.FileKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keep.Count == 0)
                continue;

            var narrowedPath = entryInsideScope ? entry.Path : scope.Path;
            selected.Add(entry with
            {
                EntryId = $"{entry.EntryId} [scope {scopePath}]",
                Path = narrowedPath,
                PageId = entryInsideScope ? entry.PageId : scope.PageId,
                Files = keep,
                ExcludeFields = excludeFields,
                StubUnlistedAncestors = true
            });
        }

        return selected;
    }

    /// <summary>
    /// Walks a YAML page tree and yields (manifest file key, content path) per page. The content
    /// path is the menu-text chain, the same coordinate system predicates are authored in.
    /// </summary>
    public static IEnumerable<(string FileKey, string ContentPath)> PagePaths(IEnumerable<SerializedPage> pages, string parentPath = "")
    {
        foreach (var page in pages)
        {
            var path = $"{parentPath}/{page.MenuText}";
            if (page.SourceFile is not null)
                yield return (page.SourceFile, path);
            foreach (var child in PagePaths(page.Children, path))
                yield return child;
        }
    }

    private static string NormalizePath(string? path) =>
        string.IsNullOrEmpty(path) ? "/" : path.Length > 1 ? path.TrimEnd('/') : path;
}
