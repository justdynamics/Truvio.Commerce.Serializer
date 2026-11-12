using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R2: Merge does what its documentation says for existing grid rows and
/// paragraphs, and no longer moves a page back. The fill helpers run on real DW objects; the
/// write wiring needs a live runtime, so it is pinned structurally against the source.
/// </summary>
public class ContentDeserializerMergeRowsAndParagraphsTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // -------------------------------------------------------------------------
    // Grid row fill
    // -------------------------------------------------------------------------

    [Fact]
    public void GridRow_EmptyTargetFields_AreFilled()
    {
        var row = new MergeableGridRowFields();
        var dto = new SerializedGridRow
        {
            Id = Guid.NewGuid(),
            SortOrder = 1,
            DefinitionId = "Swift-v2_1Column",
            ColorSchemeId = "light",
            TopSpacing = 3
        };
        int left = 0;

        var filled = row.Fill(dto, ref left);

        Assert.Equal(3, filled);
        Assert.Equal("Swift-v2_1Column", row.DefinitionId);
        Assert.Equal("light", row.ColorSchemeId);
        Assert.Equal(3, row.TopSpacing);
    }

    [Fact]
    public void GridRow_SetTargetFields_AreKept()
    {
        var row = new MergeableGridRowFields { DefinitionId = "Editor_2Columns", ColorSchemeId = "dark", TopSpacing = 0 };
        var dto = new SerializedGridRow
        {
            Id = Guid.NewGuid(),
            SortOrder = 1,
            DefinitionId = "Swift-v2_1Column",
            ColorSchemeId = "light",
            TopSpacing = 5
        };
        int left = 0;

        var filled = row.Fill(dto, ref left);

        Assert.Equal(0, filled);
        Assert.Equal(3, left);
        Assert.Equal("Editor_2Columns", row.DefinitionId);
        Assert.Equal("dark", row.ColorSchemeId);
        Assert.Equal(0, row.TopSpacing);   // an editor's explicit 0 spacing is a value, not empty
    }

    [Fact]
    public void GridRow_EmptyYamlValues_NeverClearTheTarget()
    {
        var row = new MergeableGridRowFields { BackgroundImage = "/Files/Images/hero.jpg", GapX = 2 };
        var dto = new SerializedGridRow { Id = Guid.NewGuid(), SortOrder = 1 };
        int left = 0;

        Assert.Equal(0, row.Fill(dto, ref left));
        Assert.Equal("/Files/Images/hero.jpg", row.BackgroundImage);
        Assert.Equal(2, row.GapX);
    }

    // -------------------------------------------------------------------------
    // Grid row vertical alignment (an enum on the row; None is its empty value)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    public void GridRow_UnsetVerticalAlignment_IsFilled(string? target)
    {
        var row = new MergeableGridRowFields { VerticalAlignment = target };
        var dto = new SerializedGridRow { Id = Guid.NewGuid(), SortOrder = 1, VerticalAlignment = "center" };
        int left = 0;

        Assert.Equal(1, row.Fill(dto, ref left));
        Assert.Equal("Center", row.VerticalAlignment);
        Assert.Equal(0, left);
    }

    [Fact]
    public void GridRow_SetVerticalAlignment_IsKept()
    {
        var row = new MergeableGridRowFields { VerticalAlignment = "End" };
        var dto = new SerializedGridRow { Id = Guid.NewGuid(), SortOrder = 1, VerticalAlignment = "Center" };
        int left = 0;

        Assert.Equal(0, row.Fill(dto, ref left));
        Assert.Equal("End", row.VerticalAlignment);
        Assert.Equal(1, left);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]          // the mapper writes None for a row without an alignment
    [InlineData("Middle")]        // not a member
    [InlineData("Start,Center")]  // not one member name
    [InlineData("2")]
    public void GridRow_YamlWithoutAnAlignment_FillsNothing_AndCountsNothing(string? source)
    {
        // "None" from YAML is not a value: it must not count as a fill (the row would be saved
        // and reported as changed on every Merge run) nor as a kept field.
        var unset = new MergeableGridRowFields();
        var set = new MergeableGridRowFields { VerticalAlignment = "Start" };
        var dto = new SerializedGridRow { Id = Guid.NewGuid(), SortOrder = 1, VerticalAlignment = source };
        int left = 0;

        Assert.Equal(0, unset.Fill(dto, ref left));
        Assert.Equal(0, set.Fill(dto, ref left));
        Assert.Null(unset.VerticalAlignment);
        Assert.Equal("Start", set.VerticalAlignment);
        Assert.Equal(0, left);
    }

    [Fact]
    public void MergeGridRowScalars_ReadsAndWritesTheRowsVerticalAlignment()
    {
        // A GridRow needs a live runtime to construct, so the wiring is pinned against the source:
        // the fill set is seeded from the row's alignment and a filled one is written back.
        var start = Source.IndexOf("internal static int MergeGridRowScalars(", StringComparison.Ordinal);
        var end = Source.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = Source.Substring(start, end - start);

        Assert.Contains("VerticalAlignment = row.VerticalAlignment.ToString()", body);
        Assert.Contains("row.VerticalAlignment = alignment;", body);
    }

    [Fact]
    public void FillSet_CoversEveryVisualPropertyAReplaceWritesOnARow()
    {
        // The review finding: VerticalAlignment was written by Replace (and by the old Merge
        // overwrite) but missing from the fill set, so Merge could never deliver it. Every
        // "row.X =" of the Replace write must have a fill-set property of the same name.
        var start = Source.IndexOf("private static void ApplyGridRowVisualProperties(", StringComparison.Ordinal);
        var end = Source.IndexOf("\n    }", start, StringComparison.Ordinal);
        var written = System.Text.RegularExpressions.Regex
            .Matches(Source.Substring(start, end - start), @"row\.(\w+) =")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var fillSet = typeof(MergeableGridRowFields).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Contains("VerticalAlignment", written);
        Assert.All(written, name => Assert.Contains(name, fillSet));
    }

    // -------------------------------------------------------------------------
    // Paragraph fill
    // -------------------------------------------------------------------------

    [Fact]
    public void Paragraph_EmptyTargetFields_AreFilled()
    {
        var paragraph = new MergeableParagraphFields { Header = "" };
        var dto = new SerializedParagraph
        {
            ParagraphUniqueId = Guid.NewGuid(),
            SortOrder = 1,
            ColumnId = 1,
            Header = "Welcome",
            Template = "Text.cshtml"
        };
        int left = 0;

        var filled = paragraph.Fill(dto, ref left);

        Assert.Equal(2, filled);
        Assert.Equal("Welcome", paragraph.Header);
        Assert.Equal("Text.cshtml", paragraph.Template);
    }

    [Fact]
    public void Paragraph_EditorValues_AreKept()
    {
        var paragraph = new MergeableParagraphFields { Header = "Editor heading", Template = "Editor.cshtml", ColorSchemeId = "accent" };
        var dto = new SerializedParagraph
        {
            ParagraphUniqueId = Guid.NewGuid(),
            SortOrder = 1,
            Header = "Shipped heading",
            Template = "Text.cshtml",
            ColorSchemeId = "light"
        };
        int left = 0;

        Assert.Equal(0, paragraph.Fill(dto, ref left));
        Assert.Equal(3, left);
        Assert.Equal("Editor heading", paragraph.Header);
        Assert.Equal("Editor.cshtml", paragraph.Template);
        Assert.Equal("accent", paragraph.ColorSchemeId);
    }

    [Fact]
    public void Paragraph_ModuleSettings_FillMissingElementsOnly_SameModule()
    {
        var paragraph = new MergeableParagraphFields
        {
            ModuleSystemName = "eCom_CartV2",
            ModuleSettings = "<Settings><Mail1SenderEmail>editor@cabp.example</Mail1SenderEmail></Settings>"
        };
        var dto = new SerializedParagraph
        {
            ParagraphUniqueId = Guid.NewGuid(),
            SortOrder = 1,
            ModuleSystemName = "eCom_CartV2",
            ModuleSettings = "<Settings><Mail1SenderEmail>shipped@example.com</Mail1SenderEmail><CartType>2</CartType></Settings>"
        };
        int left = 0;

        var filled = paragraph.Fill(dto, ref left);

        Assert.Equal(1, filled);
        Assert.Contains("editor@cabp.example", paragraph.ModuleSettings);
        Assert.DoesNotContain("shipped@example.com", paragraph.ModuleSettings);
        Assert.Contains("<CartType>2</CartType>", paragraph.ModuleSettings);
    }

    [Fact]
    public void Paragraph_OtherModuleOnTarget_SettingsAreNotTouched()
    {
        var paragraph = new MergeableParagraphFields { ModuleSystemName = "UserAuthentication", ModuleSettings = "<Settings><A>1</A></Settings>" };
        var dto = new SerializedParagraph
        {
            ParagraphUniqueId = Guid.NewGuid(),
            SortOrder = 1,
            ModuleSystemName = "eCom_CartV2",
            ModuleSettings = "<Settings><B>2</B></Settings>"
        };
        int left = 0;

        paragraph.Fill(dto, ref left);

        Assert.Equal("UserAuthentication", paragraph.ModuleSystemName);
        Assert.Equal("<Settings><A>1</A></Settings>", paragraph.ModuleSettings);
    }

    // -------------------------------------------------------------------------
    // Wiring
    // -------------------------------------------------------------------------

    [Fact]
    public void GridRowUpdate_MergeBranch_PrecedesTheSourceWinsWrite()
    {
        var merge = Source.IndexOf("return MergeGridRow(dto, pageId, existingGridRowId, ctx);", StringComparison.Ordinal);
        var overwrite = Source.IndexOf("existingRow2.Sort = dto.SortOrder;", StringComparison.Ordinal);
        Assert.True(merge > 0 && overwrite > merge);
    }

    [Fact]
    public void ParagraphUpdate_MergeBranch_PrecedesTheSourceWinsWrite()
    {
        var merge = Source.IndexOf("MergeParagraph(dto, pageId, existingParagraphId, ctx);", StringComparison.Ordinal);
        var overwrite = Source.IndexOf("existingForUpdate.GridRowId = gridRowId;", StringComparison.Ordinal);
        Assert.True(merge > 0 && overwrite > merge);
    }

    [Fact]
    public void MergeHelpers_UseMergeItemFields_TouchNoPermissionsOrSort()
    {
        foreach (var method in new[] { "private int MergeGridRow(", "private void MergeParagraph(" })
        {
            var start = Source.IndexOf(method, StringComparison.Ordinal);
            Assert.True(start > 0, method);
            var end = Source.IndexOf("\n    }\r", start, StringComparison.Ordinal) is var e and > 0
                ? e
                : Source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
            var body = Source.Substring(start, end - start);
            Assert.Contains("MergeItemFields(", body);
            Assert.DoesNotContain("ApplyPermissions", body);
            Assert.DoesNotContain(".Sort =", body);
        }
    }

    [Fact]
    public void MergeFills_TakeTheirExclusionsFromTheSharedHelper()
    {
        // The two Merge-fill methods do not carry their own copy of the exclusion expression.
        foreach (var name in new[] { "private int MergeGridRow(", "private void MergeParagraph(" })
        {
            var start = Source.IndexOf(name, StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var end = Source.IndexOf("Merge-fill:", Source.IndexOf("Permissions NOT applied on Merge UPDATE", start, StringComparison.Ordinal), StringComparison.Ordinal);
            var body = Source.Substring(start, end - start);
            Assert.Contains("ExclusionMerger.ForItemType(ctx.ExcludeFields, ctx.ExcludeFieldsByItemType, dto.ItemType)", body);
            Assert.DoesNotContain("ExclusionMerger.MergeFieldExclusions(", body);
        }
    }

    [Fact]
    public void FillSets_CarryNoPositionFields()
    {
        // Sort, grid row and column cannot be filled because they are not part of the fill sets.
        var rowProps = typeof(MergeableGridRowFields).GetProperties().Select(p => p.Name).ToList();
        var paraProps = typeof(MergeableParagraphFields).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Sort", rowProps);
        Assert.DoesNotContain("Sort", paraProps);
        Assert.DoesNotContain("GridRowId", paraProps);
        Assert.DoesNotContain("GridRowColumn", paraProps);
    }

    [Fact]
    public void PageMerge_NoLongerResetsTheParent()
    {
        var mergeStart = Source.IndexOf("if (StrategyFor(dto.Ownership) == ConflictStrategy.DestinationWins)", StringComparison.Ordinal);
        var mergeEnd = Source.IndexOf("Merge-fill: page", mergeStart, StringComparison.Ordinal);
        var mergeBranch = Source.Substring(mergeStart, mergeEnd - mergeStart);

        Assert.DoesNotContain("existingPage.ParentPageId = ctx.ParentPageId;", mergeBranch);
        Assert.Contains("Merge keeps the target's parent", mergeBranch);
    }
}
