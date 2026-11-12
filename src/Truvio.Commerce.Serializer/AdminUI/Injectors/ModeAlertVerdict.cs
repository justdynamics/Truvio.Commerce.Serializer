using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.AdminUI.Injectors;

/// <summary>What an editing screen edits: the page's own settings, its body, one grid row or one paragraph.</summary>
internal enum ModeAlertScope
{
    /// <summary>The page document (page properties editor).</summary>
    Page,
    /// <summary>The page's rows and paragraphs as a whole (visual editor).</summary>
    Body,
    /// <summary>One grid row (grid-row dialog).</summary>
    GridRow,
    /// <summary>One paragraph (paragraph dialog).</summary>
    Paragraph
}

/// <summary>
/// The document an editing screen is open on, by the GUIDs <c>ownershipOverrides</c> are keyed
/// with. <see cref="MasterPageGuid"/> and <see cref="MasterParagraphGuid"/> are the master's GUIDs
/// of a language-layer page or paragraph (an override entry names the master and covers every layer).
/// </summary>
internal sealed record ModeAlertTarget
{
    public ModeAlertScope Scope { get; init; } = ModeAlertScope.Page;
    public Guid PageUniqueId { get; init; }
    public Guid? MasterPageGuid { get; init; }
    public Guid GridRowUniqueId { get; init; }
    public Guid ParagraphUniqueId { get; init; }
    public Guid? MasterParagraphGuid { get; init; }
}

/// <summary>What the next deserialize does with the edited document on this host.</summary>
internal enum ModeAlertKind
{
    /// <summary>No predicate manages the document.</summary>
    None,
    /// <summary>Every managing predicate stamps it <c>onExisting: keep</c>: created once, never written again.</summary>
    Seeded,
    /// <summary>At least one managing predicate overwrites it (source wins).</summary>
    Overwritten,
    /// <summary>No predicate overwrites it and at least one fills its empty fields (destination wins).</summary>
    Filled
}

/// <summary>
/// The keep / overwrite / fill verdict of an editing-screen mode alert, and the predicates it
/// comes from.
/// <para>
/// The verdict is read off the header serialize stamps on the edited document, through the same
/// <see cref="OwnershipStampPolicy"/>: a predicate's <c>mode</c> and <c>onExisting</c>, its
/// <c>bodyOnExisting</c> for rows and paragraphs, and its <c>ownershipOverrides</c> entries for
/// the page, the grid row and the paragraph (content ownership R1 and R4), most specific first.
/// Reading only the predicate's own <c>onExisting</c> gets exactly the overridden pages wrong.
/// </para>
/// </summary>
internal sealed record ModeAlertVerdict(ModeAlertKind Kind, IReadOnlyList<string> PredicateNames)
{
    public static readonly ModeAlertVerdict None = new(ModeAlertKind.None, Array.Empty<string>());

    /// <param name="managing">The predicates that include the page itself.</param>
    /// <param name="target">The edited document.</param>
    public static ModeAlertVerdict Resolve(IEnumerable<ProviderPredicateDefinition> managing, ModeAlertTarget target)
    {
        var documents = managing
            .Select(p => (p.Name, Lifecycle: LifecycleOf(p, target)))
            .ToList();
        if (documents.Count == 0)
            return None;

        IReadOnlyList<string> Names(ExistingDocumentLifecycle lifecycle) =>
            documents.Where(d => d.Lifecycle == lifecycle).Select(d => d.Name).Distinct().ToList();

        // Source wins as soon as one predicate overwrites; keep only when every one keeps.
        if (documents.Any(d => d.Lifecycle == ExistingDocumentLifecycle.Overwrite))
            return new ModeAlertVerdict(ModeAlertKind.Overwritten, Names(ExistingDocumentLifecycle.Overwrite));
        if (documents.Any(d => d.Lifecycle == ExistingDocumentLifecycle.Fill))
            return new ModeAlertVerdict(ModeAlertKind.Filled, Names(ExistingDocumentLifecycle.Fill));
        return new ModeAlertVerdict(ModeAlertKind.Seeded, Names(ExistingDocumentLifecycle.Keep));
    }

    /// <summary>The header one predicate stamps on the edited document.</summary>
    internal static DocumentHeader HeaderOf(ProviderPredicateDefinition predicate, ModeAlertTarget target)
    {
        var policy = OwnershipStampPolicy.FromPredicate(predicate);
        var page = policy.PageHeader(target.PageUniqueId, target.MasterPageGuid);
        if (target.Scope == ModeAlertScope.Page)
            return page;

        var body = policy.BodyHeader(target.PageUniqueId, target.MasterPageGuid, page);
        if (target.Scope == ModeAlertScope.Body)
            return body;

        // A paragraph outside a grid row inherits the body header; inside one, its row's.
        var row = policy.GridRowHeader(target.GridRowUniqueId, body);
        return target.Scope == ModeAlertScope.GridRow
            ? row
            : policy.ParagraphHeader(target.ParagraphUniqueId, target.MasterParagraphGuid, row);
    }

    private static ExistingDocumentLifecycle LifecycleOf(ProviderPredicateDefinition predicate, ModeAlertTarget target) =>
        DocumentHeader.LifecycleFor(HeaderOf(predicate, target),
            predicate.Mode == SerializerMode.Merge ? ConflictStrategy.DestinationWins : ConflictStrategy.SourceWins);
}
