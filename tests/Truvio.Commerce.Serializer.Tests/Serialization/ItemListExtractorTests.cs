using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R7, serialize side: which item fields are item lists
/// (<see cref="ItemListFieldClassifier"/>), how a list leaves <c>fields</c> for <c>itemLists</c>
/// (<see cref="ItemListExtractor"/>), and when lists travel inside documents at all
/// (<see cref="ItemListTravelPolicy"/>).
/// </summary>
public class ItemListExtractorTests
{
    // -----------------------------------------------------------------------
    // Classifier
    // -----------------------------------------------------------------------

    private const string AccordionXml = """
        <items>
          <item name="Accordion" systemName="Swift-v2_Accordion">
            <fields>
              <field name="Accordion Items" systemName="Accordion_Items" type="System.Int32, System.Private.CoreLib" defaultValue="0">
                <editor type="Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb">
                  <editorConfuguration><Parameters addin="Dynamicweb.Content.Items.Editors.ItemRelationListEditor"><Parameter addin="x" name="Item type" value="Swift-v2_Accordion_Item" /><Parameter addin="x" name="Sort by" value="" /><Parameter addin="x" name="Item source" value="ListItem" /></Parameters></editorConfuguration>
                </editor>
              </field>
              <field name="Related pages" systemName="RelatedPages" type="System.Int32, System.Private.CoreLib">
                <editor type="Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb">
                  <editorConfuguration><Parameters><Parameter name="Item type" value="Swift-v2_Page" /><Parameter name="Item source" value="Page" /></Parameters></editorConfuguration>
                </editor>
              </field>
              <field name="Title" systemName="Title" type="System.String, System.Private.CoreLib">
                <editor type="Dynamicweb.Content.Items.Editors.TextEditor, Dynamicweb" />
              </field>
              <field name="Broken" systemName="Broken" type="System.Int32, System.Private.CoreLib">
                <editor type="Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb">
                  <editorConfuguration><Parameters><Parameter name="Item source" value="ListItem" /></Parameters></editorConfuguration>
                </editor>
              </field>
            </fields>
          </item>
        </items>
        """;

    [Theory]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb", true)]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemRelationListEditor", true)]
    [InlineData("ItemRelationListEditor", true)]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemTypeEditor, Dynamicweb", false)]
    [InlineData("Dynamicweb.Content.Items.Editors.TextEditor, Dynamicweb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheItemRelationListEditor_IsAnItemList(string? editorType, bool expected) =>
        Assert.Equal(expected, ItemListFieldClassifier.IsItemListEditor(editorType));

    [Fact]
    public void ItemTypeXml_YieldsItsItemListFields_WithEntryTypeAndSource()
    {
        var fields = ItemListFieldClassifier.FromItemTypeXml(AccordionXml);

        Assert.Equal(new[] { "Accordion_Items", "RelatedPages" }, fields.Keys.OrderBy(k => k));
        Assert.Equal("Swift-v2_Accordion_Item", fields["accordion_items"].MemberItemType);
        Assert.True(fields["Accordion_Items"].IsInline);
        Assert.False(fields["RelatedPages"].IsInline);
        Assert.Equal("Page", fields["RelatedPages"].ItemSource);
    }

    [Fact]
    public void EditorConfiguration_IsReadFromTheParametersXml()
    {
        const string editor = "Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb";
        const string parameters =
            "<Parameters addin=\"x\"><Parameter addin=\"x\" name=\"Item type\" value=\"Swift-v2_Slider_Item\" />" +
            "<Parameter addin=\"x\" name=\"Item source\" value=\"\" /></Parameters>";

        var info = ItemListFieldClassifier.FromEditorConfiguration(editor, parameters);

        Assert.Equal("Swift-v2_Slider_Item", info!.MemberItemType);
        Assert.True(info.IsInline);   // no source is the inline source, as in Dynamicweb
    }

    [Theory]
    [InlineData("Dynamicweb.Content.Items.Editors.TextEditor, Dynamicweb", "<Parameters><Parameter name=\"Item type\" value=\"X\" /></Parameters>")]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb", "")]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb", "<Parameters><Parameter name=\"Item source\" value=\"ListItem\" /></Parameters>")]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemRelationListEditor, Dynamicweb", "<Parameters><Parameter name=")]
    public void EditorConfiguration_ThatIsNoUsableItemList_IsNull(string editor, string parameters) =>
        Assert.Null(ItemListFieldClassifier.FromEditorConfiguration(editor, parameters));

    // -----------------------------------------------------------------------
    // Extractor
    // -----------------------------------------------------------------------

    private sealed class FakeSource : IItemListSource
    {
        public Dictionary<string, Dictionary<string, ItemListFieldInfo>> ListFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, ItemListSnapshot> Lists { get; } = new();
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeSource Field(string itemType, string field, string memberType, string source = "ListItem")
        {
            if (!ListFields.TryGetValue(itemType, out var fields))
                ListFields[itemType] = fields = new Dictionary<string, ItemListFieldInfo>(StringComparer.OrdinalIgnoreCase);
            fields[field] = new ItemListFieldInfo(memberType, source);
            return this;
        }

        public FakeSource List(int id, string itemType, params ItemListMember[] members)
        {
            Lists[id] = new ItemListSnapshot(itemType, false, members, Array.Empty<string>());
            return this;
        }

        public IReadOnlyDictionary<string, ItemListFieldInfo>? GetListFields(string itemType) =>
            Unreadable.Contains(itemType) ? null
            : ListFields.TryGetValue(itemType, out var fields) ? fields
            : new Dictionary<string, ItemListFieldInfo>();

        public ItemListSnapshot? ReadList(int listId) => Lists.TryGetValue(listId, out var list) ? list : null;

        public List<ItemListEntryMapping> Map { get; } = new();

        public FakeSource Mapping(string ownerKey, string field, string sourceId, string itemType, string itemId)
        {
            Map.Add(new ItemListEntryMapping(ItemListIdentity.EntryKey(ownerKey, field, sourceId), itemType, itemId, ownerKey, field, sourceId));
            return this;
        }

        public IReadOnlyDictionary<string, ItemListEntryMapping> GetEntryMappings(string itemType, IReadOnlyCollection<string> itemIds) =>
            Map.Where(m => m.ItemType == itemType && itemIds.Contains(m.ItemId))
               .GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => g.First());
    }

    private static ItemListMember Member(string id, params (string Field, object? Value)[] fields) =>
        new(id, fields.ToDictionary(f => f.Field, f => f.Value));

    private const string OwnerLabel = "paragraph 1111 (ID=74)";

    private static FakeSource Source() =>
        new FakeSource()
            .Field("Cabp_PointsDashboard", "Tiers", "Cabp_Tier")
            .Field("Cabp_Tier", "Links", "Cabp_TierLink");

    private static (ItemListExtraction? Extraction, Dictionary<string, object> Fields, List<string> Log, ItemListExtractor Extractor)
        Extract(FakeSource source, Dictionary<string, object> fields, IReadOnlySet<string>? excluded = null,
            Func<string, IReadOnlySet<string>?>? excludedFor = null, string itemType = "Cabp_PointsDashboard", string? ownerKey = null)
    {
        var log = new List<string>();
        var extractor = new ItemListExtractor(source, log.Add);
        return (extractor.Extract(itemType, fields, excluded, OwnerLabel, excludedFor, ownerKey), fields, log, extractor);
    }

    private static readonly string OwnerKey = ItemListIdentity.ParagraphOwner(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void AnEntryThisEngineCreatedOnTheHost_IsWrittenUnderTheDocumentIdItWasCreatedFor()
    {
        // B received the source's entries "12" and "en-tier-1" as its own 901 and 902.
        var source = Source().List(96, "Cabp_Tier",
                Member("901", ("Title", "Silver")), Member("902", ("Title", "Bronze")), Member("903", ("Title", "Editor's")))
            .Mapping(OwnerKey, "Tiers", "12", "Cabp_Tier", "901")
            .Mapping(OwnerKey, "Tiers", "en-tier-1", "Cabp_Tier", "902");
        var fields = new Dictionary<string, object> { ["Tiers"] = 96 };

        var (extraction, _, log, _) = Extract(source, fields, ownerKey: OwnerKey);

        Assert.Equal(new[] { "12", "en-tier-1", "903" }, extraction!.Lists["Tiers"].Entries.Select(e => e.Id));
        Assert.Empty(log);
    }

    [Fact]
    public void AMapRowOfAnotherOwnerOrField_IsNotUsed()
    {
        var other = ItemListIdentity.ParagraphOwner(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var source = Source().List(96, "Cabp_Tier", Member("901", ("Title", "Silver")), Member("902", ("Title", "Gold")))
            .Mapping(other, "Tiers", "12", "Cabp_Tier", "901")
            .Mapping(OwnerKey, "Slides", "13", "Cabp_Tier", "902");

        var (extraction, _, _, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 }, ownerKey: OwnerKey);

        Assert.Equal(new[] { "901", "902" }, extraction!.Lists["Tiers"].Entries.Select(e => e.Id));
    }

    [Fact]
    public void ASourceIdAnotherEntryHoldsAsItsRawId_IsNotUsed_TheEntryKeepsItsHostId()
    {
        // 901 was created for "12", and an editor's entry on this host is 12.
        var source = Source().List(96, "Cabp_Tier", Member("901", ("Title", "Silver")), Member("12", ("Title", "Editor's")))
            .Mapping(OwnerKey, "Tiers", "12", "Cabp_Tier", "901");

        var (extraction, _, log, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 }, ownerKey: OwnerKey);

        Assert.Equal(new[] { "901", "12" }, extraction!.Lists["Tiers"].Entries.Select(e => e.Id));
        Assert.Contains(log, line => line.Contains("'901' was created for the document entry '12'"));
        Assert.DoesNotContain(log, line => line.StartsWith("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAnOwnerKey_EntriesKeepTheirHostIds()
    {
        var source = Source().List(96, "Cabp_Tier", Member("901", ("Title", "Silver")))
            .Mapping(OwnerKey, "Tiers", "12", "Cabp_Tier", "901");

        var (extraction, _, _, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 });

        Assert.Equal(new[] { "901" }, extraction!.Lists["Tiers"].Entries.Select(e => e.Id));
    }

    [Fact]
    public void ListField_LeavesFields_AndBecomesOrderedEntriesWithoutSystemFields()
    {
        var source = Source().List(96, "Cabp_Tier",
            Member("en-tier-2", ("Id", "en-tier-2"), ("Sort", 1), ("ItemInstanceType", ""), ("Title", "Silver"), ("Perk", null)),
            Member("en-tier-1", ("Id", "en-tier-1"), ("Sort", 2), ("Title", "Bronze")));
        var fields = new Dictionary<string, object> { ["Heading"] = "Your points", ["Tiers"] = 96 };

        var (extraction, remaining, log, extractor) = Extract(source, fields);

        Assert.Equal(new[] { "Heading" }, remaining.Keys);
        var entries = extraction!.Lists["Tiers"].Entries;
        Assert.Equal(new[] { "en-tier-2", "en-tier-1" }, entries.Select(e => e.Id));
        Assert.Equal(new[] { "Title" }, entries[0].Fields.Keys);
        Assert.Equal("Silver", entries[0].Fields["Title"]);
        Assert.Null(extraction.Options);
        Assert.Empty(log);
        Assert.Contains("Cabp_Tier", extractor.MemberItemTypes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void ItemWithoutAList_StillOwnsTheField_AsAnEmptyList(object? value)
    {
        var fields = new Dictionary<string, object> { ["Heading"] = "Your points" };
        if (value is not null) fields["Tiers"] = value;

        var (extraction, remaining, log, _) = Extract(Source(), fields);

        Assert.Empty(extraction!.Lists["Tiers"].Entries);
        Assert.DoesNotContain("Tiers", remaining.Keys);
        Assert.Empty(log);
    }

    [Fact]
    public void ItemTypeWithoutItemListFields_IsNotTouched()
    {
        var fields = new Dictionary<string, object> { ["Title"] = "Plain", ["Count"] = 3 };

        var (extraction, remaining, _, _) = Extract(Source(), fields, itemType: "Swift-v2_Text");

        Assert.Null(extraction);
        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public void ItemTypeWhoseMetadataCannotBeRead_IsNotTouched()
    {
        var source = Source();
        source.Unreadable.Add("Cabp_PointsDashboard");
        var fields = new Dictionary<string, object> { ["Tiers"] = 96 };

        var (extraction, remaining, _, _) = Extract(source, fields);

        Assert.Null(extraction);
        Assert.Equal(96, remaining["Tiers"]);
    }

    [Fact]
    public void NestedList_TravelsInsideItsEntry()
    {
        var source = Source()
            .List(96, "Cabp_Tier", Member("en-tier-1", ("Title", "Bronze"), ("Links", 97)))
            .List(97, "Cabp_TierLink", Member("link-1", ("Title", "Docs")), Member("link-2", ("Title", "Support")));

        var (extraction, _, _, extractor) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 });

        var tier = Assert.Single(extraction!.Lists["Tiers"].Entries);
        Assert.Equal(new[] { "Title" }, tier.Fields.Keys);
        Assert.Equal(new[] { "link-1", "link-2" }, tier.ItemLists!["Links"].Entries.Select(e => e.Id));
        Assert.Equal(new[] { "Cabp_Tier", "Cabp_TierLink" }, extractor.MemberItemTypes.OrderBy(t => t));
    }

    [Fact]
    public void ExcludedListField_IsLeftOut_LikeAnyExcludedField()
    {
        var source = Source().List(96, "Cabp_Tier", Member("en-tier-1", ("Title", "Bronze")));
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tiers" };

        var (extraction, _, _, _) = Extract(source, new Dictionary<string, object> { ["Heading"] = "x" }, excluded);

        Assert.Null(extraction);
    }

    [Fact]
    public void ExclusionsOfTheEntryItemType_ApplyToEntryFields()
    {
        var source = Source().List(96, "Cabp_Tier", Member("en-tier-1", ("Title", "Bronze"), ("LocalNote", "host only")));
        IReadOnlySet<string> tierExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LocalNote" };

        var (extraction, _, _, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 },
            excludedFor: type => type == "Cabp_Tier" ? tierExclusions : null);

        Assert.Equal(new[] { "Title" }, Assert.Single(extraction!.Lists["Tiers"].Entries).Fields.Keys);
    }

    [Fact]
    public void ListOfPagesOrParagraphs_StaysARawIdInFields_AndIsNamedOnce()
    {
        var source = new FakeSource().Field("Cabp_Related", "Pages", "Swift-v2_Page", source: "Page");
        var log = new List<string>();
        var extractor = new ItemListExtractor(source, log.Add);
        var first = new Dictionary<string, object> { ["Pages"] = 12 };
        var second = new Dictionary<string, object> { ["Pages"] = 13 };

        Assert.Null(extractor.Extract("Cabp_Related", first, null, OwnerLabel));
        Assert.Null(extractor.Extract("Cabp_Related", second, null, "paragraph 2222 (ID=75)"));

        Assert.Equal(12, first["Pages"]);
        var line = Assert.Single(log);
        Assert.Contains("Item list not carried", line);
        Assert.DoesNotContain("WARNING", line);
    }

    [Fact]
    public void ChangedOnLanguage_IsCarriedAsAnOption_OnlyWhenSet()
    {
        var source = Source();
        source.Lists[96] = new ItemListSnapshot("Cabp_Tier", ChangedOnLanguage: true,
            new[] { Member("es-tier-1", ("Title", "Bronce")) }, Array.Empty<string>());

        var (extraction, _, _, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 });

        Assert.True(extraction!.Options!["Tiers"].ChangedOnLanguage);
    }

    [Fact]
    public void ListIdThatDoesNotExist_IsAWarning_AndTheListIsWrittenEmpty()
    {
        var (extraction, _, log, _) = Extract(Source(), new Dictionary<string, object> { ["Tiers"] = 30 });

        Assert.Empty(extraction!.Lists["Tiers"].Entries);
        var warning = Assert.Single(log);
        Assert.StartsWith("WARNING:", warning);
        Assert.Contains("item list 30, which does not exist", warning);
        Assert.Contains(OwnerLabel, warning);
    }

    [Fact]
    public void ListOfAnotherItemTypeThanTheFieldDeclares_IsAWarning_NotAnEntryOfTheWrongType()
    {
        var source = Source().List(4, "Cabp_StatStrip_Item", Member("stat-1", ("Value", "42")));

        var (extraction, _, log, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 4 });

        Assert.Empty(extraction!.Lists["Tiers"].Entries);
        Assert.Contains("of item type 'Cabp_StatStrip_Item', but the field declares 'Cabp_Tier'", Assert.Single(log));
        Assert.StartsWith("WARNING:", log[0]);
    }

    [Fact]
    public void RelationWhoseItemIsGone_IsAWarning_NamingTheId()
    {
        var source = Source();
        source.Lists[96] = new ItemListSnapshot("Cabp_Tier", false,
            new[] { Member("en-tier-1", ("Title", "Bronze")) }, new[] { "en-tier-9" });

        var (extraction, _, log, _) = Extract(source, new Dictionary<string, object> { ["Tiers"] = 96 });

        Assert.Single(extraction!.Lists["Tiers"].Entries);
        Assert.Contains("relates to item 'en-tier-9'", Assert.Single(log));
        Assert.StartsWith("WARNING:", log[0]);
    }

    [Fact]
    public void ListSharedByTwoOwners_IsAWarning_AndEachDocumentCarriesTheEntries()
    {
        var source = Source().List(2, "Cabp_Tier", Member("en-tier-1", ("Title", "Bronze")));
        var log = new List<string>();
        var extractor = new ItemListExtractor(source, log.Add);

        var first = extractor.Extract("Cabp_PointsDashboard", new Dictionary<string, object> { ["Tiers"] = 2 }, null, "paragraph A");
        var again = extractor.Extract("Cabp_PointsDashboard", new Dictionary<string, object> { ["Tiers"] = 2 }, null, "paragraph A");
        Assert.Empty(log);   // the same document mapped twice is not a second owner

        var second = extractor.Extract("Cabp_PointsDashboard", new Dictionary<string, object> { ["Tiers"] = 2 }, null, "paragraph B");

        Assert.Single(first!.Lists["Tiers"].Entries);
        Assert.Single(again!.Lists["Tiers"].Entries);
        Assert.Single(second!.Lists["Tiers"].Entries);
        var warning = Assert.Single(log);
        Assert.StartsWith("WARNING:", warning);
        Assert.Contains("paragraph A (field 'Tiers') also uses", warning);
    }

    [Fact]
    public void NoteUncarried_NamesAListOnAnItemWhoseListsDoNotTravel_OncePerField()
    {
        var log = new List<string>();
        var extractor = new ItemListExtractor(Source(), log.Add);
        var fields = new Dictionary<string, object> { ["Tiers"] = 96, ["Heading"] = "x" };

        extractor.NoteUncarried("Cabp_PointsDashboard", fields, "grid row 1");
        extractor.NoteUncarried("Cabp_PointsDashboard", fields, "grid row 2");
        extractor.NoteUncarried("Cabp_PointsDashboard", new Dictionary<string, object> { ["Tiers"] = 0 }, "grid row 3");
        extractor.NoteUncarried("Swift-v2_Text", fields, "grid row 4");

        Assert.Contains("grid row 1", Assert.Single(log));
        Assert.Equal(96, fields["Tiers"]);
    }

    // -----------------------------------------------------------------------
    // Travel policy: the version boundary
    // -----------------------------------------------------------------------

    private static ProviderPredicateDefinition SqlTable(string table) =>
        new() { Name = table, ProviderType = "SqlTable", Table = table };

    private static ProviderPredicateDefinition Content() =>
        new() { Name = "Site", ProviderType = "Content", AreaId = 3, Path = "/" };

    [Fact]
    public void WithoutListRowPredicates_ListsTravelInsideTheirDocuments()
    {
        Assert.True(ItemListTravelPolicy.ListsTravelInDocuments(new[] { Content(), SqlTable("EcomShops") }));
        Assert.True(ItemListTravelPolicy.ListsTravelInDocuments(Array.Empty<ProviderPredicateDefinition>()));
    }

    [Theory]
    [InlineData("ItemList")]
    [InlineData("ItemListRelation")]
    [InlineData("[ItemList]")]
    [InlineData("dbo.itemlistrelation")]
    [InlineData("[dbo].[ItemList]")]
    public void AListRowPredicate_KeepsThe109Documents(string table)
    {
        var predicates = new[] { Content(), SqlTable(table) };

        Assert.False(ItemListTravelPolicy.ListsTravelInDocuments(predicates));
        Assert.Equal(table, Assert.Single(ItemListTravelPolicy.ListRowPredicates(predicates)).Table);
    }

    [Fact]
    public void AContentPredicateNamedItemList_IsNotAListRowPredicate()
    {
        var predicate = Content() with { Name = "ItemList", Table = "ItemList" };

        Assert.True(ItemListTravelPolicy.ListsTravelInDocuments(new[] { predicate }));
    }

    [Fact]
    public void EntryRowPredicates_AreTheSqlTablePredicatesOnTheEntryTypesTables()
    {
        var predicates = new[]
        {
            Content(), SqlTable("ItemType_Cabp_Tier"), SqlTable("[ItemType_Swift-v2_Slider_Item]"), SqlTable("ItemType_Cabp_PointsDashboard")
        };

        var doubled = ItemListTravelPolicy.EntryRowPredicates(predicates, new[] { "Cabp_Tier", "swift-v2_slider_item" });

        Assert.Equal(new[] { "ItemType_Cabp_Tier", "[ItemType_Swift-v2_Slider_Item]" }, doubled.Select(p => p.Table));
    }
}
