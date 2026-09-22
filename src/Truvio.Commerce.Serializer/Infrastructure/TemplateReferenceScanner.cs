using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Phase 37-05 / TEMPLATE-01: walks serialized page trees and extracts every
/// template reference (page-layout cshtml, ItemType definition, grid-row definition)
/// along with the source page identifiers that triggered each reference. Emits
/// de-duplicated <see cref="TemplateReference"/> records whose
/// <see cref="TemplateReference.ReferencedBy"/> list accumulates across every
/// page that shares the same (kind, path).
/// </summary>
public class TemplateReferenceScanner
{
    /// <summary>
    /// Scan a list of root pages (and their nested children) and return a de-duplicated
    /// list of template references keyed by (kind, path).
    /// </summary>
    public List<TemplateReference> Scan(List<SerializedPage> rootPages)
    {
        var acc = new Dictionary<(string kind, string path), TemplateReference>();
        foreach (var p in rootPages)
            Walk(p, PagePath(p), acc);
        return acc.Values.ToList();
    }

    private static string PagePath(SerializedPage p)
    {
        if (!string.IsNullOrEmpty(p.UrlName)) return p.UrlName;
        if (!string.IsNullOrEmpty(p.MenuText)) return p.MenuText;
        return p.PageUniqueId.ToString();
    }

    private void Walk(
        SerializedPage page,
        string pageIdentifier,
        Dictionary<(string kind, string path), TemplateReference> acc)
    {
        if (!string.IsNullOrEmpty(page.Layout))
            AddRef(acc, "page-layout", page.Layout, pageIdentifier);

        if (!string.IsNullOrEmpty(page.ItemType))
            AddRef(acc, "item-type", page.ItemType, pageIdentifier);

        foreach (var row in page.GridRows)
        {
            if (!string.IsNullOrEmpty(row.DefinitionId))
                AddRef(acc, "grid-row", row.DefinitionId, pageIdentifier);

            // Grid rows can also carry ItemType (container fields) — track these too so
            // the baseline replace run has all referenced item-type xml files.
            if (!string.IsNullOrEmpty(row.ItemType))
                AddRef(acc, "item-type", row.ItemType, pageIdentifier);

            foreach (var col in row.Columns)
            {
                foreach (var para in col.Paragraphs)
                {
                    if (!string.IsNullOrEmpty(para.ItemType))
                        AddRef(acc, "item-type", para.ItemType, pageIdentifier);
                }
            }
        }

        // Foundry #1315: page-level paragraphs (GridRowId 0) carry item types too.
        foreach (var para in page.Paragraphs)
        {
            if (!string.IsNullOrEmpty(para.ItemType))
                AddRef(acc, "item-type", para.ItemType, pageIdentifier);
        }

        foreach (var child in page.Children)
            Walk(child, PagePath(child), acc);
    }

    private static void AddRef(
        Dictionary<(string kind, string path), TemplateReference> acc,
        string kind,
        string path,
        string referencedBy)
    {
        var key = (kind, path);
        if (!acc.TryGetValue(key, out var existing))
        {
            existing = new TemplateReference { Kind = kind, Path = path };
            acc[key] = existing;
        }

        if (!existing.ReferencedBy.Contains(referencedBy, StringComparer.OrdinalIgnoreCase))
            existing.ReferencedBy.Add(referencedBy);
    }
}
