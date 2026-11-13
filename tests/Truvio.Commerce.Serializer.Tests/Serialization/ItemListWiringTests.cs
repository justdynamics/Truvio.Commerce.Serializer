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
        // Four writes, and the dry run's diff of the same write (owner decision D).
        Assert.Equal(5, Count(Deserializer, "ItemListWriter.Protect("));
        Assert.Contains("var protectedFields = ItemListWriter.Protect(excluded, lists);", Deserializer);
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
        var apply = Method(Deserializer, "private ItemListWriteResult ApplyItemLists(");

        Assert.Contains("if (lists is null || lists.Count == 0)\n            return new ItemListWriteResult { CountsOverwrites = _isDryRun };", apply);
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
    public void DwHost_CreatesAnEntryUnderANewNumberFromTheHostsOwnCounter_AndVerifiesIt()
    {
        // Owner decision A: the platform's id generator (ItemTypeId), moved on past ids the table
        // already holds, then inserted under that id through the item API.
        var create = Method(DwHost, "public string CreateItem(");

        Assert.Contains("using Dynamicweb.Content.Items.Activation;", DwHost);
        Assert.Equal(2, Count(create, "IdGenerator.Next(itemType)"));
        Assert.Contains("while (ItemExists(itemType, id))", create);
        Assert.Contains("var item = new Item(itemType) { Id = id };", create);
        Assert.Contains("item.Save(context);", create);
        Assert.Contains("if (!ItemExists(itemType, id))\n            throw new InvalidOperationException(", create);
        Assert.Contains("return id;", create);
    }

    [Fact]
    public void DwHost_KeepsTheEntryMapInTheSerializersOwnTable_AndCreatesItOnlyToWrite()
    {
        var find = Method(DwHost, "public IReadOnlyDictionary<string, string> FindMappedEntries(");
        find = find[..find.IndexOf("public void RecordEntry(", StringComparison.Ordinal)];
        var record = Method(DwHost, "public void RecordEntry(");
        var reverse = Method(DwHost, "public IReadOnlyDictionary<string, ItemListEntryMapping> GetEntryMappings(");

        // Reads treat a missing table as an empty map; only a write creates it.
        Assert.Contains("!EntryMapExists()", find);
        Assert.DoesNotContain("CreateSql", find);
        Assert.Contains("!EntryMapExists()", reverse);
        Assert.Contains("ItemListEntryMap.CreateSql", record);
        Assert.Contains("ItemListEntryMap.UpsertSql", record);
        Assert.Contains("WHERE [ItemType] = {0} AND [EntryKey] IN (", find);
        Assert.Contains("WHERE [ItemType] = {0} AND [ItemId] IN (", reverse);
        Assert.Contains("CREATE TABLE [dbo].[TruvioSerializerItemListEntry]", Truvio.Commerce.Serializer.Serialization.ItemListEntryMap.CreateSql);
        Assert.Contains("IF OBJECT_ID(", Truvio.Commerce.Serializer.Serialization.ItemListEntryMap.CreateSql);
    }

    [Fact]
    public void DwHost_PutsBackOnlyAnItemInstanceTypeTheSaveEmptied()
    {
        var keep = Method(DwHost, "public void KeepInstanceType(");

        Assert.Contains("SET [ItemInstanceType] = {0} WHERE [Id] = {1} AND ISNULL([ItemInstanceType], '') = ''", keep);
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
        // The one statement it writes itself restores an entry's Sort from its relation.
        Assert.Contains("\"UPDATE it SET it.[Sort] = ilr.[ItemListRelationSort] FROM [\"", DwHost);
        Assert.Contains("AND it.[Sort] <> ilr.[ItemListRelationSort]", DwHost);
    }

    [Fact]
    public void DwHost_NeverDeletesAnItem()
    {
        // Owner decision of 2026-11-13: a delivery never deletes a list entry.
        Assert.DoesNotContain("DeleteById", DwHost);
        Assert.DoesNotContain("DeleteItem", DwHost);
        Assert.DoesNotContain(".Delete(", DwHost);
    }

    // -----------------------------------------------------------------------
    // Dry run: list changes count
    // -----------------------------------------------------------------------

    [Fact]
    public void DryRun_ParagraphReplace_CountsItsListDiff_BeforeDecidingUnchanged()
    {
        var write = Method(Deserializer, "private void DeserializeParagraph(");
        var log = Method(Deserializer, "private void LogDryRunParagraphUpdate(");

        var apply = write.IndexOf("var paragraphLists = ApplyItemLists(", StringComparison.Ordinal);
        var diff = write.IndexOf("LogDryRunParagraphUpdate(dto, existing, ctx, paragraphLists);", StringComparison.Ordinal);
        Assert.True(apply >= 0 && diff > apply);
        Assert.True(log.IndexOf("AddListDiffs(diffs, lists);", StringComparison.Ordinal) < log.IndexOf("if (diffs.Count == 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void DryRun_ParagraphReplace_ComparesWhatTheWriteWouldChange()
    {
        // Owner decision D: GlobalRecordPageGuid and MasterParagraphGuid as links, Text and every
        // other field against the item as SaveItemFields writes it, links resolved, permissions
        // against the host's set. Never the paragraph's own Text column, never ToString().
        var log = Method(Deserializer, "private void LogDryRunParagraphUpdate(");

        Assert.Contains("DryRunDiff.GuidLink(\"GlobalRecordPageGuid\", globalRecordGuid, existing.GlobalRecordPageID,", log);
        Assert.Contains("DryRunDiff.GuidLink(MasterLinkRestorer.MasterParagraphGuidField, masterGuid, existing.MasterParagraphID,", log);
        Assert.Contains("DryRunItemFieldDiffs(existing.ItemType, existing.ItemId, dto.Fields, dto.ItemLists, paraExclude)", log);
        Assert.Contains("DryRunDiff.SamePermissions(dto.Permissions, _dryRunPermissions.MapPermissions(existing.ID, \"Paragraph\"))", log);
        Assert.DoesNotContain("existing.Text", log);
        Assert.DoesNotContain("?.ToString();", log);

        var fields = Method(Deserializer, "private IReadOnlyList<string> DryRunItemFieldDiffs(");
        Assert.Contains("ItemManager.Storage.Open(itemType).SelectById(itemId)", fields);
        Assert.Contains("ResolveLinkFields(", fields);
        Assert.Contains("DryRunTarget().Resolver", fields);
        Assert.Contains("ItemListWriter.Protect(excluded, lists)", fields);
    }

    [Fact]
    public void DryRun_PageReplace_ComparesItemFieldsAndPermissionsLikeTheWrite()
    {
        var log = Method(Deserializer, "private void LogDryRunPageUpdate(");

        Assert.Contains("DryRunItemFieldDiffs(existing.ItemType, existing.ItemId, dto.Fields, dto.ItemLists, pageExclude)", log);
        Assert.Contains("DryRunDiff.SamePermissions(dto.Permissions, _dryRunPermissions.MapPermissions(existing.ID, \"Page\"))", log);
        Assert.DoesNotContain("existing.Item?[kvp.Key]", log);
    }

    [Fact]
    public void DryRun_PageReplace_CountsItsListDiff_BeforeDecidingUnchanged()
    {
        var write = Method(Deserializer, "private int DeserializePage(");
        var log = Method(Deserializer, "private void LogDryRunPageUpdate(");

        Assert.Contains("LogDryRunPageUpdate(dto, existingPage, ctx, pageLists);", write);
        Assert.Contains("LogMergeFillDryRun(dto, existingPage, mergeExclude, ctx, pageListFill);", write);
        Assert.True(log.IndexOf("AddListDiffs(diffs, lists);", StringComparison.Ordinal) < log.IndexOf("if (diffs.Count == 0)", StringComparison.Ordinal));
        Assert.Contains("if (diffs.Count == 0 && listChanges == 0) ctx.Skipped++;", Method(Deserializer, "private void LogMergeFillDryRun("));
    }

    [Fact]
    public void DryRun_ParagraphMerge_CountsItsListChanges()
    {
        var merge = Method(Deserializer, "private void MergeParagraph(");

        Assert.Contains("if (filled == 0 && listFill.Changes == 0) ctx.Skipped++; else ctx.Updated++;", merge);
    }

    [Fact]
    public void ListCounts_FlowIntoTheEntryResult()
    {
        Assert.Equal(2, Count(Deserializer, "ItemLists = ItemListTotals"));
        Assert.Contains("Log(aggregated.ItemLists.Summary(_isDryRun));", Deserializer);
        Assert.Contains("ItemLists = result.ItemLists,", Read("Providers", "Content", "ContentProvider.cs"));
        Assert.Contains("ItemLists = r.ItemLists,", Read("Reporting", "EntryOutcome.cs"));
        Assert.Contains("ItemLists = o.ItemLists.IsZero ? null : o.ItemLists,", Read("AdminUI", "Commands", "DeserializeCommand.cs"));
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
