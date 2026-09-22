namespace Truvio.Commerce.Serializer.Models;

public record SerializedPage
{
    /// <summary>Ownership header (<c>ownership</c>): the mode this document was serialized under. Deserialize honors it per page.</summary>
    [YamlDotNet.Serialization.YamlMember(Order = -1)]
    public DocumentHeader? Ownership { get; init; }

    public required Guid PageUniqueId { get; init; }
    public int? SourcePageId { get; init; }
    public required string Name { get; init; }
    public required string MenuText { get; init; }
    public required string UrlName { get; init; }
    public required int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public string? ItemType { get; init; }
    public string? Layout { get; init; }
    public bool LayoutApplyToSubPages { get; init; }
    public bool IsFolder { get; init; }
    /// <summary>
    /// DW page-preset/template flag. Must round-trip: DW's SavePage forces
    /// MenuText = item Title for every NON-template page, so a preset whose menu text
    /// diverges from its item Title (e.g. Swift's "Home preset" with Title "Home") gets
    /// silently renamed on the first post-write save unless this flag is preserved.
    /// </summary>
    public bool IsTemplate { get; init; }
    /// <summary>
    /// Ancestor pass-through for deep-rooted predicates (path more than one level below the
    /// area root, e.g. "/Navigation/Footer Navigation/Help and info"): the page is NOT part
    /// of the predicate's content — it is emitted scalars-only (no grid rows, paragraphs or
    /// permissions) so the YAML directory nesting can carry the subtree's parentage. On
    /// target, a stub that already exists is never link-resolved (its fields were written
    /// and resolved by the owning predicate's pass).
    /// </summary>
    public bool IsStructuralStub { get; init; }
    /// <summary>
    /// Read-time only (never persisted): the page.yml path relative to the mode root in
    /// manifest-file format ("_content/&lt;Area&gt;/.../page.yml"). Set by FileSystemStore so
    /// the deserializer can prune the merged on-disk area tree down to ONE manifest entry's
    /// files — multiple predicates of the same mode share the area directory, and without
    /// pruning every entry re-deserializes (and re-link-resolves) every sibling's content.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string? SourceFile { get; init; }
    public string? TreeSection { get; init; }
    public string? NavigationTag { get; init; }
    public string? ShortCut { get; init; }
    public bool Hidden { get; init; }
    public bool Allowclick { get; init; } = true;
    public bool Allowsearch { get; init; } = true;
    public bool ShowInSitemap { get; init; } = true;
    public bool ShowInLegend { get; init; } = true;
    public int SslMode { get; init; }
    public string? ColorSchemeId { get; init; }
    public string? ExactUrl { get; init; }
    public string? ContentType { get; init; }
    public string? TopImage { get; init; }
    public string? DisplayMode { get; init; }
    public DateTime? ActiveFrom { get; init; }
    public DateTime? ActiveTo { get; init; }
    public int PermissionType { get; init; }
    /// <summary>
    /// GUID of the master page when this page is a language-layer copy (Page.MasterPageId > 0).
    /// The master page lives in the master area; deserialize resolves it back to the target's
    /// numeric page ID in the post-write link pass.
    /// </summary>
    public Guid? MasterPageGuid { get; init; }
    /// <summary>DW MasterType (None/Lock/Inherit) for language-layer pages; null when not a language copy.</summary>
    public string? MasterType { get; init; }
    public SerializedSeoSettings? Seo { get; init; }
    public SerializedUrlSettings? UrlSettings { get; init; }
    public SerializedVisibilitySettings? Visibility { get; init; }
    public SerializedNavigationSettings? NavigationSettings { get; init; }
    public DateTime? CreatedDate { get; init; }
    public DateTime? UpdatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public Dictionary<string, object> Fields { get; init; } = new();
    public Dictionary<string, object> PropertyFields { get; init; } = new();
    public List<SerializedPermission> Permissions { get; init; } = new();
    public List<SerializedGridRow> GridRows { get; init; } = new();
    /// <summary>
    /// Foundry #1315: paragraphs placed DIRECTLY on the page (<c>ParagraphGridRowId = 0</c>),
    /// beside the grid rows rather than inside one. Stock Swift 2 service pages (the search
    /// type-ahead responder, Variant Selector Service, Favorites list service) are built this
    /// way and are rendered through <c>Model.Placeholder("dwcontent")</c>, not through the grid.
    ///
    /// <para>A page-level list — not a synthetic "grid row 0" — because a grid row is a real
    /// DW record: a synthetic one would be CREATED on the target by
    /// <c>ContentDeserializer.DeserializeGridRow</c>, wrapping the paragraphs in markup the
    /// stock page does not have, and it would collide with the 1..N SortOrder renumbering the
    /// serializer applies to real rows. An extra <c>paragraphs:</c> list is additive: documents
    /// written before this change simply carry none.</para>
    /// </summary>
    public List<SerializedParagraph> Paragraphs { get; init; } = new();
    public List<SerializedPage> Children { get; init; } = new();
}
