using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Reporting;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Queries;

public sealed class SerializerSettingsQuery : DataQueryModelBase<SerializerSettingsModel>
{
    /// <summary>Coverage counting walks the page tree — skip with a note above this size.</summary>
    private const int CoveragePageCap = 2000;

    public override SerializerSettingsModel? GetModel()
    {
        var configPath = ConfigPathResolver.FindConfigFile();
        if (configPath == null)
        {
            EnsureSerializerFolderWithStarterExample();
            return new SerializerSettingsModel { NeedsSetup = true };
        }

        var config = ConfigLoader.Load(configPath);

        var relativePath = configPath;
        var wwwrootMarker = Path.DirectorySeparatorChar + "wwwroot" + Path.DirectorySeparatorChar;
        var idx = configPath.IndexOf(wwwrootMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            relativePath = configPath[(idx + wwwrootMarker.Length)..];

        var replaceCount = config.Predicates.Count(p => p.Mode == SerializerMode.Replace);
        var mergeCount = config.Predicates.Count(p => p.Mode == SerializerMode.Merge);

        return new SerializerSettingsModel
        {
            OutputDirectory = config.OutputDirectory,
            ReplaceOutputSubfolder = config.ReplaceOutputSubfolder,
            MergeOutputSubfolder = config.MergeOutputSubfolder,
            ShowMergeIndicators = config.ShowMergeIndicators,
            ShowReplaceIndicators = config.ShowReplaceIndicators,
            ConfigFilePath = relativePath,
            ItemTypeExcludesSummary = SummarizeExcludes(config.ExcludeFieldsByItemType,
                "field", "No per-item-type field excludes configured — every field of every item type syncs."),
            XmlExcludesSummary = SummarizeExcludes(config.ExcludeXmlElementsByType,
                "setting", "No embedded-XML excludes configured — module settings and provider parameters sync in full."),
            PredicatesSummary = (replaceCount + mergeCount) == 0
                ? "No predicates configured. Nothing will be synced."
                : $"{replaceCount} replace predicate(s), {mergeCount} merge predicate(s) configured. Manage via the Predicates sub-node.",
            LastRunsSummary = BuildLastRunsSummary(configPath),
            CoverageSummary = BuildCoverageSummary(config),
            NeedsSetup = config.Predicates.Count == 0
        };
    }

    /// <summary>
    /// First-open self-initialization on an environment without a configuration: create
    /// Files/System/Serializer/ and drop the embedded Swift starter as
    /// swift-starter.example.json so the folder is visible in the file manager with a
    /// ready-to-copy example. The .example suffix keeps ConfigPathResolver from picking it
    /// up as the live config. Best-effort — a read-only Files area never breaks the screen.
    /// The nupkg also ships this file via its Files/ folder for app-store installs; this
    /// covers installs where that extraction doesn't happen (e.g. assembly-only deploys).
    /// </summary>
    private static void EnsureSerializerFolderWithStarterExample()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPathResolver.DefaultPath)!;
            Directory.CreateDirectory(dir);
            var examplePath = Path.Combine(dir, "swift-starter.example.json");
            if (!File.Exists(examplePath))
                File.WriteAllText(examplePath, Commands.ApplySwiftStarterCommand.ReadEmbeddedStarter());
        }
        catch
        {
            // Best-effort; the Get-started actions work without the example on disk.
        }
    }

    /// <summary>
    /// Compact inventory of a by-type exclusion dict: every type with a non-empty list and
    /// its count, e.g. "eCom_CartV2 (20 settings), UserAuthentication (6 settings)".
    /// Types discovered with empty lists (nothing excluded) are reported as a tail count.
    /// </summary>
    private static string SummarizeExcludes(
        IReadOnlyDictionary<string, List<string>> dict, string noun, string emptyMessage)
    {
        var withExcludes = dict
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} ({kv.Value.Count} {(kv.Value.Count == 1 ? noun : noun + "s")})")
            .ToList();

        if (withExcludes.Count == 0)
            return emptyMessage;

        var emptyCount = dict.Count(kv => kv.Value.Count == 0);
        var tail = emptyCount > 0 ? $" {emptyCount} more type(s) known with nothing excluded." : "";
        return $"These stay local per environment: {string.Join(", ", withExcludes)}.{tail}";
    }

    /// <summary>"Last replace received: … · Last merge received: …" from the run logs.</summary>
    private static string BuildLastRunsSummary(string configPath)
    {
        try
        {
            var logDir = LastRunResolver.GetLogDir(configPath);
            return $"{Format(LastRunResolver.FindLastReceived(logDir, "replace"), "Last replace received")} · " +
                   Format(LastRunResolver.FindLastReceived(logDir, "merge"), "Last merge received");
        }
        catch
        {
            return "";
        }

        static string Format(LogFileSummary? summary, string label) => summary is null
            ? $"{label}: never"
            : $"{label}: {summary.Timestamp.ToLocalTime():dd MMM yyyy HH:mm} " +
              $"(created {summary.TotalCreated}, updated {summary.TotalUpdated}, failed {summary.TotalFailed})";
    }

    /// <summary>
    /// One-line coverage picture: per content area, how many pages replace / merge / are
    /// unmanaged; plus SqlTable predicate counts per mode. Page counting walks the live
    /// tree through the same coverage evaluators as the tree icons — best-effort, and
    /// skipped with a note above <see cref="CoveragePageCap"/> pages.
    /// </summary>
    private static string BuildCoverageSummary(SerializerConfiguration config)
    {
        try
        {
            var contentPredicates = config.Predicates
                .Where(p => string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sqlReplace = config.Predicates.Count(p =>
                string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase) && p.Mode == SerializerMode.Replace);
            var sqlMerge = config.Predicates.Count(p =>
                string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase) && p.Mode == SerializerMode.Merge);

            var parts = new List<string>();

            foreach (var areaId in contentPredicates.Select(p => p.AreaId).Where(id => id > 0).Distinct().OrderBy(id => id))
            {
                var replaceEval = new ContentCoverageEvaluator(
                    contentPredicates.Where(p => p.Mode == SerializerMode.Replace && p.AreaId == areaId));
                var mergeEval = new ContentCoverageEvaluator(
                    contentPredicates.Where(p => p.Mode == SerializerMode.Merge && p.AreaId == areaId), "merge");

                int replacePages = 0, mergePages = 0, unmanaged = 0, total = 0;
                var capped = false;

                void Walk(Dynamicweb.Content.Page page, string path, IReadOnlyList<Guid> ancestors)
                {
                    if (capped) return;
                    if (++total > CoveragePageCap) { capped = true; return; }

                    // Content ownership R3: the same page context serialize matches against.
                    var context = Truvio.Commerce.Serializer.Serialization.ContentPathBuilder
                        .BuildPredicateContext(page, path, ancestors);
                    if (replaceEval.GetManagingPredicateNames(context, areaId).Count > 0)
                        replacePages++;
                    else if (mergeEval.GetManagingPredicateNames(context, areaId).Count > 0)
                        mergePages++;
                    else
                        unmanaged++;

                    var childAncestors = ancestors.Append(page.UniqueId).ToList();
                    foreach (var child in Dynamicweb.Content.Services.Pages.GetPagesByParentID(page.ID))
                        Walk(child, path + "/" + child.MenuText, childAncestors);
                }

                foreach (var root in Dynamicweb.Content.Services.Pages.GetRootPagesForArea(areaId))
                    Walk(root, "/" + root.MenuText, Array.Empty<Guid>());

                parts.Add(capped
                    ? $"Area {areaId}: more than {CoveragePageCap} pages — counts skipped"
                    : $"Area {areaId}: {replacePages} pages replace, {mergePages} merge, {unmanaged} unmanaged");
            }

            parts.Add($"Tables: {sqlReplace} replace, {sqlMerge} merge");
            return string.Join(" · ", parts) + ".";
        }
        catch
        {
            // Coverage is informational; never break the settings screen over it.
            return "";
        }
    }
}
