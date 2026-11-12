namespace Truvio.Commerce.Serializer.Models;

/// <summary>
/// One entry of a Content predicate's <c>ownershipOverrides</c> list (content ownership R4): stamps
/// a single page, grid row or paragraph apart from the predicate's own <c>mode</c>,
/// <c>onExisting</c> and <c>bodyOnExisting</c>. Exactly one of the three GUIDs is set.
/// <code>
/// { "paragraphUniqueId": "…", "mode": "Merge", "onExisting": "keep" }      // a slot
/// { "pageUniqueId": "…", "onExisting": "overwrite", "bodyOnExisting": "keep" }  // a shell
/// </code>
/// Keys are the master's GUIDs: a language-layer page matches through its <c>masterPageGuid</c>
/// and a layer paragraph through its <c>MasterParagraphGuid</c>, so one entry covers every layer.
/// </summary>
public sealed record OwnershipOverride
{
    /// <summary>A page (its own document; with <see cref="BodyOnExisting"/> also its rows and paragraphs).</summary>
    public Guid? PageUniqueId { get; init; }

    /// <summary>A grid row, and the paragraphs inside it unless a paragraph has its own entry.</summary>
    public Guid? GridRowUniqueId { get; init; }

    /// <summary>A single paragraph.</summary>
    public Guid? ParagraphUniqueId { get; init; }

    /// <summary>"Replace" or "Merge"; null keeps the mode the document would otherwise get.</summary>
    public string? Mode { get; init; }

    /// <summary>"overwrite", "fill" or "keep"; null keeps the lifecycle the document would otherwise get.</summary>
    public string? OnExisting { get; init; }

    /// <summary>Page entries only: the lifecycle of the page's grid rows and paragraphs.</summary>
    public string? BodyOnExisting { get; init; }
}
