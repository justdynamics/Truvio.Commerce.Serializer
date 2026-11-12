using Truvio.Commerce.Serializer.AdminUI.Injectors;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.AdminUI;

/// <summary>
/// The editing-screen mode alert says what the next deserialize does with the document on screen.
/// It must agree with the header serialize stamps on that document: the predicate's keys AND its
/// ownershipOverrides (content ownership R4), for the page, its body, a grid row and a paragraph.
/// </summary>
public class ModeAlertVerdictTests
{
    private static readonly Guid Page = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MasterPage = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Row = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Slot = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MasterSlot = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static ProviderPredicateDefinition Predicate(string name, SerializerMode mode,
        string? onExisting = null, string? bodyOnExisting = null, params OwnershipOverride[] overrides) => new()
    {
        Name = name,
        ProviderType = "Content",
        Mode = mode,
        AreaId = 3,
        Path = "/",
        OnExisting = onExisting,
        BodyOnExisting = bodyOnExisting,
        OwnershipOverrides = overrides.ToList()
    };

    private static ModeAlertTarget OnPage(ModeAlertScope scope = ModeAlertScope.Page) => new()
    {
        Scope = scope,
        PageUniqueId = Page,
        GridRowUniqueId = scope is ModeAlertScope.GridRow or ModeAlertScope.Paragraph ? Row : Guid.Empty,
        ParagraphUniqueId = scope is ModeAlertScope.Paragraph ? Slot : Guid.Empty
    };

    private static ModeAlertVerdict Resolve(ModeAlertTarget target, params ProviderPredicateDefinition[] managing) =>
        ModeAlertVerdict.Resolve(managing, target);

    // -------------------------------------------------------------------------
    // Without overrides: the predicate's own keys
    // -------------------------------------------------------------------------

    [Fact]
    public void NoManagingPredicate_IsNone() =>
        Assert.Equal(ModeAlertKind.None, Resolve(OnPage()).Kind);

    [Theory]
    [InlineData(SerializerMode.Replace, null, "Overwritten")]
    [InlineData(SerializerMode.Merge, null, "Filled")]
    [InlineData(SerializerMode.Replace, "keep", "Seeded")]
    [InlineData(SerializerMode.Merge, "keep", "Seeded")]
    [InlineData(SerializerMode.Replace, "fill", "Filled")]
    [InlineData(SerializerMode.Merge, "overwrite", "Overwritten")]
    public void PredicateKeys_DecideThePageVerdict(SerializerMode mode, string? onExisting, string expected)
    {
        var verdict = Resolve(OnPage(), Predicate("system", mode, onExisting));

        Assert.Equal(expected, verdict.Kind.ToString());
        Assert.Equal(new[] { "system" }, verdict.PredicateNames);
    }

    [Fact]
    public void OnePredicateOverwrites_OneKeeps_SourceWins()
    {
        var verdict = Resolve(OnPage(),
            Predicate("seed", SerializerMode.Merge, "keep"),
            Predicate("system", SerializerMode.Replace));

        Assert.Equal(ModeAlertKind.Overwritten, verdict.Kind);
        Assert.Equal(new[] { "system" }, verdict.PredicateNames);
    }

    // -------------------------------------------------------------------------
    // R4: ownershipOverrides on the page
    // -------------------------------------------------------------------------

    [Fact]
    public void PageOverrideKeep_OnAReplacePredicate_IsSeeded_NotOverwritten()
    {
        // The review finding: the predicate says overwrite, the override keeps exactly this page.
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { PageUniqueId = Page, OnExisting = "keep" });

        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(), predicate).Kind);
        // Another page of the same predicate keeps the predicate's verdict.
        Assert.Equal(ModeAlertKind.Overwritten,
            Resolve(OnPage() with { PageUniqueId = Guid.NewGuid() }, predicate).Kind);
    }

    [Fact]
    public void PageOverrideOverwrite_OnAKeepPredicate_IsOverwritten_NotSeeded()
    {
        var predicate = Predicate("editorial", SerializerMode.Replace, "keep", null,
            new OwnershipOverride { PageUniqueId = Page, OnExisting = "overwrite" });

        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(), predicate).Kind);
    }

    [Fact]
    public void PageOverrideModeMerge_OnAReplacePredicate_IsFilled()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { PageUniqueId = Page, Mode = "Merge" });

        Assert.Equal(ModeAlertKind.Filled, Resolve(OnPage(), predicate).Kind);
    }

    [Fact]
    public void PageOverride_KeyedByTheMasterGuid_CoversALanguageLayerPage()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { PageUniqueId = MasterPage, OnExisting = "keep" });

        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage() with { MasterPageGuid = MasterPage }, predicate).Kind);
        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(), predicate).Kind);
    }

    // -------------------------------------------------------------------------
    // R4: the body, a grid row, a paragraph
    // -------------------------------------------------------------------------

    [Fact]
    public void Shell_PageOverwritten_BodyKept()
    {
        // { pageUniqueId, onExisting: overwrite, bodyOnExisting: keep }: the page properties are
        // overwritten, the rows and paragraphs an editor fills are kept.
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { PageUniqueId = Page, OnExisting = "overwrite", BodyOnExisting = "keep" });

        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(ModeAlertScope.Page), predicate).Kind);
        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.Body), predicate).Kind);
        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.GridRow), predicate).Kind);
        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.Paragraph), predicate).Kind);
    }

    [Fact]
    public void PredicateBodyOnExisting_AppliesToRowsAndParagraphs_NotToThePage()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, "keep");

        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(ModeAlertScope.Page), predicate).Kind);
        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.Paragraph), predicate).Kind);
    }

    [Fact]
    public void Slot_ParagraphOverrideKeep_OnlyThatParagraphIsSeeded()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { ParagraphUniqueId = Slot, Mode = "Merge", OnExisting = "keep" });

        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.Paragraph), predicate).Kind);
        Assert.Equal(ModeAlertKind.Overwritten,
            Resolve(OnPage(ModeAlertScope.Paragraph) with { ParagraphUniqueId = Guid.NewGuid() }, predicate).Kind);
        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(ModeAlertScope.GridRow), predicate).Kind);
        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(ModeAlertScope.Page), predicate).Kind);
    }

    [Fact]
    public void GridRowOverride_CoversTheRowAndItsParagraphs_UnlessAParagraphHasItsOwn()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { GridRowUniqueId = Row, OnExisting = "keep" },
            new OwnershipOverride { ParagraphUniqueId = Slot, OnExisting = "overwrite" });

        Assert.Equal(ModeAlertKind.Seeded, Resolve(OnPage(ModeAlertScope.GridRow), predicate).Kind);
        Assert.Equal(ModeAlertKind.Seeded,
            Resolve(OnPage(ModeAlertScope.Paragraph) with { ParagraphUniqueId = Guid.NewGuid() }, predicate).Kind);
        Assert.Equal(ModeAlertKind.Overwritten, Resolve(OnPage(ModeAlertScope.Paragraph), predicate).Kind);
    }

    [Fact]
    public void ParagraphOverride_KeyedByTheMasterGuid_CoversALayerParagraph()
    {
        var predicate = Predicate("system", SerializerMode.Replace, null, null,
            new OwnershipOverride { ParagraphUniqueId = MasterSlot, OnExisting = "keep" });

        Assert.Equal(ModeAlertKind.Seeded,
            Resolve(OnPage(ModeAlertScope.Paragraph) with { MasterParagraphGuid = MasterSlot }, predicate).Kind);
    }

    [Fact]
    public void TheVerdict_IsTheHeaderSerializeStamps()
    {
        // One source of truth: the alert reads the same OwnershipStampPolicy the serializer stamps with.
        var predicate = Predicate("system", SerializerMode.Replace, null, "fill",
            new OwnershipOverride { PageUniqueId = Page, OnExisting = "keep" },
            new OwnershipOverride { GridRowUniqueId = Row, Mode = "Merge" },
            new OwnershipOverride { ParagraphUniqueId = Slot, OnExisting = "keep" });
        var paragraph = new SerializedParagraph { ParagraphUniqueId = Slot, SortOrder = 1 };
        var area = new SerializedArea
        {
            AreaId = Guid.NewGuid(),
            Name = "Site",
            SortOrder = 1,
            Pages = new()
            {
                new SerializedPage
                {
                    PageUniqueId = Page, Name = "Home", MenuText = "Home", UrlName = "home", SortOrder = 1, IsActive = true,
                    GridRows = new()
                    {
                        new SerializedGridRow
                        {
                            Id = Row, SortOrder = 1,
                            Columns = new() { new SerializedGridColumn { Id = 1, Width = 12, Paragraphs = new() { paragraph } } }
                        }
                    }
                }
            }
        };

        var stamped = DocumentHeader.Stamp(area, OwnershipStampPolicy.FromPredicate(predicate)).Pages[0];

        Assert.Equal(stamped.Ownership, ModeAlertVerdict.HeaderOf(predicate, OnPage(ModeAlertScope.Page)));
        Assert.Equal(stamped.GridRows[0].Ownership, ModeAlertVerdict.HeaderOf(predicate, OnPage(ModeAlertScope.GridRow)));
        Assert.Equal(stamped.GridRows[0].Columns[0].Paragraphs[0].Ownership,
            ModeAlertVerdict.HeaderOf(predicate, OnPage(ModeAlertScope.Paragraph)));
    }
}
