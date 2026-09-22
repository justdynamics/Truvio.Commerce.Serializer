using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Pure computation of master-link restore operations for language-layer content.
/// Runs after page/paragraph writes (post-write pass, like InternalLinkResolver) so both
/// sides of every link exist on target — provided master-area predicates are ordered before
/// their language layers in the config.
/// </summary>
public static class MasterLinkRestorer
{
    /// <summary>A page whose MasterPageId must be set on target.</summary>
    public sealed record PageLinkUpdate(int TargetPageId, int TargetMasterPageId, string? MasterType);

    /// <summary>A paragraph whose MasterParagraphID / GlobalRecordPageID must be set on target.</summary>
    public sealed record ParagraphLinkUpdate(int TargetParagraphId, int? TargetMasterParagraphId, int? TargetGlobalRecordPageId);

    /// <summary>A link that could not be resolved (master not present on target yet).</summary>
    public sealed record UnresolvedLink(string Kind, Guid OwnerGuid, Guid MasterGuid);

    /// <summary>
    /// Walks the serialized page tree and pairs each page carrying a MasterPageGuid with
    /// its own target ID and its master's target ID via the full-DB GUID cache.
    /// </summary>
    public static (List<PageLinkUpdate> Updates, List<UnresolvedLink> Unresolved) ComputePageLinkUpdates(
        IEnumerable<SerializedPage> pages,
        IReadOnlyDictionary<Guid, int> pageGuidToTargetId)
    {
        var updates = new List<PageLinkUpdate>();
        var unresolved = new List<UnresolvedLink>();
        Visit(pages, page =>
        {
            if (!page.MasterPageGuid.HasValue)
                return;
            if (!pageGuidToTargetId.TryGetValue(page.PageUniqueId, out var ownId))
                return; // page itself failed to write — already reported by the write pass
            if (pageGuidToTargetId.TryGetValue(page.MasterPageGuid.Value, out var masterId))
                updates.Add(new PageLinkUpdate(ownId, masterId, page.MasterType));
            else
                unresolved.Add(new UnresolvedLink("page", page.PageUniqueId, page.MasterPageGuid.Value));
        });
        return (updates, unresolved);
    }

    /// <summary>
    /// Walks paragraphs (pages → grid rows → columns) and computes MasterParagraphID /
    /// GlobalRecordPageID restores from the MasterParagraphGuid / GlobalRecordPageGuid
    /// reference fields the mapper emitted.
    /// </summary>
    public static (List<ParagraphLinkUpdate> Updates, List<UnresolvedLink> Unresolved) ComputeParagraphLinkUpdates(
        IEnumerable<SerializedPage> pages,
        IReadOnlyDictionary<Guid, int> paragraphGuidToTargetId,
        IReadOnlyDictionary<Guid, int> pageGuidToTargetId)
    {
        var updates = new List<ParagraphLinkUpdate>();
        var unresolved = new List<UnresolvedLink>();
        VisitParagraphs(pages, para =>
        {
            var masterGuid = ReadGuidField(para.Fields, "MasterParagraphGuid");
            var globalPageGuid = ReadGuidField(para.Fields, "GlobalRecordPageGuid");
            if (masterGuid is null && globalPageGuid is null)
                return;
            if (!paragraphGuidToTargetId.TryGetValue(para.ParagraphUniqueId, out var ownId))
                return;

            int? masterId = null;
            if (masterGuid.HasValue)
            {
                if (paragraphGuidToTargetId.TryGetValue(masterGuid.Value, out var resolved))
                    masterId = resolved;
                else
                    unresolved.Add(new UnresolvedLink("paragraph", para.ParagraphUniqueId, masterGuid.Value));
            }

            int? globalPageId = null;
            if (globalPageGuid.HasValue)
            {
                if (pageGuidToTargetId.TryGetValue(globalPageGuid.Value, out var resolved))
                    globalPageId = resolved;
                else
                    unresolved.Add(new UnresolvedLink("globalRecordPage", para.ParagraphUniqueId, globalPageGuid.Value));
            }

            if (masterId.HasValue || globalPageId.HasValue)
                updates.Add(new ParagraphLinkUpdate(ownId, masterId, globalPageId));
        });
        return (updates, unresolved);
    }

    private static Guid? ReadGuidField(IReadOnlyDictionary<string, object> fields, string key)
    {
        if (fields.TryGetValue(key, out var raw) && raw is not null && Guid.TryParse(raw.ToString(), out var guid))
            return guid;
        return null;
    }

    private static void Visit(IEnumerable<SerializedPage> pages, Action<SerializedPage> action)
    {
        foreach (var page in pages)
        {
            action(page);
            Visit(page.Children, action);
        }
    }

    private static void VisitParagraphs(IEnumerable<SerializedPage> pages, Action<SerializedParagraph> action)
    {
        Visit(pages, page =>
        {
            foreach (var row in page.GridRows)
                foreach (var column in row.Columns)
                    foreach (var para in column.Paragraphs)
                        action(para);
            // Foundry #1315: page-level paragraphs (GridRowId 0) are in no column.
            foreach (var para in page.Paragraphs)
                action(para);
        });
    }
}
