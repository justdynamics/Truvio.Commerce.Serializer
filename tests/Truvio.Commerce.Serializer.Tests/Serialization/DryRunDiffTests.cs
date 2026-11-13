using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Owner decision D of 2026-11-13: the dry run of a Replace on an existing paragraph or page
/// compares what the write would change. Paired: an identical target reports nothing, and a real
/// change in each field reports a line. The cases are the ones measured on the CABP clean room
/// (about 1,100 false updates, mostly GlobalRecordPageGuid and Text).
/// </summary>
public class DryRunDiffTests
{
    private static readonly HashSet<string> System = new(StringComparer.OrdinalIgnoreCase)
        { "Id", "ItemInstanceType", "Sort", "GlobalRecordPageGuid", "MasterParagraphGuid" };

    private static bool NotWritten(string name) => System.Contains(name);

    private static IReadOnlyList<string> Diff(Dictionary<string, object> document, Dictionary<string, object?> host) =>
        DryRunDiff.ItemFields(document, host, NotWritten);

    // -----------------------------------------------------------------------
    // GlobalRecordPageGuid: a paragraph column, compared as a link
    // -----------------------------------------------------------------------

    private static readonly Guid AddressesPage = Guid.Parse("c1a954f6-bec3-41be-9849-90da0ff533cd");
    private static int? TargetPage(Guid guid) => guid == AddressesPage ? 512 : null;

    [Fact]
    public void GlobalRecordPageGuid_PointingAtTheHostsCurrentPage_IsNoChange()
    {
        Assert.Null(DryRunDiff.GuidLink("GlobalRecordPageGuid", AddressesPage.ToString(), 512, TargetPage));
        // GUID case and format do not matter.
        Assert.Null(DryRunDiff.GuidLink("GlobalRecordPageGuid", AddressesPage.ToString("B").ToUpperInvariant(), 512, TargetPage));
    }

    [Fact]
    public void GlobalRecordPageGuid_PointingElsewhere_IsReported()
    {
        var line = DryRunDiff.GuidLink("GlobalRecordPageGuid", AddressesPage.ToString(), 77, TargetPage);

        Assert.Equal($"Fields[GlobalRecordPageGuid]: '77' -> '512' ({AddressesPage})", line);
    }

    [Fact]
    public void GlobalRecordPageGuid_OfAPageTheTargetDoesNotHave_IsReported()
    {
        var other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        Assert.Contains("not on the target yet", DryRunDiff.GuidLink("GlobalRecordPageGuid", other.ToString(), 512, TargetPage));
        Assert.Contains("not a GUID", DryRunDiff.GuidLink("GlobalRecordPageGuid", "page 7", 512, TargetPage));
    }

    [Fact]
    public void NoGlobalRecordPageGuid_InTheDocument_IsNoChange_TheWriteNeverClearsIt()
    {
        Assert.Null(DryRunDiff.GuidLink("GlobalRecordPageGuid", null, 512, TargetPage));
        Assert.Null(DryRunDiff.GuidLink("GlobalRecordPageGuid", "", 0, TargetPage));
    }

    [Fact]
    public void GlobalRecordPageGuid_IsNeverComparedAsAnItemField()
    {
        // The old dry run compared it with the item's (absent) field and reported every
        // global-element paragraph as an update.
        var document = new Dictionary<string, object> { ["GlobalRecordPageGuid"] = AddressesPage.ToString(), ["Title"] = "Addresses" };
        var host = new Dictionary<string, object?> { ["Title"] = "Addresses" };

        Assert.Empty(Diff(document, host));
    }

    // -----------------------------------------------------------------------
    // Text: the item's field
    // -----------------------------------------------------------------------

    [Fact]
    public void Text_EqualToTheItemsTextField_IsNoChange_EvenWithCrlfOnTheHost()
    {
        var document = new Dictionary<string, object> { ["Text"] = "<p>Ship to\nthe job site</p>" };
        var host = new Dictionary<string, object?> { ["Text"] = "<p>Ship to\r\nthe job site</p>" };

        Assert.Empty(Diff(document, host));
    }

    [Fact]
    public void Text_ThatDiffersFromTheItemsTextField_IsReported()
    {
        var document = new Dictionary<string, object> { ["Text"] = "<p>Ship to the job site</p>" };
        var host = new Dictionary<string, object?> { ["Text"] = "<p>Pick up at the branch</p>" };

        var line = Assert.Single(Diff(document, host));
        Assert.Equal("Fields[Text]: '<p>Pick up at the branch</p>' -> '<p>Ship to the job site</p>'", line);
    }

    [Fact]
    public void Text_OnAnItemWithoutATextField_IsNoChange_TheItemApiIgnoresIt()
    {
        var document = new Dictionary<string, object> { ["Text"] = "<p>Module text</p>", ["Title"] = "T" };
        var host = new Dictionary<string, object?> { ["Title"] = "T" };

        Assert.Empty(Diff(document, host));
    }

    [Fact]
    public void AParagraphWithoutAnItem_HasNoItemFieldDiff()
    {
        Assert.Empty(DryRunDiff.ItemFields(new Dictionary<string, object> { ["Text"] = "x" }, null, NotWritten));
    }

    // -----------------------------------------------------------------------
    // Other normalisation, paired
    // -----------------------------------------------------------------------

    [Fact]
    public void AnIdenticalItem_ReportsNothing()
    {
        var document = new Dictionary<string, object>
        {
            ["Id"] = "1", ["Sort"] = 0, ["ItemInstanceType"] = "",
            ["Title"] = "Your points", ["ShowBadge"] = "true", ["Columns"] = "3", ["Image"] = "",
            ["PrimaryCta"] = "{\"Label\":\"\",\"Style\":\"link\",\"LinkType\":\"\",\"Link\":\"\",\"SelectedValue\":\"\"}"
        };
        var host = new Dictionary<string, object?>
        {
            ["Id"] = "4711", ["Sort"] = 3L, ["ItemInstanceType"] = "Cabp_PointsDashboard",
            ["Title"] = "Your points", ["ShowBadge"] = true, ["Columns"] = 3, ["Image"] = null,
            ["PrimaryCta"] = null
        };

        Assert.Empty(Diff(document, host));
    }

    [Theory]
    [InlineData("Title", "Your points", "Your rewards")]
    [InlineData("ShowBadge", "true", false)]
    [InlineData("Columns", "3", 4)]
    public void ARealChange_InAnyField_IsReported(string field, object documentValue, object hostValue)
    {
        var document = new Dictionary<string, object> { [field] = documentValue };
        var host = new Dictionary<string, object?> { [field] = hostValue };

        Assert.Single(Diff(document, host), line => line.StartsWith($"Fields[{field}]:", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostFieldTheDocumentDoesNotCarry_IsReported_UnlessItIsNotWritten()
    {
        var host = new Dictionary<string, object?> { ["Title"] = "T", ["Perk"] = "Free shipping", ["Id"] = "9" };

        var line = Assert.Single(Diff(new Dictionary<string, object> { ["Title"] = "T" }, host));

        Assert.Equal("Fields[Perk]: 'Free shipping' -> '' (not in the document)", line);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData("Header", "Header")]
    public void NullAndEmpty_AreTheSameText(string? a, string? b) =>
        Assert.True(DryRunDiff.SameText(a, b));

    [Fact]
    public void ADifferentText_IsNotTheSame() =>
        Assert.False(DryRunDiff.SameText("Header", "header"));

    // -----------------------------------------------------------------------
    // Permissions
    // -----------------------------------------------------------------------

    private static SerializedPermission Perm(string owner, string type, string level, string? sub = null) =>
        new() { Owner = owner, OwnerType = type, Level = level, LevelValue = 0, SubName = sub };

    [Fact]
    public void TheSamePermissions_InAnotherOrder_AreNoChange()
    {
        var document = new[] { Perm("Anonymous", "role", "none"), Perm("Dealers", "group", "read") };
        var host = new[] { Perm("dealers", "group", "Read"), Perm("Anonymous", "role", "None") };

        Assert.True(DryRunDiff.SamePermissions(document, host));
    }

    [Fact]
    public void ADifferentPermission_IsAChange()
    {
        var document = new[] { Perm("Anonymous", "role", "none") };

        Assert.False(DryRunDiff.SamePermissions(document, new[] { Perm("Anonymous", "role", "read") }));
        Assert.False(DryRunDiff.SamePermissions(document, Array.Empty<SerializedPermission>()));
        Assert.False(DryRunDiff.SamePermissions(document, new[] { Perm("Anonymous", "role", "none"), Perm("Dealers", "group", "read") }));
    }

    // -----------------------------------------------------------------------
    // Grid row order: the serialize side numbers rows 1..N
    // -----------------------------------------------------------------------

    [Fact]
    public void GridRowPosition_IsTheRowsPlaceInSortThenIdOrder()
    {
        // CABP page 79: Sort 1, 3, 4, 5, 6 serializes as 1..5; row 200 (Sort 3) is second.
        var rows = new[] { (199, 1), (200, 3), (201, 4), (202, 5), (203, 6) };

        Assert.Equal(2, DryRunDiff.GridRowPosition(rows, 200));
        Assert.Equal(5, DryRunDiff.GridRowPosition(rows, 203));
        Assert.Equal(0, DryRunDiff.GridRowPosition(rows, 999));
        // Equal Sort values are ordered by id, as the serialize side orders them.
        Assert.Equal(1, DryRunDiff.GridRowPosition(new[] { (8, 0), (5, 0) }, 5));
    }

    // -----------------------------------------------------------------------
    // JSON values (buttons): data, not formatting
    // -----------------------------------------------------------------------

    [Fact]
    public void AButton_InAnotherJsonFormatting_IsNoChange()
    {
        // The host holds the button compact with a \u escape; the document carries it indented.
        var host = new Dictionary<string, object?> { ["FirstButton"] = """{"SelectedValue":"174","Label":"Cont\u00E1ctenos","Link":"Default.aspx?ID=174"}""" };
        var document = new Dictionary<string, object> { ["FirstButton"] = """
            {
              "Label": "Contáctenos",
              "SelectedValue": "174",
              "Link": "Default.aspx?ID=174"
            }
            """ };

        Assert.Empty(Diff(document, host));
    }

    [Fact]
    public void AButton_WithAnotherLabelOrLink_IsReported()
    {
        var host = new Dictionary<string, object?> { ["FirstButton"] = "{\"SelectedValue\":\"57\",\"Label\":\"Join now\",\"Link\":\"Default.aspx?ID=57\"}" };

        Assert.Single(Diff(new Dictionary<string, object> { ["FirstButton"] = "{\"SelectedValue\":\"57\",\"Label\":\"Join today\",\"Link\":\"Default.aspx?ID=57\"}" }, host));
        Assert.Single(Diff(new Dictionary<string, object> { ["FirstButton"] = "{\"SelectedValue\":\"183\",\"Label\":\"Join now\",\"Link\":\"Default.aspx?ID=183\"}" }, host));
    }

    [Fact]
    public void TextThatIsNotJson_IsComparedAsText()
    {
        Assert.Null(ItemListFieldDiff.CanonicalJson("{not json"));
        Assert.Null(ItemListFieldDiff.CanonicalJson("plain"));
        Assert.Equal("{\"a\":1,\"b\":\"é\"}", ItemListFieldDiff.CanonicalJson("{ \"b\": \"\\u00e9\", \"a\": 1 }"));
    }
}
