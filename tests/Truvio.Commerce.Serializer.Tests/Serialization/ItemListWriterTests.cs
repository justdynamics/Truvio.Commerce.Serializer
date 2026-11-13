using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Truvio.Commerce.Serializer.Tests.TestHelpers;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R7: what a delivery does to the item lists of a document's item, per
/// lifecycle, run against an in-memory host. The Dynamicweb half (<c>DwItemListHost</c>) needs a
/// live runtime and is pinned at source level in <see cref="ItemListWiringTests"/>.
/// </summary>
public class ItemListWriterTests
{
    private const string OwnerType = "Cabp_PointsDashboard";
    private const string OwnerId = "7";
    private const string TierType = "Cabp_Tier";
    private const string LinkType = "Cabp_TierLink";

    private static readonly Guid ParagraphGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string OwnerKey = ItemListIdentity.ParagraphOwner(ParagraphGuid);
    private static readonly ItemListOwner Owner = new(OwnerType, OwnerId, OwnerKey, $"paragraph {ParagraphGuid}");

    private static FakeItemListHost Host() =>
        new FakeItemListHost()
            .WithListField(OwnerType, "Tiers", TierType)
            .WithListField(TierType, "Links", LinkType)
            .WithItem(OwnerType, OwnerId, ("Heading", "Your points"));

    private static SerializedItemListEntry Entry(string id, string title, string? perk = null,
        Dictionary<string, SerializedItemList>? nested = null)
    {
        var fields = new Dictionary<string, object> { ["Title"] = title };
        if (perk is not null) fields["Perk"] = perk;
        return new SerializedItemListEntry { Id = id, Fields = fields, ItemLists = nested };
    }

    private static Dictionary<string, SerializedItemList> Tiers(params SerializedItemListEntry[] entries) =>
        new() { ["Tiers"] = new SerializedItemList(entries) };

    /// <summary>The host id the entry map gives a document entry of the owner's Tiers list.</summary>
    private static string Mapped(FakeItemListHost host, string sourceId) =>
        host.MappedId(OwnerKey, "Tiers", sourceId) ?? throw new InvalidOperationException($"no map row for '{sourceId}'");

    // -----------------------------------------------------------------------
    // No lists: documents written through 1.0.9-beta
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ItemListLifecycle.Overwrite)]
    [InlineData(ItemListLifecycle.Fill)]
    public void DocumentWithoutItemLists_WritesNothing(ItemListLifecycle lifecycle)
    {
        var host = Host();
        var existing = host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        var result = host.Writer().Apply(Owner, null, null, lifecycle);

        Assert.Equal(0, result.Changes);
        Assert.Empty(host.Journal);
        Assert.Equal(existing, host.GetListId(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(new[] { "en-tier-1" }, host.Members(OwnerType, OwnerId, "Tiers"));
    }

    // -----------------------------------------------------------------------
    // Empty target: the list and its entries are created, in order, under host numbers
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ItemListLifecycle.Overwrite)]
    [InlineData(ItemListLifecycle.Fill)]
    public void EmptyTarget_CreatesTheListAndItsEntriesInDocumentOrder(ItemListLifecycle lifecycle)
    {
        var host = Host();

        var result = host.Writer().Apply(Owner,
            Tiers(Entry("en-tier-2", "Silver"), Entry("en-tier-1", "Bronze"), Entry("en-tier-3", "Gold")),
            null, lifecycle);

        Assert.Equal(3, result.Created);
        Assert.Equal(1, result.ListsCreated);
        Assert.Equal(new[] { Mapped(host, "en-tier-2"), Mapped(host, "en-tier-1"), Mapped(host, "en-tier-3") },
            host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal("Silver", host.Field(TierType, Mapped(host, "en-tier-2"), "Title"));
        Assert.True(host.GetListId(OwnerType, OwnerId, "Tiers") > 0);
        Assert.Equal(TierType, host.GetListItemType(host.GetListId(OwnerType, OwnerId, "Tiers")));
    }

    [Fact]
    public void EmptyListOnAnEmptyTarget_CreatesNoList()
    {
        var host = Host();

        var result = host.Writer().Apply(Owner, Tiers(), null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Changes);
        Assert.Empty(host.Lists);
        Assert.Equal(0, host.GetListId(OwnerType, OwnerId, "Tiers"));
    }

    [Fact]
    public void NewEntry_GetsTheNextNumberOfTheHostsCounter_WhateverItsDocumentId()
    {
        // Owner decision A: a text id (en-tier-1) and a source number (12) both become host numbers.
        var host = Host().WithCounter(TierType, 40);

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze"), Entry("12", "Silver")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { "41", "42" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.All(host.Members(OwnerType, OwnerId, "Tiers"), id => Assert.True(ItemListIdentity.IsHostAllocated(id)));
        Assert.Equal(42, host.Counter(TierType));
        Assert.Null(host.Item(TierType, "en-tier-1"));
        Assert.Null(host.Item(TierType, "12"));
        Assert.Equal("Silver", host.Field(TierType, "42", "Title"));
    }

    [Fact]
    public void NewEntry_RecordsItsKey_WithTheSourceId_InTheEntryMap()
    {
        var host = Host().WithCounter(TierType, 40);

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        var row = Assert.Single(host.EntryMap.Values);
        Assert.Equal(ItemListIdentity.EntryKey(OwnerKey, "Tiers", "en-tier-1"), row.EntryKey);
        Assert.Equal(TierType, row.ItemType);
        Assert.Equal("41", row.ItemId);
        Assert.Equal(OwnerKey, row.OwnerKey);
        Assert.Equal("Tiers", row.Field);
        Assert.Equal("en-tier-1", row.SourceId);
    }

    [Fact]
    public void ACounterBehindTheTable_IsMovedOnPastTheIdsItHolds_NothingIsOverwritten()
    {
        // Rows delivered by the SqlTable provider before R7 did not move ItemTypeId.
        var host = Host().WithCounter(TierType, 2)
            .WithItem(TierType, "3", ("Title", "An entry that came as a row"))
            .WithItem(TierType, "4", ("Title", "Another"));

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { "5" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal("An entry that came as a row", host.Field(TierType, "3", "Title"));
        Assert.Equal(5, host.Counter(TierType));
    }

    [Fact]
    public void NumericId_NeverTouchesAnUnrelatedHostItemWithTheSameNumber()
    {
        var host = Host().WithItem(TierType, "12", ("Title", "An editor's own tier")).WithCounter(TierType, 12);

        host.Writer().Apply(Owner, Tiers(Entry("12", "Silver")), null, ItemListLifecycle.Overwrite);

        Assert.Equal("An editor's own tier", host.Field(TierType, "12", "Title"));
        Assert.Equal("Silver", host.Field(TierType, Mapped(host, "12"), "Title"));
        Assert.NotEqual("12", Mapped(host, "12"));
        Assert.DoesNotContain(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void TextIdUsedByAnUnrelatedItem_IsNeitherTakenOverNorAWarning()
    {
        var host = Host().WithItem(TierType, "en-tier-1", ("Title", "Someone else's"));

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.Equal("Someone else's", host.Field(TierType, "en-tier-1", "Title"));
        Assert.Equal(new[] { Mapped(host, "en-tier-1") }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.DoesNotContain(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Overwrite (Replace): the delivered entries as the source has them, nothing deleted
    // -----------------------------------------------------------------------

    [Fact]
    public void Overwrite_UpdatesMatchedEntries_SourceWins_AndNullsFieldsTheDocumentDoesNotCarry()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("Title", "Old bronze"), ("Perk", "Free shipping"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        var result = host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(1, result.Overwritten);
        Assert.Equal(0, result.Created);
        Assert.Equal("Bronze", host.Field(TierType, "en-tier-1", "Title"));
        Assert.Null(host.Field(TierType, "en-tier-1", "Perk"));
    }

    [Fact]
    public void Overwrite_KeepsEntriesAbsentFromTheDocument_AfterTheDelivered_AndReportsEachAsInformation()
    {
        var host = Host();
        host.WithItem(TierType, "editor-added", ("Title", "Platinum"));
        var listId = host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1", "editor-added", "en-tier-2");

        var result = host.Writer().Apply(Owner,
            Tiers(Entry("en-tier-1", "Bronze"), Entry("en-tier-2", "Silver")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(1, result.HostOnly);
        Assert.Equal(new[] { "en-tier-1", "en-tier-2", "editor-added" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal("Platinum", host.Field(TierType, "editor-added", "Title"));
        Assert.DoesNotContain(host.Journal, line => line.Contains("editor-added") && !line.StartsWith("members of list", StringComparison.Ordinal));
        // Owner decision B: information, not a WARNING, so strict mode does not fail on it.
        Assert.DoesNotContain(host.Log, line => line.TrimStart().StartsWith("WARNING", StringComparison.OrdinalIgnoreCase));
        var note = Assert.Single(host.Log, line => line.StartsWith("Host-only list entry (information)", StringComparison.Ordinal));
        Assert.Contains("item list 'Tiers'", note);
        Assert.Contains($"(list {listId})", note);
        Assert.Contains("entry 'editor-added'", note);
        Assert.Contains($"paragraph {ParagraphGuid}", note);
        Assert.Contains("never deletes", note);
    }

    [Fact]
    public void Overwrite_TakesTheDocumentOrder()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "b", "c");

        host.Writer().Apply(Owner, Tiers(Entry("c", "C"), Entry("a", "A"), Entry("b", "B")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { "c", "a", "b" }, host.Members(OwnerType, OwnerId, "Tiers"));
    }

    [Fact]
    public void Overwrite_EmptyDocumentList_KeepsTheHostEntries_AndReportsThem()
    {
        var host = Host();
        var listId = host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "b");

        var result = host.Writer().Apply(Owner, Tiers(), null, ItemListLifecycle.Overwrite);

        Assert.Equal(2, result.HostOnly);
        Assert.Equal(new[] { "a", "b" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(listId, host.GetListId(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(2, host.ItemCount(TierType));
        Assert.Equal(2, host.Log.Count(line => line.StartsWith("Host-only list entry (information)", StringComparison.Ordinal)));
        Assert.Equal(0, result.Changes);
    }

    [Fact]
    public void Overwrite_HostOnlyEntries_KeepTheirRelativeOrder_BehindTheDelivered()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "x", "a", "y", "b");

        var result = host.Writer().Apply(Owner, Tiers(Entry("b", "B"), Entry("a", "A"), Entry("new", "N")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { "b", "a", Mapped(host, "new"), "x", "y" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(2, result.HostOnly);
        Assert.Equal(1, result.Reordered);
    }

    [Fact]
    public void Overwrite_NeverDeletesAnItem()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "b");

        host.Writer().Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Overwrite);

        Assert.DoesNotContain(host.Journal, line => line.StartsWith("delete", StringComparison.Ordinal));
        Assert.NotNull(host.Item(TierType, "b"));
        Assert.Equal(new[] { "a", "b" }, host.Members(OwnerType, OwnerId, "Tiers"));
    }

    [Fact]
    public void Overwrite_RedeliveryWithAHostOnlyEntry_ChangesNoMembership()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "editor-added");
        var lists = Tiers(Entry("a", "A"), Entry("b", "B"));
        host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);
        Assert.Equal(new[] { Mapped(host, "a"), Mapped(host, "b"), "editor-added" }, host.Members(OwnerType, OwnerId, "Tiers"));
        host.Journal.Clear();

        var result = host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Changes);
        Assert.Equal(1, result.HostOnly);
        Assert.DoesNotContain(host.Journal, line => line.StartsWith("members of list", StringComparison.Ordinal));
    }

    [Fact]
    public void Overwrite_Redelivery_MatchesThroughTheEntryMap_CreatesAndRecordsNothing()
    {
        var host = Host();
        var lists = Tiers(Entry("en-tier-1", "Bronze"), Entry("12", "Silver"));
        host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);
        var listId = host.GetListId(OwnerType, OwnerId, "Tiers");
        var mapRows = host.EntryMap.Count;
        host.Journal.Clear();

        var result = host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Changes);
        Assert.Equal(2, result.Overwritten);
        Assert.Equal(listId, host.GetListId(OwnerType, OwnerId, "Tiers"));
        // The entries are written again, the relations and the entry map are not; an item save
        // resets the item's own Sort, so the position is put back.
        Assert.Equal(new[] { $"save {TierType}/{Mapped(host, "en-tier-1")}", $"save {TierType}/{Mapped(host, "12")}", $"sort of list {listId}" }, host.Journal);
        Assert.Equal(2, host.ItemCount(TierType));
        Assert.Equal(mapRows, host.EntryMap.Count);
    }

    // -----------------------------------------------------------------------
    // Fill (Merge): keep what the host has, add what is missing
    // -----------------------------------------------------------------------

    [Fact]
    public void Fill_KeepsSetFields_FillsEmptyOnes_AndNeverNulls()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("Title", "Editor's bronze"), ("Perk", ""), ("Badge", "/Files/bronze.svg"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        var result = host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze", perk: "Free shipping")), null, ItemListLifecycle.Fill);

        Assert.Equal(1, result.Filled);
        Assert.Equal(1, result.FilledFields);
        Assert.Equal("Editor's bronze", host.Field(TierType, "en-tier-1", "Title"));
        Assert.Equal("Free shipping", host.Field(TierType, "en-tier-1", "Perk"));
        Assert.Equal("/Files/bronze.svg", host.Field(TierType, "en-tier-1", "Badge"));
    }

    [Fact]
    public void Fill_ThatFillsAField_PutsTheEntrysPositionBack_WithoutRewritingTheRelations()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("Title", ""));
        var listId = host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Fill);

        Assert.Equal(new[] { $"fill {TierType}/en-tier-1 (1)", $"sort of list {listId}" }, host.Journal);
    }

    [Fact]
    public void Fill_KeepsHostOnlyEntriesAndTheHostOrder_AndAppendsMissingEntries()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-2", "editor-added", "en-tier-1");

        var result = host.Writer().Apply(Owner,
            Tiers(Entry("en-tier-1", "Bronze"), Entry("en-tier-3", "Gold"), Entry("en-tier-2", "Silver")),
            null, ItemListLifecycle.Fill);

        Assert.Equal(1, result.HostOnly);
        Assert.Equal(1, result.Created);
        // Under fill a host-only entry is the expected case (R2): nothing is said.
        Assert.DoesNotContain(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal) || line.StartsWith("Host-only", StringComparison.Ordinal));
        Assert.Equal(new[] { "en-tier-2", "editor-added", "en-tier-1", Mapped(host, "en-tier-3") }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.NotNull(host.Item(TierType, "editor-added"));
        Assert.Equal("Gold", host.Field(TierType, Mapped(host, "en-tier-3"), "Title"));
    }

    [Fact]
    public void Fill_EmptyDocumentList_LeavesTheHostListAlone()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "b");

        var result = host.Writer().Apply(Owner, Tiers(), null, ItemListLifecycle.Fill);

        Assert.Equal(0, result.Changes);
        Assert.Empty(host.Journal);
        Assert.Equal(new[] { "a", "b" }, host.Members(OwnerType, OwnerId, "Tiers"));
    }

    [Fact]
    public void Fill_SecondDelivery_ChangesNothing()
    {
        var host = Host();
        var lists = Tiers(
            Entry("en-tier-1", "Bronze", perk: "Free shipping"),
            Entry("12", "Silver", nested: new Dictionary<string, SerializedItemList>
            {
                ["Links"] = new(new[] { Entry("3", "Docs"), Entry("link-b", "Support") })
            }));
        host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Fill);
        var members = host.Members(OwnerType, OwnerId, "Tiers").ToList();
        var tierCount = host.ItemCount(TierType);
        var linkCount = host.ItemCount(LinkType);
        var mapRows = host.EntryMap.Count;
        host.Journal.Clear();

        var result = host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Fill);

        Assert.Equal(0, result.Changes);
        Assert.Empty(host.Journal);
        Assert.Equal(members, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(tierCount, host.ItemCount(TierType));
        Assert.Equal(linkCount, host.ItemCount(LinkType));
        Assert.Equal(4, mapRows);
        Assert.Equal(mapRows, host.EntryMap.Count);
    }

    // -----------------------------------------------------------------------
    // Identity across hosts
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ItemListLifecycle.Overwrite)]
    [InlineData(ItemListLifecycle.Fill)]
    public void EntriesDeliveredAsRowsBefore_AreMatchedByTheirIds_NumberOrText_NeverRenamedOrDuplicated(ItemListLifecycle lifecycle)
    {
        // A host that received the list the 1.0.9-beta way holds the entries under the source ids,
        // numbers and text ids alike (CABP holds 234 text-id entries such as en-tier-1).
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "12", "en-tier-1");

        var result = host.Writer().Apply(Owner, Tiers(Entry("12", "Bronze"), Entry("en-tier-1", "Silver")), null, lifecycle);

        Assert.Equal(0, result.Created);
        Assert.Equal(new[] { "12", "en-tier-1" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(2, host.ItemCount(TierType));
        Assert.Empty(host.EntryMap);
    }

    [Fact]
    public void TheEntryMapClaim_WinsOverAnEqualIdOfAnotherMember()
    {
        // B created 41 for the source's "12"; an editor on B later added an entry that got 12.
        var host = Host().WithItem(TierType, "41", ("Title", "Silver")).WithItem(TierType, "12", ("Title", "Editor's"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "41", "12");
        host.WithMapping(OwnerKey, "Tiers", "12", TierType, "41");

        var result = host.Writer().Apply(Owner, Tiers(Entry("12", "Silver (new)")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Created);
        Assert.Equal("Silver (new)", host.Field(TierType, "41", "Title"));
        Assert.Equal("Editor's", host.Field(TierType, "12", "Title"));
        Assert.Equal(1, result.HostOnly);
    }

    [Fact]
    public void AnEntryMapRowWhoseEntryLeftTheList_MatchesNothing_AndIsMovedToTheNewEntry()
    {
        var host = Host().WithCounter(TierType, 50);
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "33");
        host.WithMapping(OwnerKey, "Tiers", "en-tier-1", TierType, "9");   // 9 is not in the list

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Fill);

        Assert.Equal(new[] { "33", "51" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal("51", Mapped(host, "en-tier-1"));
    }

    [Fact]
    public void TwoOwners_WithTheSameEntryIds_GetSeparateEntries()
    {
        var otherGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var otherKey = ItemListIdentity.ParagraphOwner(otherGuid);
        var otherOwner = new ItemListOwner(OwnerType, "8", otherKey, $"paragraph {otherGuid}");
        var host = Host().WithItem(OwnerType, "8");

        host.Writer().Apply(Owner, Tiers(Entry("1", "Bronze")), null, ItemListLifecycle.Overwrite);
        host.Writer().Apply(otherOwner, Tiers(Entry("1", "Platinum")), null, ItemListLifecycle.Overwrite);
        host.Writer().Apply(otherOwner, Tiers(Entry("1", "Platinum")), null, ItemListLifecycle.Overwrite);

        var first = Assert.Single(host.Members(OwnerType, OwnerId, "Tiers"));
        var second = Assert.Single(host.Members(OwnerType, "8", "Tiers"));
        Assert.NotEqual(first, second);
        Assert.Equal("Bronze", host.Field(TierType, first, "Title"));
        Assert.Equal("Platinum", host.Field(TierType, second, "Title"));
        Assert.Equal(second, host.MappedId(otherKey, "Tiers", "1"));
        Assert.NotEqual(host.GetListId(OwnerType, OwnerId, "Tiers"), host.GetListId(OwnerType, "8", "Tiers"));
    }

    [Fact]
    public void RoundTrip_AToBAndBack_MatchesOnEveryHost_AndChangesNothingTheSecondTime()
    {
        // Host A numbered its entries 5 and 6; B has its own counter. A's document goes to B, B is
        // serialized, and B's document goes back to A and to B again.
        var hostA = Host();
        hostA.WithItem(TierType, "5", ("Title", "Bronze"));
        hostA.WithItem(TierType, "6", ("Title", "Silver"));
        hostA.WithList(OwnerType, OwnerId, "Tiers", TierType, "5", "6");
        var hostB = Host().WithCounter(TierType, 900);

        var fromA = Extract(hostA);
        Assert.Equal(new[] { "5", "6" }, fromA["Tiers"].Entries.Select(e => e.Id));

        hostB.Writer().Apply(Owner, fromA, null, ItemListLifecycle.Overwrite);
        Assert.Equal(new[] { "901", "902" }, hostB.Members(OwnerType, OwnerId, "Tiers"));

        // B writes the source ids back into its document: the ids every host agrees on.
        var fromB = Extract(hostB);
        Assert.Equal(new[] { "5", "6" }, fromB["Tiers"].Entries.Select(e => e.Id));

        hostA.Journal.Clear();
        var backToA = hostA.Writer().Apply(Owner, fromB, null, ItemListLifecycle.Fill);
        Assert.Equal(0, backToA.Changes);
        Assert.Empty(hostA.Journal);
        Assert.Equal(new[] { "5", "6" }, hostA.Members(OwnerType, OwnerId, "Tiers"));

        hostB.Journal.Clear();
        var againToB = hostB.Writer().Apply(Owner, fromB, null, ItemListLifecycle.Fill);
        Assert.Equal(0, againToB.Changes);
        Assert.Empty(hostB.Journal);
        Assert.Equal(2, hostB.ItemCount(TierType));
    }

    private static Dictionary<string, SerializedItemList> Extract(FakeItemListHost host)
    {
        var fields = new Dictionary<string, object> { ["Tiers"] = host.GetListId(OwnerType, OwnerId, "Tiers") };
        var extraction = new ItemListExtractor(host).Extract(OwnerType, fields, null, "paragraph", ownerKey: OwnerKey);
        return extraction!.Lists;
    }

    [Fact]
    public void MatchedEntryWhoseItemIsGone_IsPutBackUnderItsIdAndWrittenInFull()
    {
        var host = Host();
        var listId = host.WithList(OwnerType, OwnerId, "Tiers", TierType);
        host.Lists[listId].Members.Add("en-tier-1");   // a relation without its item

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Fill);

        Assert.Equal("Bronze", host.Field(TierType, "en-tier-1", "Title"));
        Assert.Equal(new[] { "en-tier-1" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Contains($"restore {TierType}/en-tier-1", host.Journal);
    }

    [Fact]
    public void FieldPointingAtAListOfAnotherItemType_GetsItsOwnList_TheOtherIsLeftAlone()
    {
        var host = Host().WithItemType("Cabp_StatStrip_Item");
        var foreignList = host.WithList(OwnerType, OwnerId, "Tiers", "Cabp_StatStrip_Item", "stat-1");

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.NotEqual(foreignList, host.GetListId(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(new[] { "stat-1" }, host.Lists[foreignList].Members);
        Assert.NotNull(host.Item("Cabp_StatStrip_Item", "stat-1"));
        Assert.Contains(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal) && line.Contains($"item list {foreignList}"));
    }

    // -----------------------------------------------------------------------
    // ItemInstanceType (owner decision C)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ItemListLifecycle.Overwrite)]
    [InlineData(ItemListLifecycle.Fill)]
    public void AnEntryThatArrivedAsATableRow_KeepsItsItemInstanceType_WhenItIsWritten(ItemListLifecycle lifecycle)
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("ItemInstanceType", TierType), ("Title", ""));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, lifecycle);

        Assert.Equal("Bronze", host.Field(TierType, "en-tier-1", "Title"));
        Assert.Equal(TierType, host.Field(TierType, "en-tier-1", "ItemInstanceType"));
        Assert.Contains($"keep instance type {TierType}/en-tier-1", host.Journal);
    }

    [Fact]
    public void AnEntryWithAnEmptyItemInstanceType_IsLeftAsThePlatformWritesIt()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("ItemInstanceType", ""), ("Title", "Old"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(string.Empty, host.Field(TierType, "en-tier-1", "ItemInstanceType"));
        Assert.DoesNotContain(host.Journal, line => line.StartsWith("keep instance type", StringComparison.Ordinal));
    }

    [Fact]
    public void AFillThatWritesNothing_DoesNotTouchItemInstanceType()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("ItemInstanceType", TierType), ("Title", "Editor's"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");

        host.Writer().Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Fill);

        Assert.Empty(host.Journal);
        Assert.Equal(TierType, host.Field(TierType, "en-tier-1", "ItemInstanceType"));
    }

    // -----------------------------------------------------------------------
    // Lists inside entries
    // -----------------------------------------------------------------------

    private static Dictionary<string, SerializedItemList> TiersWithLinks(params SerializedItemListEntry[] links) =>
        Tiers(Entry("en-tier-1", "Bronze", nested: new Dictionary<string, SerializedItemList>
        {
            ["Links"] = new SerializedItemList(links)
        }));

    private static string NestedOwner => ItemListIdentity.EntryOwner(OwnerKey, "Tiers", "en-tier-1");

    private static IReadOnlyList<string> Links(FakeItemListHost host) =>
        host.Members(TierType, Mapped(host, "en-tier-1"), "Links");

    private static string MappedLink(FakeItemListHost host, string sourceId) =>
        host.MappedId(NestedOwner, "Links", sourceId) ?? throw new InvalidOperationException($"no map row for link '{sourceId}'");

    [Fact]
    public void NestedList_IsCreatedUnderItsEntry_AndTheEntrysListFieldIsNotNulled()
    {
        var host = Host();

        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs"), Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { MappedLink(host, "link-1"), MappedLink(host, "link-2") }, Links(host));
        Assert.Equal("Support", host.Field(LinkType, MappedLink(host, "link-2"), "Title"));

        // A second overwrite writes the entry's fields again; the list field must survive it.
        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs"), Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);
        Assert.Equal(new[] { MappedLink(host, "link-1"), MappedLink(host, "link-2") }, Links(host));
        Assert.Equal(2, host.ItemCount(LinkType));
    }

    [Fact]
    public void NestedList_Overwrite_KeepsNestedEntriesAbsentFromTheDocument_AndReportsThem()
    {
        var host = Host();
        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs"), Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);
        var link1 = MappedLink(host, "link-1");
        host.Log.Clear();

        var result = host.Writer().Apply(Owner, TiersWithLinks(Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(new[] { MappedLink(host, "link-2"), link1 }, Links(host));
        Assert.NotNull(host.Item(LinkType, link1));
        Assert.Equal(1, result.HostOnly);
        var note = Assert.Single(host.Log, line => line.StartsWith("Host-only list entry (information)", StringComparison.Ordinal));
        Assert.Contains("entry 'en-tier-1'", note);   // the parent entry
        Assert.Contains("item list 'Links'", note);
        Assert.Contains($"entry '{link1}'", note);
        Assert.Contains($"paragraph {ParagraphGuid}", note);
    }

    [Fact]
    public void NestedList_Fill_KeepsHostOnlyNestedEntries()
    {
        var host = Host();
        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs"), Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);

        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-2", "Support"), Entry("link-3", "Status")), null, ItemListLifecycle.Fill);

        Assert.Equal(new[] { MappedLink(host, "link-1"), MappedLink(host, "link-2"), MappedLink(host, "link-3") }, Links(host));
    }

    [Fact]
    public void AHostOnlyEntry_KeepsTheListItCarries()
    {
        var host = Host();
        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs")), null, ItemListLifecycle.Overwrite);

        host.Writer().Apply(Owner, Tiers(), null, ItemListLifecycle.Overwrite);

        Assert.Equal(1, host.ItemCount(TierType));
        Assert.Equal(1, host.ItemCount(LinkType));
        Assert.Equal(new[] { MappedLink(host, "link-1") }, Links(host));
    }

    [Fact]
    public void NestedEntries_AreKeyedByTheirParentEntry()
    {
        var host = Host();
        var lists = Tiers(
            Entry("en-tier-1", "Bronze", nested: new Dictionary<string, SerializedItemList> { ["Links"] = new(new[] { Entry("1", "Docs") }) }),
            Entry("en-tier-2", "Silver", nested: new Dictionary<string, SerializedItemList> { ["Links"] = new(new[] { Entry("1", "Support") }) }));

        host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);

        var first = Assert.Single(host.Members(TierType, Mapped(host, "en-tier-1"), "Links"));
        var second = Assert.Single(host.Members(TierType, Mapped(host, "en-tier-2"), "Links"));
        Assert.NotEqual(first, second);
        Assert.Equal(first, host.MappedId(ItemListIdentity.EntryOwner(OwnerKey, "Tiers", "en-tier-1"), "Links", "1"));
        Assert.Equal(second, host.MappedId(ItemListIdentity.EntryOwner(OwnerKey, "Tiers", "en-tier-2"), "Links", "1"));
    }

    [Fact]
    public void ListsNestedTooDeep_AreRefused()
    {
        var host = new FakeItemListHost().WithListField("Node", "Children", "Node").WithItem("Node", "root");
        SerializedItemListEntry Chain(int levels) => new()
        {
            Id = $"n{levels}",
            ItemLists = levels == 0 ? null : new Dictionary<string, SerializedItemList> { ["Children"] = new(new[] { Chain(levels - 1) }) }
        };
        var lists = new Dictionary<string, SerializedItemList> { ["Children"] = new(new[] { Chain(ItemListWriter.MaxDepth) }) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            host.Writer().Apply(new ItemListOwner("Node", "root", "page:x", "page x"), lists, null, ItemListLifecycle.Overwrite));

        Assert.Contains("nested more than", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Fails loudly, never drops
    // -----------------------------------------------------------------------

    [Fact]
    public void ListNameThatIsNoItemListFieldOnTheHost_Throws_NamingIt()
    {
        var host = Host();
        var lists = new Dictionary<string, SerializedItemList> { ["Teirs"] = new(new[] { Entry("a", "A") }) };

        var ex = Assert.Throws<InvalidOperationException>(() => host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite));

        Assert.Contains("'Teirs'", ex.Message);
        Assert.Contains("not an item list field", ex.Message);
        Assert.Contains(OwnerType, ex.Message);
        Assert.Empty(host.Journal);
    }

    [Fact]
    public void ListOfPagesOrParagraphs_Throws()
    {
        var host = new FakeItemListHost()
            .WithListField(OwnerType, "Related", "Swift-v2_Page", itemSource: "Page")
            .WithItem(OwnerType, OwnerId);
        var lists = new Dictionary<string, SerializedItemList> { ["Related"] = new(new[] { Entry("44", "A page") }) };

        var ex = Assert.Throws<InvalidOperationException>(() => host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite));

        Assert.Contains("only an inline list travels", ex.Message);
    }

    [Fact]
    public void EntryItemTypeMissingOnTheHost_Throws_NamingTheItemTypeFile()
    {
        var host = new FakeItemListHost().WithItem(OwnerType, OwnerId);
        host.WithListField(OwnerType, "Tiers", TierType).WithoutItemType(TierType);

        var ex = Assert.Throws<InvalidOperationException>(() => host.Writer().Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Overwrite));

        Assert.Contains($"ItemType_{TierType}.xml", ex.Message);
    }

    [Theory]
    [InlineData(ItemListLifecycle.Overwrite)]
    [InlineData(ItemListLifecycle.Fill)]
    public void EmptyListForAFieldWithoutAList_NeedsNoEntryItemType(ItemListLifecycle lifecycle)
    {
        // Stock Swift declares a list field whose entry item type it does not ship; the field is always empty.
        var host = new FakeItemListHost().WithItem(OwnerType, OwnerId);
        host.WithListField(OwnerType, "Tiers", TierType).WithoutItemType(TierType);

        var result = host.Writer().Apply(Owner, Tiers(), null, lifecycle);

        Assert.Equal(0, result.Changes);
        Assert.Empty(host.Journal);
    }

    [Fact]
    public void DuplicateEntryIds_Throw_BeforeAnythingIsWritten()
    {
        var host = Host();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            host.Writer().Apply(Owner, Tiers(Entry("a", "A"), Entry("A", "Again")), null, ItemListLifecycle.Overwrite));

        Assert.Contains("used by more than one entry", ex.Message);
        Assert.Empty(host.Journal);
    }

    [Fact]
    public void NullList_Throws()
    {
        var host = Host();
        var lists = new Dictionary<string, SerializedItemList> { ["Tiers"] = null! };

        var ex = Assert.Throws<InvalidOperationException>(() => host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite));

        Assert.Contains("write [] for an empty list", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Exclusions, options, dry run, link pass bookkeeping
    // -----------------------------------------------------------------------

    [Fact]
    public void ExcludedEntryFields_AreNotNulledByOverwrite_AndNotFilledByFill()
    {
        var host = Host();
        host.WithItem(TierType, "en-tier-1", ("Title", "Old"), ("LocalNote", "keep me"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "en-tier-1");
        IReadOnlySet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LocalNote", "Perk" };

        host.Writer(excludedFieldsFor: type => type == TierType ? excluded : null)
            .Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);
        Assert.Equal("keep me", host.Field(TierType, "en-tier-1", "LocalNote"));

        host.Writer(excludedFieldsFor: type => type == TierType ? excluded : null)
            .Apply(Owner, Tiers(Entry("en-tier-1", "Bronze", perk: "Free shipping")), null, ItemListLifecycle.Fill);
        Assert.Null(host.Field(TierType, "en-tier-1", "Perk"));
    }

    [Fact]
    public void ChangedOnLanguage_IsWrittenWithANewOrOverwrittenList_AndLeftAloneUnderFill()
    {
        var host = Host();
        var options = new Dictionary<string, SerializedItemListOptions> { ["Tiers"] = new() { ChangedOnLanguage = true } };

        host.Writer().Apply(Owner, Tiers(Entry("a", "A")), options, ItemListLifecycle.Fill);
        var listId = host.GetListId(OwnerType, OwnerId, "Tiers");
        Assert.True(host.Lists[listId].ChangedOnLanguage);     // new list: the document's value

        host.Writer().Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Fill);
        Assert.True(host.Lists[listId].ChangedOnLanguage);     // fill: the host's value stays

        host.Writer().Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Overwrite);
        Assert.False(host.Lists[listId].ChangedOnLanguage);    // overwrite: the document's value
    }

    [Fact]
    public void DryRun_WritesNothing_AndCountsCreatesUpdatesHostOnlyAndOrder()
    {
        var host = Host();
        host.WithItem(TierType, "a", ("Title", "Old A"));
        host.WithItem(TierType, "b", ("Title", "B"));
        host.WithItem(TierType, "x", ("Title", "Editor"));
        var listId = host.WithList(OwnerType, OwnerId, "Tiers", TierType, "x", "b", "a");

        var writer = host.Writer(isDryRun: true);
        var result = writer.Apply(Owner, Tiers(Entry("a", "A"), Entry("b", "B"), Entry("c", "C")), null, ItemListLifecycle.Overwrite);

        Assert.Empty(host.Journal);
        Assert.Empty(host.EntryMap);
        Assert.Equal(new[] { "x", "b", "a" }, host.Members(OwnerType, OwnerId, "Tiers"));
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Overwritten);     // "a" changes, "b" does not
        Assert.Equal(1, result.HostOnly);
        Assert.Equal(1, result.Reordered);
        Assert.True(result.Changes > 0);
        Assert.Contains(result.DryRunLines, line => line.StartsWith("update entry 'a'") && line.Contains("Title: 'Old A' -> 'A'"));
        Assert.Contains(result.DryRunLines, line => line.StartsWith("create entry 'c'"));
        Assert.Contains(result.DryRunLines, line => line.StartsWith("order of item list 'Tiers'"));
        // A host-only entry is a note, not a change line.
        Assert.DoesNotContain(result.DryRunLines, line => line.Contains("host-only"));
        Assert.Contains(result.DryRunNotes, line => line.StartsWith("host-only entry 'x'"));
        Assert.DoesNotContain(result.DryRunLines, line => line.Contains("entry 'b'"));
        Assert.Contains(host.Log, line => line.Contains("[DRY-RUN] Item list 'Tiers'") && line.Contains($"list {listId}") &&
                                          line.Contains("1 to create, 1 to update, 1 unchanged, 1 host-only left in place, order changes"));
        // The dry run does not escalate: it never logs a WARNING line.
        Assert.DoesNotContain(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal));
        Assert.Equal(new ItemListCounts(0, 1, 1, 0, 0, 1, 1, 0), writer.Totals);
    }

    [Fact]
    public void DryRun_HostOnlyEntriesAlone_AreNotAChange()
    {
        // Owner decision B: the drift stop counts changes; an entry the delivery leaves alone is none.
        var host = Host();
        host.WithItem(TierType, "a", ("Title", "A"));
        host.WithItem(TierType, "editor-added", ("Title", "Editor"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "editor-added");

        var result = host.Writer(isDryRun: true).Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Changes);
        Assert.Empty(result.DryRunLines);
        Assert.Equal(1, result.HostOnly);
        Assert.Single(result.DryRunNotes);
        Assert.Contains(host.Log, line => line.StartsWith("[DRY-RUN] Host-only list entry (information)", StringComparison.Ordinal));
    }

    [Fact]
    public void DryRun_ComparesEntryFieldsAfterLinkResolution()
    {
        // The document holds the source's page id, the host the target's; the link pass maps 146 to 478.
        var host = Host();
        host.WithItem(TierType, "a", ("Title", "A"), ("Href", "/Default.aspx?ID=478"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a");
        var entry = Entry("a", "A");
        entry.Fields["Href"] = "/Default.aspx?ID=146";
        Dictionary<string, object> Resolve(string type, Dictionary<string, object> fields) =>
            fields.ToDictionary(kv => kv.Key, kv => (object)(kv.Value as string ?? "").Replace("ID=146", "ID=478"));

        var resolved = new ItemListWriter(host, host.Save, host.Fill, null, host.Log.Add, isDryRun: true, resolveLinksForDiff: Resolve)
            .Apply(Owner, Tiers(entry), null, ItemListLifecycle.Overwrite);
        var raw = host.Writer(isDryRun: true).Apply(Owner, Tiers(entry), null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, resolved.Changes);
        Assert.Equal(1, raw.Overwritten);
    }

    [Fact]
    public void DryRun_OfAListAlreadyAsDelivered_CountsNoChange()
    {
        var host = Host();
        var lists = Tiers(Entry("a", "A", perk: "P"), Entry("b", "B"));
        host.Writer().Apply(Owner, lists, null, ItemListLifecycle.Overwrite);

        var result = host.Writer(isDryRun: true).Apply(Owner, lists, null, ItemListLifecycle.Overwrite);

        Assert.Equal(0, result.Changes);
        Assert.Empty(result.DryRunLines);
    }

    [Fact]
    public void DryRun_ReportsAFieldTheOverwriteWouldNull()
    {
        var host = Host();
        host.WithItem(TierType, "a", ("Title", "A"), ("Perk", "Free shipping"));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a");

        var result = host.Writer(isDryRun: true).Apply(Owner, Tiers(Entry("a", "A")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(1, result.Overwritten);
        Assert.Contains(result.DryRunLines, line => line.Contains("Perk: 'Free shipping' -> '' (not in the document)"));
    }

    [Fact]
    public void DryRun_Fill_CountsOnlyEmptyFieldsItWouldFill_AndHostOnlyWithoutAWarningLine()
    {
        var host = Host();
        host.WithItem(TierType, "a", ("Title", "Editor's A"), ("Perk", ""));
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "a", "editor-added");

        var result = host.Writer(isDryRun: true).Apply(Owner, Tiers(Entry("a", "A", perk: "Free shipping")), null, ItemListLifecycle.Fill);

        Assert.Empty(host.Journal);
        Assert.Equal(1, result.FilledFields);
        Assert.Equal(1, result.HostOnly);
        Assert.Equal(0, result.Reordered);
        Assert.Contains(result.DryRunLines, line => line.StartsWith("fill entry 'a'") && line.Contains("Perk: '' -> 'Free shipping'"));
        Assert.DoesNotContain(result.DryRunLines, line => line.Contains("Title"));
    }

    [Fact]
    public void DryRun_NestedLists_AreDiffedToo()
    {
        var host = Host();
        host.Writer().Apply(Owner, TiersWithLinks(Entry("link-1", "Docs")), null, ItemListLifecycle.Overwrite);

        var result = host.Writer(isDryRun: true).Apply(Owner,
            TiersWithLinks(Entry("link-1", "Documentation"), Entry("link-2", "Support")), null, ItemListLifecycle.Overwrite);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Overwritten);
        Assert.Contains(result.DryRunLines, line => line.StartsWith("update entry 'link-1' in item list 'Links'") && line.Contains("entry 'en-tier-1'"));
    }

    [Fact]
    public void DryRun_FieldPointingAtAnotherTypesList_IsADiffLine_NotAWarning()
    {
        var host = Host();
        var foreignList = host.WithList(OwnerType, OwnerId, "Tiers", "Cabp_StatStrip_Item", "stat-1");

        var result = host.Writer(isDryRun: true).Apply(Owner, Tiers(Entry("en-tier-1", "Bronze")), null, ItemListLifecycle.Overwrite);

        Assert.Empty(host.Journal);
        Assert.Equal(1, result.ListsCreated);
        Assert.Contains(result.DryRunLines, line => line.Contains($"points at list {foreignList}"));
        Assert.DoesNotContain(host.Log, line => line.StartsWith("WARNING", StringComparison.Ordinal));
        Assert.Contains(host.Log, line => line.StartsWith("[DRY-RUN]") && line.Contains("real run reports this as a WARNING"));
    }

    [Fact]
    public void DryRun_OfANewList_CountsTheListAndItsEntries()
    {
        var host = Host();

        var result = host.Writer(isDryRun: true).Apply(Owner, TiersWithLinks(Entry("link-1", "Docs")), null, ItemListLifecycle.Overwrite);

        Assert.Empty(host.Journal);
        Assert.Equal(2, result.ListsCreated);
        Assert.Equal(2, result.Created);
    }

    [Fact]
    public void WrittenMembers_AreTheEntriesTheRunWrote_NestedOnesIncluded_NotTheHostOnlyOnes()
    {
        var host = Host();
        host.WithList(OwnerType, OwnerId, "Tiers", TierType, "editor-added");
        var writer = host.Writer();

        writer.Apply(Owner, TiersWithLinks(Entry("link-1", "Docs")), null, ItemListLifecycle.Fill);

        var written = writer.WrittenMembers(OwnerType, OwnerId);
        Assert.Equal(
            new[] { new ItemListMemberRef(TierType, Mapped(host, "en-tier-1")), new ItemListMemberRef(LinkType, MappedLink(host, "link-1")) },
            written);
        Assert.Empty(writer.WrittenMembers(OwnerType, "another"));
        Assert.Empty(writer.WrittenMembers(null, null));
    }

    [Fact]
    public void FieldsWithoutLists_DropsTheListFieldsOfTheDocument_AndProtectKeepsThemFromTheNullOut()
    {
        var fields = new Dictionary<string, object> { ["Heading"] = "Your points", ["tiers"] = "96" };
        var lists = Tiers();

        var withoutLists = ItemListWriter.FieldsWithoutLists(fields, lists);
        var protectedFields = ItemListWriter.Protect(new HashSet<string> { "LocalNote" }, lists);

        Assert.Equal(new[] { "Heading" }, withoutLists.Keys);
        Assert.Same(fields, ItemListWriter.FieldsWithoutLists(fields, null));
        Assert.Contains("TIERS", protectedFields!);
        Assert.Contains("LocalNote", protectedFields!);
        Assert.Null(ItemListWriter.Protect(null, null));
    }
}
