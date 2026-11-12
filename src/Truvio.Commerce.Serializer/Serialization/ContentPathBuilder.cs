using Dynamicweb.Content;
using Truvio.Commerce.Serializer.Configuration;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Builds the predicate-space content path ("/Parent/Child") for a DW page by walking its
/// parent chain. This is the path predicates and excludes are authored against.
/// </summary>
internal static class ContentPathBuilder
{
    public static string BuildContentPath(Page page)
    {
        var segments = new List<string>();
        var current = page;
        while (current != null)
        {
            segments.Insert(0, current.MenuText ?? string.Empty);
            current = current.ParentPageId > 0
                ? Services.Pages.GetPage(current.ParentPageId)
                : null;
        }
        return "/" + string.Join("/", segments);
    }

    /// <summary>
    /// Content ownership R3: the context a predicate is matched against for <paramref name="page"/>.
    /// A language-layer page (MasterPageId &gt; 0) is matched as its master: the master's path,
    /// GUID, navigation tag, item type and ancestor GUIDs. Other pages use their own, with
    /// <paramref name="ownPath"/> / <paramref name="ownAncestors"/> when the caller already has
    /// them (the serialize walk), else walked from the parent chain. Falls back to the page's own
    /// values when the master cannot be read.
    /// </summary>
    public static ContentPageContext BuildPredicateContext(Page page, string? ownPath = null,
        IReadOnlyList<Guid>? ownAncestors = null)
    {
        if (page.MasterPageId > 0)
        {
            try
            {
                var master = Services.Pages.GetPage(page.MasterPageId);
                if (master != null)
                    return FromChain(master);
            }
            catch
            {
                // fall through to the page's own identity
            }
        }

        if (ownPath is null || ownAncestors is null)
            return FromChain(page);

        return new ContentPageContext
        {
            Path = ownPath,
            PageUniqueId = page.UniqueId,
            NavigationTag = page.NavigationTag,
            ItemType = page.ItemType,
            AncestorUniqueIds = ownAncestors
        };
    }

    private static ContentPageContext FromChain(Page page)
    {
        var segments = new List<string>();
        var ancestors = new List<Guid>();
        var current = page;
        var first = true;
        while (current != null)
        {
            segments.Insert(0, current.MenuText ?? string.Empty);
            if (!first && current.UniqueId != Guid.Empty)
                ancestors.Add(current.UniqueId);
            first = false;
            current = current.ParentPageId > 0 ? Services.Pages.GetPage(current.ParentPageId) : null;
        }

        return new ContentPageContext
        {
            Path = "/" + string.Join("/", segments),
            PageUniqueId = page.UniqueId,
            NavigationTag = page.NavigationTag,
            ItemType = page.ItemType,
            AncestorUniqueIds = ancestors
        };
    }
}
