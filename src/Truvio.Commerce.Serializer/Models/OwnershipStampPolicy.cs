using Truvio.Commerce.Serializer.Configuration;

namespace Truvio.Commerce.Serializer.Models;

/// <summary>
/// Decides the ownership header of every document a Content predicate serializes (content
/// ownership R1 and R4). Precedence, most specific first:
/// <list type="number">
/// <item>paragraph: its own <c>ownershipOverrides</c> entry, then its grid row's;</item>
/// <item>grid row: its own entry;</item>
/// <item>rows and paragraphs of a page: the page entry's <c>bodyOnExisting</c>, then the
/// predicate's <c>bodyOnExisting</c>;</item>
/// <item>everything else: the page's header (page entry, then the predicate's <c>mode</c> and
/// <c>onExisting</c>).</item>
/// </list>
/// An override sets the mode and the lifecycle independently; a key it leaves out is inherited
/// from the next rule down. A policy built from a predicate without these keys stamps the plain
/// mode header on every document, as before.
/// </summary>
public sealed class OwnershipStampPolicy
{
    private readonly Dictionary<Guid, OwnershipOverride> _pages = new();
    private readonly Dictionary<Guid, OwnershipOverride> _gridRows = new();
    private readonly Dictionary<Guid, OwnershipOverride> _paragraphs = new();

    public SerializerMode Mode { get; }
    public ExistingDocumentLifecycle? OnExisting { get; }
    public ExistingDocumentLifecycle? BodyOnExisting { get; }

    private OwnershipStampPolicy(SerializerMode mode, ExistingDocumentLifecycle? onExisting,
        ExistingDocumentLifecycle? bodyOnExisting, IEnumerable<OwnershipOverride>? overrides)
    {
        Mode = mode;
        OnExisting = onExisting;
        BodyOnExisting = bodyOnExisting;
        foreach (var o in overrides ?? Enumerable.Empty<OwnershipOverride>())
        {
            // Later entries win, the same rule the config applies everywhere else.
            if (o.PageUniqueId is { } page && page != Guid.Empty) _pages[page] = o;
            else if (o.GridRowUniqueId is { } row && row != Guid.Empty) _gridRows[row] = o;
            else if (o.ParagraphUniqueId is { } para && para != Guid.Empty) _paragraphs[para] = o;
        }
    }

    /// <summary>Mode-only policy: every document gets <c>ownership: { mode }</c>.</summary>
    public static OwnershipStampPolicy ForMode(SerializerMode mode) => new(mode, null, null, null);

    /// <summary>Policy of one Content predicate.</summary>
    public static OwnershipStampPolicy FromPredicate(ProviderPredicateDefinition predicate) => new(
        predicate.Mode,
        DocumentHeader.ParseLifecycle(predicate.OnExisting),
        DocumentHeader.ParseLifecycle(predicate.BodyOnExisting),
        predicate.OwnershipOverrides);

    public DocumentHeader PageHeader(SerializedPage page)
    {
        var o = Find(_pages, page.PageUniqueId, page.MasterPageGuid);
        return DocumentHeader.For(
            ParseMode(o?.Mode) ?? Mode,
            DocumentHeader.ParseLifecycle(o?.OnExisting) ?? OnExisting);
    }

    /// <summary>The header a page's rows and paragraphs inherit when they have no entry of their own.</summary>
    public DocumentHeader BodyHeader(SerializedPage page, DocumentHeader pageHeader)
    {
        var o = Find(_pages, page.PageUniqueId, page.MasterPageGuid);
        var body = DocumentHeader.ParseLifecycle(o?.BodyOnExisting) ?? BodyOnExisting;
        return body is null
            ? pageHeader
            : DocumentHeader.For(DocumentHeader.ParseMode(pageHeader) ?? Mode, body);
    }

    public DocumentHeader GridRowHeader(SerializedGridRow row, DocumentHeader inherited) =>
        Apply(Find(_gridRows, row.Id, null), inherited);

    public DocumentHeader ParagraphHeader(SerializedParagraph paragraph, DocumentHeader inherited) =>
        Apply(Find(_paragraphs, paragraph.ParagraphUniqueId, MasterParagraphGuid(paragraph)), inherited);

    private DocumentHeader Apply(OwnershipOverride? o, DocumentHeader inherited)
    {
        if (o is null)
            return inherited;
        return DocumentHeader.For(
            ParseMode(o.Mode) ?? DocumentHeader.ParseMode(inherited) ?? Mode,
            DocumentHeader.ParseLifecycle(o.OnExisting) ?? DocumentHeader.ParseOnExisting(inherited));
    }

    private static OwnershipOverride? Find(Dictionary<Guid, OwnershipOverride> map, Guid own, Guid? master)
    {
        if (map.Count == 0) return null;
        if (own != Guid.Empty && map.TryGetValue(own, out var hit)) return hit;
        if (master is { } m && m != Guid.Empty && map.TryGetValue(m, out var masterHit)) return masterHit;
        return null;
    }

    /// <summary>A language-layer paragraph carries its master's GUID in <c>MasterParagraphGuid</c>.</summary>
    private static Guid? MasterParagraphGuid(SerializedParagraph paragraph) =>
        paragraph.Fields.TryGetValue("MasterParagraphGuid", out var raw)
        && Guid.TryParse(raw?.ToString(), out var guid)
            ? guid
            : null;

    private static SerializerMode? ParseMode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<SerializerMode>(value.Trim(), ignoreCase: true, out var m)
            ? m
            : null;
}
