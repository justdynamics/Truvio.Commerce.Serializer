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
