using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R7: the places where item lists are wired into the serialize and
/// deserialize pipelines. Those run on live Dynamicweb objects, so the wiring is pinned
/// structurally against the source, the same way the other ContentDeserializer suites do; the
/// rules themselves are exercised for real in <see cref="ItemListWriterTests"/>,
/// <see cref="ItemListPlannerTests"/> and <see cref="ItemListExtractorTests"/>. The link sweep
/// over list entries is pure and runs here.
/// </summary>
public class ItemListWiringTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Deserializer = Read("Serialization", "ContentDeserializer.cs");
    private static readonly string Mapper = Read("Serialization", "ContentMapper.cs");
    private static readonly string Serializer = Read("Serialization", "ContentSerializer.cs");
    private static readonly string DwHost = Read("Serialization", "DwItemListHost.cs");

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { Root, "src", "Truvio.Commerce.Serializer" }.Concat(path).ToArray()))
            .ReplaceLineEndings("\n");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var nextInternal = source.IndexOf("\n    internal ", start + signature.Length, StringComparison.Ordinal);
        if (nextInternal >= 0 && (next < 0 || nextInternal < next)) next = nextInternal;
        return next < 0 ? source[start..] : source[start..next];
    }

    private static int Count(string source, string text)
    {
        int count = 0, index = 0;
        while ((index = source.IndexOf(text, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += text.Length;
        }
        return count;
    }

    // -----------------------------------------------------------------------
    // Deserialize: every lifecycle path of a paragraph and a page delivers the lists
    // -----------------------------------------------------------------------

    [Fact]
    public void Paragraph_CreateAndReplace_OverwriteItsLists_MergeFillsThem()
    {
        var write = Method(Deserializer, "private void DeserializeParagraph(");
        var merge = Method(Deserializer, "private void MergeParagraph(");

        // insert, dry-run update, update
        Assert.Equal(3, Count(write, "dto.ItemLists, dto.ItemListOptions, ItemListLifecycle.Overwrite"));
        Assert.DoesNotContain("ItemListLifecycle.Fill", write);
        // dry run and real run
        Assert.Equal(2, Count(merge, "dto.ItemLists, dto.ItemListOptions, ItemListLifecycle.Fill"));
        Assert.DoesNotContain("ItemListLifecycle.Overwrite", merge);
        Assert.Contains("ItemListIdentity.ParagraphOwner(dto.ParagraphUniqueId)", write);
        Assert.Contains("ItemListIdentity.ParagraphOwner(dto.ParagraphUniqueId)", merge);
    }

    [Fact]
    public void Page_CreateAndReplace_OverwriteItsLists_MergeFillsThem()
    {
        var write = Method(Deserializer, "private int DeserializePage(");

        // insert, dry-run update, update
        Assert.Equal(3, Count(write, "dto.ItemLists, dto.ItemListOptions, ItemListLifecycle.Overwrite"));
        // dry-run merge, merge
        Assert.Equal(2, Count(write, "dto.ItemLists, dto.ItemListOptions, ItemListLifecycle.Fill"));
        Assert.Equal(5, Count(write, "ItemListIdentity.PageOwner(dto.PageUniqueId)"));
    }

    [Fact]
    public void KeptParagraph_ReturnsBeforeAnyListWrite()
    {
        var write = Method(Deserializer, "private void DeserializeParagraph(");
        var existingBranch = write[write.IndexOf("Content ownership R1: an existing paragraph stamped onExisting: keep", StringComparison.Ordinal)..];

        var keep = existingBranch.IndexOf("_kept.ParagraphIds.Add(existingParagraphId);", StringComparison.Ordinal);
        var keepReturn = existingBranch.IndexOf("return;", keep, StringComparison.Ordinal);
        var firstListWrite = existingBranch.IndexOf("ApplyItemLists(", StringComparison.Ordinal);

        Assert.True(keep >= 0 && keepReturn > keep && firstListWrite > keepReturn);
    }

    [Fact]
    public void FieldWrites_NeverCarryAListField_AndNeverNullOne()
    {
        // Every SaveItemFields / MergeItemFields call that takes a paragraph's or page's fields
        // takes them without the document's list fields; the source-wins ones protect them too.
        Assert.Equal(6, Count(Deserializer, "ItemListWriter.FieldsWithoutLists(dto.Fields, dto.ItemLists)"));
        Assert.Equal(4, Count(Deserializer, "ItemListWriter.Protect("));
        foreach (var signature in new[] { "private int DeserializePage(", "private void DeserializeParagraph(", "private void MergeParagraph(" })
            Assert.DoesNotContain("ItemId, dto.Fields,", Method(Deserializer, signature));
    }

    [Fact]
    public void ListEntries_AreWrittenByTheSameFieldWritersAsAParagraph()
    {
        Assert.Contains("(itemType, itemId, fields, protectedFields) => SaveItemFields(itemType, itemId, fields, protectedFields)", Deserializer);
        Assert.Contains("return MergeItemFields(itemType, itemId, fields, excludedFields, ref left);", Deserializer);
        Assert.Contains("itemListHost ?? new DwItemListHost(log)", Deserializer);
    }

    [Fact]
    public void DocumentWithListsButNoItem_Throws_InsteadOfDroppingTheLists()
    {
        var apply = Method(Deserializer, "private int ApplyItemLists(");

        Assert.Contains("if (lists is null || lists.Count == 0)\n            return 0;", apply);
        Assert.Contains("throw new InvalidOperationException(", apply);
        Assert.Contains("has no item on this host to hold them", apply);
    }

    [Fact]
    public void LinkPass_VisitsTheEntriesTheRunWrote_AfterTheirOwnersItem()
    {
        var pass = Method(Deserializer, "private void ResolveLinksInArea(");

        Assert.Contains(
            "ResolveLinksInItemFields(page.ItemType, page.ItemId, resolver);\n" +
            "                ResolveLinksInItemListEntries(page.ItemType, page.ItemId, resolver);", pass);
        Assert.Contains(
            "ResolveLinksInItemFields(para.ItemType, para.ItemId, resolver);\n" +
            "                ResolveLinksInItemListEntries(para.ItemType, para.ItemId, resolver);", pass);

        // A kept paragraph is skipped before either call.
        var keptSkip = pass.IndexOf("if (kept is not null && !kept.ResolveParagraph(page.ID, para.ID))", StringComparison.Ordinal);
        Assert.True(keptSkip >= 0 && keptSkip < pass.IndexOf("ResolveLinksInItemListEntries(para.ItemType", StringComparison.Ordinal));

        var entries = Method(Deserializer, "private void ResolveLinksInItemListEntries(");
        Assert.Contains("foreach (var member in _itemLists.WrittenMembers(ownerItemType, ownerItemId))", entries);
        Assert.Contains("ResolveLinksInItemFields(member.ItemType, member.ItemId, resolver);", entries);
    }

    // -----------------------------------------------------------------------
    // Serialize
    // -----------------------------------------------------------------------

    [Fact]
    public void Mapper_ExtractsTheListsOfParagraphAndPageItems_AndOnlyNotesTheOthers()
    {
        Assert.Contains("var itemLists = _itemLists?.Extract(paragraph.ItemType, fields, effectiveExcludeFields,", Mapper);
        Assert.Contains("var itemLists = _itemLists?.Extract(page.ItemType, fields, effectiveExcludeFields, pageLabel,", Mapper);
        Assert.Equal(2, Count(Mapper, "ItemLists = itemLists?.Lists,"));
        Assert.Equal(2, Count(Mapper, "ItemListOptions = itemLists?.Options,"));
        Assert.Equal(2, Count(Mapper, "type => ExclusionMerger.ForItemType(excludeFields, excludeFieldsByItemType, type)"));
        // grid row, area and page property items: their lists are named, not carried
        Assert.Equal(3, Count(Mapper, "_itemLists?.NoteUncarried("));
    }

    [Fact]
    public void Serializer_CarriesListsOnlyWhenNoPredicateShipsThemAsRows_AndReadsTheOnDiskConfigurationForThat()
    {
        Assert.Contains("_configuredPredicates = configuration.Predicates.Concat(ReadOnDiskPredicates()).ToList();", Serializer);
        Assert.Contains(
            "if (ItemListTravelPolicy.ListsTravelInDocuments(_configuredPredicates))\n" +
            "            _itemListExtractor = new ItemListExtractor(itemListSource ?? new DwItemListHost(log), log);\n" +
            "        _mapper = new ContentMapper(_referenceResolver, _itemListExtractor);", Serializer);
        Assert.Contains("Item lists stay outside the content documents", Serializer);
        Assert.Contains("ItemListTravelPolicy.EntryRowPredicates(_configuredPredicates, _itemListExtractor.MemberItemTypes)", Serializer);
        Assert.Contains("Log($\"WARNING: SqlTable predicate '{predicate.Name}' ships table '{predicate.Table}'", Serializer);
    }

    [Fact]
    public void Serializer_NamesTheEntryItemTypesInTheTemplateManifest()
    {
        Assert.Contains("refs = MergeTemplateReferences(refs, _itemListExtractor.MemberItemTypes", Serializer);
        Assert.Contains("Kind = \"item-type\",", Serializer);
    }

    // -----------------------------------------------------------------------
    // The Dynamicweb half
    // -----------------------------------------------------------------------

    [Fact]
    public void DwHost_ReadsListsAndRelationsUncached_InListOrder()
    {
        Assert.Contains("SELECT [ItemListItemType] FROM [ItemList] WHERE [ItemListId] = {0}", DwHost);
        Assert.Contains(
            "SELECT [ItemListRelationItemId] FROM [ItemListRelation] WHERE [ItemListRelationItemListId] = {0} \" +\n" +
            "            \"ORDER BY [ItemListRelationSort] ASC, [ItemListRelationId] ASC", DwHost);
        Assert.Contains("repository.SelectById(", DwHost);
        Assert.DoesNotContain("Services.Items.GetItem", DwHost);
    }

    [Fact]
    public void DwHost_CreatesAnEntryUnderExactlyTheGivenId_AndVerifiesIt()
    {
        var create = Method(DwHost, "public void CreateItem(");

        Assert.Contains("var item = new Item(itemType) { Id = itemId };", create);
        Assert.Contains("item.Save(context);", create);
        Assert.Contains("if (!ItemExists(itemType, itemId))\n            throw new InvalidOperationException(", create);
    }

    [Fact]
    public void DwHost_NeverWritesAListIdOrARelationRowItself()
    {
        // The list row comes from ItemList.Save (the host's identity), relations from
        // ItemList.SaveRelations: no IDENTITY_INSERT, no hand-written relation insert.
        Assert.DoesNotContain("IDENTITY_INSERT", DwHost);
        Assert.DoesNotContain("INSERT INTO", DwHost);
        Assert.DoesNotContain("DELETE FROM", DwHost);
        Assert.Contains("var list = new ItemList(memberItemType);\n        list.Save();", DwHost);
        Assert.Contains("list.SaveRelations(orderedItemIds);", DwHost);
        Assert.Contains("repository.DeleteById(itemId, cleanup: false);", DwHost);
        // The one statement it writes itself restores an entry's Sort from its relation.
        Assert.Contains("\"UPDATE it SET it.[Sort] = ilr.[ItemListRelationSort] FROM [\"", DwHost);
        Assert.Contains("AND it.[Sort] <> ilr.[ItemListRelationSort]", DwHost);
    }

    // -----------------------------------------------------------------------
    // Link sweep over list entries (pure)
    // -----------------------------------------------------------------------

    private static SerializedPage PageWithListLink(string link, bool nested = false, bool onPage = false)
    {
        var entry = new SerializedItemListEntry { Id = "slide-1", Fields = new Dictionary<string, object> { ["Link"] = link } };
        if (nested)
            entry = new SerializedItemListEntry
            {
                Id = "tier-1",
                ItemLists = new Dictionary<string, SerializedItemList> { ["Links"] = new(new[] { entry }) }
            };
        var lists = new Dictionary<string, SerializedItemList> { ["Slides"] = new(new[] { entry }) };

        var paragraph = new SerializedParagraph
        {
            ParagraphUniqueId = Guid.NewGuid(),
            SourceParagraphId = 500,
            SortOrder = 1,
            ItemLists = onPage ? null : lists
        };
        return ContentTreeBuilder.BuildSinglePage("Home") with
        {
            SourcePageId = 10,
            Paragraphs = new List<SerializedParagraph> { paragraph },
            ItemLists = onPage ? lists : null
        };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Sweep_FindsAnUnresolvableLinkInAListEntry_AndNamesTheEntry(bool nested, bool onPage)
    {
        var result = new BaselineLinkSweeper().Sweep(new List<SerializedPage> { PageWithListLink("Default.aspx?ID=999", nested, onPage) });

        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal(999, unresolved.UnresolvablePageId);
        Assert.Equal("Fields.Link", unresolved.FieldName);
        Assert.Contains("item list Slides entry", unresolved.SourcePageIdentifier);
        Assert.Contains("slide-1", unresolved.SourcePageIdentifier);
    }

    [Fact]
    public void Sweep_CountsAResolvableLinkInAListEntry()
    {
        var result = new BaselineLinkSweeper().Sweep(new List<SerializedPage> { PageWithListLink("Default.aspx?ID=10#500") });

        Assert.Empty(result.Unresolved);
        Assert.Equal(1, result.ResolvedCount);
    }
}
