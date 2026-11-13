using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;
using YamlDotNet.Core;

namespace Truvio.Commerce.Serializer.Tests.Models;

/// <summary>
/// Content ownership R7: the YAML shape of an item list inside its paragraph or page document,
/// and the strict read of its entries.
/// </summary>
public class SerializedItemListYamlTests : IDisposable
{
    private readonly FileSystemStore _store = new();
    private readonly string _tempRoot;

    public SerializedItemListYamlTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SerializerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static SerializedParagraph Paragraph(Dictionary<string, SerializedItemList>? lists = null,
        Dictionary<string, SerializedItemListOptions>? options = null) => new()
    {
        ParagraphUniqueId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        SortOrder = 1,
        ItemType = "Cabp_PointsDashboard",
        Fields = new Dictionary<string, object> { ["Heading"] = "Your points" },
        ItemLists = lists,
        ItemListOptions = options
    };

    private static SerializedItemListEntry Entry(string id, string title, Dictionary<string, SerializedItemList>? nested = null) => new()
    {
        Id = id,
        Fields = new Dictionary<string, object> { ["Title"] = title },
        ItemLists = nested
    };

    /// <summary>
    /// The document as the store writes it, with map keys unquoted: the serializer quotes every
    /// string scalar, dictionary keys included, and these tests are about structure and order.
    /// </summary>
    private static string Write(object value) =>
        System.Text.RegularExpressions.Regex.Replace(
            YamlConfiguration.BuildSerializer().Serialize(value).ReplaceLineEndings("\n"),
            "(?m)^(\\s*(?:- )?)\"([^\"\\n]+)\":", "$1$2:");

    private static T RoundTrip<T>(T value)
    {
        var yaml = YamlConfiguration.BuildSerializer().Serialize(value!);
        return YamlConfiguration.BuildDeserializer().Deserialize<T>(yaml);
    }

    // -----------------------------------------------------------------------
    // Shape
    // -----------------------------------------------------------------------

    [Fact]
    public void List_IsWrittenAsASequenceOfEntries_UnderItsFieldName()
    {
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList>
        {
            ["Tiers"] = new(new[] { Entry("en-tier-1", "Bronze"), Entry("en-tier-2", "Silver") })
        });

        var yaml = Write(paragraph);

        Assert.Contains(
            "itemLists:\n" +
            "  Tiers:\n" +
            "  - id: \"en-tier-1\"\n" +
            "    fields:\n" +
            "      Title: \"Bronze\"\n" +
            "  - id: \"en-tier-2\"\n" +
            "    fields:\n" +
            "      Title: \"Silver\"\n",
            yaml);
    }

    [Fact]
    public void NumericEntryId_IsWrittenAsQuotedText()
    {
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList>
        {
            ["Slides"] = new(new[] { Entry("12", "First") })
        });

        var yaml = Write(paragraph);

        Assert.Contains("- id: \"12\"", yaml);
        Assert.Equal("12", RoundTrip(paragraph).ItemLists!["Slides"].Entries[0].Id);
    }

    [Fact]
    public void EntryFields_AreWrittenInKeyOrder()
    {
        var entry = new SerializedItemListEntry
        {
            Id = "a",
            Fields = new Dictionary<string, object> { ["Zeta"] = "z", ["Alpha"] = "a", ["Mid"] = "m" }
        };
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList> { ["Tiers"] = new(new[] { entry }) });

        var yaml = Write(paragraph);

        Assert.True(yaml.IndexOf("Alpha:", StringComparison.Ordinal) < yaml.IndexOf("Mid:", StringComparison.Ordinal));
        Assert.True(yaml.IndexOf("Mid:", StringComparison.Ordinal) < yaml.IndexOf("Zeta:", StringComparison.Ordinal));
    }

    [Fact]
    public void ParagraphWithoutItemLists_WritesNoItemListKeys()
    {
        var yaml = Write(Paragraph());

        Assert.DoesNotContain("itemLists", yaml);
        Assert.DoesNotContain("itemListOptions", yaml);
    }

    [Fact]
    public void Options_AreWrittenBesideTheLists_OnlyWhenGiven()
    {
        var lists = new Dictionary<string, SerializedItemList> { ["Tiers"] = new(new[] { Entry("es-tier-1", "Bronce") }) };
        var withOptions = Paragraph(lists, new Dictionary<string, SerializedItemListOptions>
        {
            ["Tiers"] = new() { ChangedOnLanguage = true }
        });

        var yaml = Write(withOptions);

        Assert.Contains("itemListOptions:\n  Tiers:\n    changedOnLanguage: true\n", yaml);
        Assert.True(RoundTrip(withOptions).ItemListOptions!["Tiers"].ChangedOnLanguage);
        Assert.DoesNotContain("itemListOptions", YamlConfiguration.BuildSerializer().Serialize(Paragraph(lists)));
    }

    // -----------------------------------------------------------------------
    // Round trip
    // -----------------------------------------------------------------------

    [Fact]
    public void RoundTrip_KeepsEntriesInOrder_WithTheirFields()
    {
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList>
        {
            ["Tiers"] = new(new[] { Entry("c", "Third"), Entry("a", "First"), Entry("b", "Second") })
        });

        var read = RoundTrip(paragraph);

        Assert.Equal(new[] { "c", "a", "b" }, read.ItemLists!["Tiers"].Entries.Select(e => e.Id));
        Assert.Equal("Third", read.ItemLists["Tiers"].Entries[0].Fields["Title"]);
        Assert.Equal("Your points", read.Fields["Heading"]);
    }

    [Fact]
    public void RoundTrip_KeepsAListInsideAnEntry()
    {
        var nested = new Dictionary<string, SerializedItemList>
        {
            ["Links"] = new(new[] { Entry("link-1", "Docs"), Entry("link-2", "Support") })
        };
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList>
        {
            ["Tiers"] = new(new[] { Entry("en-tier-1", "Bronze", nested) })
        });

        var read = RoundTrip(paragraph);

        var entry = Assert.Single(read.ItemLists!["Tiers"].Entries);
        Assert.Equal(new[] { "link-1", "link-2" }, entry.ItemLists!["Links"].Entries.Select(e => e.Id));
        Assert.Equal("Support", entry.ItemLists["Links"].Entries[1].Fields["Title"]);
    }

    [Fact]
    public void RoundTrip_KeepsAnEmptyList_SoReplaceCanEmptyTheHostList()
    {
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList> { ["Tiers"] = new() });

        var yaml = Write(paragraph);
        var read = RoundTrip(paragraph);

        Assert.Contains("Tiers: []", yaml);
        Assert.NotNull(read.ItemLists);
        Assert.Empty(read.ItemLists!["Tiers"].Entries);
    }

    [Fact]
    public void Store_KeepsParagraphAndPageLists_IncludingAnEmptyPageList()
    {
        var paragraph = Paragraph(new Dictionary<string, SerializedItemList>
        {
            ["Tiers"] = new(new[] { Entry("en-tier-1", "Bronze"), Entry("en-tier-2", "Silver") })
        });
        var page = ContentTreeBuilder.BuildSinglePage("Points") with
        {
            Paragraphs = new List<SerializedParagraph> { paragraph },
            ItemLists = new Dictionary<string, SerializedItemList>
            {
                ["Banners"] = new(new[] { Entry("banner-1", "Welcome") }),
                // page.yml is written with empty collections omitted; an owned empty list must stay.
                ["Footnotes"] = new()
            }
        };
        var area = new SerializedArea { AreaId = Guid.NewGuid(), Name = "Site", SortOrder = 1, Pages = new List<SerializedPage> { page } };

        _store.WriteTree(area, _tempRoot);
        var read = Assert.Single(_store.ReadTree(_tempRoot, "Site").Pages);

        Assert.Equal(new[] { "banner-1" }, read.ItemLists!["Banners"].Entries.Select(e => e.Id));
        Assert.True(read.ItemLists.ContainsKey("Footnotes"));
        Assert.Empty(read.ItemLists["Footnotes"].Entries);
        var readParagraph = Assert.Single(read.Paragraphs);
        Assert.Equal(new[] { "en-tier-1", "en-tier-2" }, readParagraph.ItemLists!["Tiers"].Entries.Select(e => e.Id));
    }

    // -----------------------------------------------------------------------
    // Documents written through 1.0.9-beta
    // -----------------------------------------------------------------------

    [Fact]
    public void DocumentWithoutItemLists_ReadsAsBefore_ListFieldStaysARawIdInFields()
    {
        const string yaml =
            "paragraphUniqueId: 11111111-2222-3333-4444-555555555555\n" +
            "sortOrder: 1\n" +
            "itemType: Cabp_PointsDashboard\n" +
            "fields:\n" +
            "  Heading: \"Your points\"\n" +
            "  Tiers: 96\n";

        var read = YamlConfiguration.BuildDeserializer().Deserialize<SerializedParagraph>(yaml);

        Assert.Null(read.ItemLists);
        Assert.Null(read.ItemListOptions);
        Assert.Equal("96", read.Fields["Tiers"]);
    }

    // -----------------------------------------------------------------------
    // Strict read
    // -----------------------------------------------------------------------

    private static SerializedParagraph Read(string itemListsYaml) =>
        YamlConfiguration.BuildDeserializer().Deserialize<SerializedParagraph>(
            "paragraphUniqueId: 11111111-2222-3333-4444-555555555555\nsortOrder: 1\n" + itemListsYaml);

    [Fact]
    public void UnknownEntryMember_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n  - id: en-tier-1\n    feilds:\n      Title: Bronze\n"));

        Assert.Contains("Unknown member 'feilds'", ex.ToString());
    }

    [Fact]
    public void EntryWithoutId_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n  - fields:\n      Title: Bronze\n"));

        Assert.Contains("has no 'id'", ex.ToString());
    }

    [Fact]
    public void EntryWithBlankId_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n  - id: \"\"\n    fields:\n      Title: Bronze\n"));

        Assert.Contains("has no 'id'", ex.ToString());
    }

    [Fact]
    public void ListThatIsNotASequence_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n    id: en-tier-1\n"));

        Assert.Contains("must be a sequence", ex.ToString());
    }

    [Fact]
    public void EntryThatIsNotAMapping_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n  - en-tier-1\n"));

        Assert.Contains("must be a mapping", ex.ToString());
    }

    [Fact]
    public void UnknownMemberInANestedEntry_FailsTheRead()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => Read(
            "itemLists:\n  Tiers:\n  - id: en-tier-1\n    itemLists:\n      Links:\n      - id: link-1\n        sort: 3\n"));

        Assert.Contains("Unknown member 'sort'", ex.ToString());
    }

    [Fact]
    public void EntryWithIdOnly_ReadsWithNoFields()
    {
        var read = Read("itemLists:\n  Tiers:\n  - id: en-tier-1\n");

        var entry = Assert.Single(read.ItemLists!["Tiers"].Entries);
        Assert.Equal("en-tier-1", entry.Id);
        Assert.Empty(entry.Fields);
        Assert.Null(entry.ItemLists);
    }
}
