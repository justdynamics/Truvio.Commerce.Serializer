using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R7: identity of a list entry across hosts, and the plan a delivery makes
/// for one list under each lifecycle.
/// </summary>
public class ItemListPlannerTests
{
    private static readonly string Owner = ItemListIdentity.ParagraphOwner(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private const string Field = "Tiers";

    private static string Key(string id) => ItemListIdentity.EntryKey(Owner, Field, id);

    private static ItemListPlan Plan(ItemListLifecycle lifecycle, string[] yaml, string[] host, Dictionary<string, string>? map = null) =>
        ItemListPlanner.Plan(lifecycle, Field, yaml, host,
            id => map is not null && map.TryGetValue(id, out var hostId) ? hostId : null);

    // -----------------------------------------------------------------------
    // Identity
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("12", true)]
    [InlineData("0", true)]
    [InlineData("en-tier-1", false)]
    [InlineData("12a", false)]
    [InlineData("sl-0123456789abcdef01234567", false)]
    [InlineData("", false)]
    public void OnlyDigits_AreAHostAllocatedId(string id, bool expected) =>
        Assert.Equal(expected, ItemListIdentity.IsHostAllocated(id));

    [Fact]
    public void EntryKey_IsStable_Prefixed_AndNeverANumber()
    {
        var key = Key("12");

        Assert.Equal(key, Key("12"));
        Assert.StartsWith(ItemListIdentity.KeyPrefix, key);
        Assert.Equal(27, key.Length);
        Assert.False(ItemListIdentity.IsHostAllocated(key));
    }

    [Fact]
    public void EntryKey_DiffersPerOwner_PerField_AndPerSourceId()
    {
        var otherOwner = ItemListIdentity.ParagraphOwner(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        Assert.NotEqual(Key("12"), ItemListIdentity.EntryKey(otherOwner, Field, "12"));
        Assert.NotEqual(Key("12"), ItemListIdentity.EntryKey(Owner, "Slides", "12"));
        Assert.NotEqual(Key("12"), Key("13"));
        Assert.NotEqual(
            ItemListIdentity.EntryKey(ItemListIdentity.PageOwner(Guid.Empty), Field, "1"),
            ItemListIdentity.EntryKey(ItemListIdentity.ParagraphOwner(Guid.Empty), Field, "1"));
    }

    [Fact]
    public void EntryKey_IgnoresCaseOfFieldAndId()
    {
        Assert.Equal(ItemListIdentity.EntryKey(Owner, "tiers", "EN-TIER-1"), ItemListIdentity.EntryKey(Owner, "Tiers", "en-tier-1"));
    }

    [Fact]
    public void EntryKey_FitsTheEntryMapsKeyColumn()
    {
        Assert.Contains("[EntryKey] nvarchar(64)", ItemListEntryMap.CreateSql);
        Assert.True(Key("a-very-long-source-id-" + new string('x', 200)).Length <= 64);
    }

    [Fact]
    public void EntryOwner_IsKeyedByTheDocumentId_SoNestedEntriesMatchOnEveryHost()
    {
        Assert.Equal($"{Owner}/tiers/en-tier-1", ItemListIdentity.EntryOwner(Owner, "Tiers", "EN-TIER-1"));
    }

    // -----------------------------------------------------------------------
    // Matching
    // -----------------------------------------------------------------------

    [Fact]
    public void Match_ThroughTheEntryMap_TakesTheMappedHostEntry()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "12", "en-tier-1" }, new[] { "901", "902" },
            new Dictionary<string, string> { ["12"] = "902", ["en-tier-1"] = "901" });

        Assert.Equal(new[] { "902", "901" }, plan.Entries.Select(e => e.HostId));
        Assert.All(plan.Entries, e => Assert.Equal(ItemListMatch.Key, e.MatchedBy));
        Assert.Empty(plan.HostOnly);
    }

    [Fact]
    public void Match_ByTheDocumentsOwnId_ForEntriesDeliveredAsRowsBefore_NumberOrText()
    {
        var plan = Plan(ItemListLifecycle.Fill, new[] { "12", "en-tier-1" }, new[] { "EN-TIER-1", "12" });

        Assert.Equal(new[] { "12", "EN-TIER-1" }, plan.Entries.Select(e => e.HostId));
        Assert.All(plan.Entries, e => Assert.Equal(ItemListMatch.Id, e.MatchedBy));
        Assert.Equal(0, plan.Creates);
    }

    [Fact]
    public void TheMapsClaim_WinsOverAnEqualIdOfAnotherMember()
    {
        // The map says document "12" is host 41; host 12 is someone else's entry.
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "12" }, new[] { "41", "12" },
            new Dictionary<string, string> { ["12"] = "41" });

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("41", entry.HostId);
        Assert.Equal(ItemListMatch.Key, entry.MatchedBy);
        Assert.Equal(new[] { "12" }, plan.HostOnly);
    }

    [Fact]
    public void AMapRowWhoseEntryIsNotInTheList_MatchesNothing()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "12" }, new[] { "33" },
            new Dictionary<string, string> { ["12"] = "9" });

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(ItemListEntryAction.Create, entry.Action);
        Assert.Null(entry.HostId);
        Assert.Equal(ItemListMatch.None, entry.MatchedBy);
    }

    [Fact]
    public void EntryIsLookedForOnlyInTheOwnersList()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "en-tier-1" }, new[] { "en-tier-9" });

        Assert.Equal(ItemListEntryAction.Create, Assert.Single(plan.Entries).Action);
        Assert.Equal(new[] { "en-tier-9" }, plan.HostOnly);
    }

    [Fact]
    public void AnEntryToCreate_HasNoHostIdYet_TheHostNumbersIt()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "en-tier-1", "12" }, Array.Empty<string>());

        Assert.All(plan.Entries, e =>
        {
            Assert.Equal(ItemListEntryAction.Create, e.Action);
            Assert.Null(e.HostId);
        });
        Assert.Equal(new[] { "41", "42" }, plan.FinalOrder(new Dictionary<int, string> { [0] = "41", [1] = "42" }));
        Assert.Throws<InvalidOperationException>(() => plan.FinalOrder());
    }

    // -----------------------------------------------------------------------
    // Overwrite
    // -----------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<int, string> Created3 = new Dictionary<int, string> { [0] = "n0", [1] = "n1", [2] = "n2", [3] = "n3" };

    [Fact]
    public void Overwrite_MatchesUpdates_CreatesMissing_KeepsHostOnlyAfterTheDelivered()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "c", "a", "new" }, new[] { "a", "b", "c" });

        Assert.Equal(
            new[] { ItemListEntryAction.Overwrite, ItemListEntryAction.Overwrite, ItemListEntryAction.Create },
            plan.Entries.Select(e => e.Action));
        // A Replace never deletes a list entry: "b" stays, after the delivered entries.
        Assert.Equal(new[] { "b" }, plan.HostOnly);
        Assert.Equal(new[] { "c", "a", "n2", "b" }, plan.FinalOrder(Created3));
    }

    [Fact]
    public void Overwrite_HostOnlyEntries_KeepTheirRelativeHostOrder()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "c" }, new[] { "x", "c", "a", "y" });

        Assert.Equal(new[] { "x", "a", "y" }, plan.HostOnly);
        Assert.Equal(new[] { "c", "x", "a", "y" }, plan.FinalOrder());
    }

    [Fact]
    public void Overwrite_EmptyDocument_KeepsEveryHostEntry()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, Array.Empty<string>(), new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, plan.HostOnly);
        Assert.Equal(new[] { "a", "b" }, plan.FinalOrder());
        Assert.True(plan.KeepsMembership());
    }

    [Fact]
    public void Overwrite_SecondDeliveryWithHostOnlyEntries_KeepsTheMembership()
    {
        // After the first delivery the host order is delivered + host-only; the same document
        // again changes nothing.
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "c", "a", "new" }, new[] { "c", "a", "77", "b" },
            new Dictionary<string, string> { ["new"] = "77" });

        Assert.True(plan.KeepsMembership());
        Assert.False(plan.ReordersHostMembers());
    }

    [Fact]
    public void ReordersHostMembers_IsTrueOnlyWhenExistingMembersMove()
    {
        var host = new[] { "a", "b" };

        Assert.True(Plan(ItemListLifecycle.Overwrite, new[] { "b", "a" }, host).ReordersHostMembers());
        Assert.False(Plan(ItemListLifecycle.Overwrite, new[] { "a", "b", "new" }, host).ReordersHostMembers());
        // A host-only entry moving behind the delivered ones is an order change.
        Assert.True(Plan(ItemListLifecycle.Overwrite, new[] { "b" }, host).ReordersHostMembers());
        Assert.False(Plan(ItemListLifecycle.Fill, new[] { "b", "a", "new" }, host).ReordersHostMembers());
    }

    // -----------------------------------------------------------------------
    // Fill
    // -----------------------------------------------------------------------

    [Fact]
    public void Fill_FillsMatches_AppendsMissing_KeepsHostOnlyEntriesAndHostOrder()
    {
        var plan = Plan(ItemListLifecycle.Fill, new[] { "new-1", "c", "a", "new-2" }, new[] { "a", "b", "c" });

        Assert.Equal(
            new[] { ItemListEntryAction.Create, ItemListEntryAction.Fill, ItemListEntryAction.Fill, ItemListEntryAction.Create },
            plan.Entries.Select(e => e.Action));
        Assert.Equal(new[] { "b" }, plan.HostOnly);
        Assert.Equal(new[] { "a", "b", "c", "n0", "n3" }, plan.FinalOrder(Created3));
    }

    [Fact]
    public void Fill_WhenEveryEntryMatches_KeepsTheMembership()
    {
        Assert.True(Plan(ItemListLifecycle.Fill, new[] { "a", "b" }, new[] { "b", "a" }).KeepsMembership());
    }

    [Fact]
    public void Overwrite_WithTheSameEntriesInTheSameOrder_KeepsTheMembership_AnotherOrderDoesNot()
    {
        var host = new[] { "a", "b" };

        Assert.True(Plan(ItemListLifecycle.Overwrite, new[] { "a", "b" }, host).KeepsMembership());
        Assert.False(Plan(ItemListLifecycle.Overwrite, new[] { "b", "a" }, host).KeepsMembership());
        // "b" stays as a host-only entry: same membership, same order.
        Assert.True(Plan(ItemListLifecycle.Overwrite, new[] { "a" }, host).KeepsMembership());
        Assert.False(Plan(ItemListLifecycle.Overwrite, new[] { "a", "new" }, host).KeepsMembership());
    }

    // -----------------------------------------------------------------------
    // Errors: nothing is planned
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EntryWithoutAnId_Throws(string id)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Plan(ItemListLifecycle.Overwrite, new[] { "a", id }, Array.Empty<string>()));

        Assert.Contains("entry 2 has no id", ex.Message);
    }

    [Fact]
    public void TheSameIdTwice_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Plan(ItemListLifecycle.Fill, new[] { "a", "b", "A" }, Array.Empty<string>()));

        Assert.Contains("'A' is used by more than one entry", ex.Message);
    }

    [Fact]
    public void TwoEntriesMappedToOneHostEntry_MatchOnce_TheOtherIsCreated()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "a", "b" }, new[] { "41" },
            new Dictionary<string, string> { ["a"] = "41", ["b"] = "41" });

        Assert.Equal("41", plan.Entries[0].HostId);
        Assert.Equal(ItemListEntryAction.Create, plan.Entries[1].Action);
    }
}
