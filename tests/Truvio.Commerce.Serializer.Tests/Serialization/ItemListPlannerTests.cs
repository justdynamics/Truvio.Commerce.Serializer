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

    private static string Scoped(string id) => ItemListIdentity.ScopedId(Owner, Field, id);

    private static ItemListPlan Plan(ItemListLifecycle lifecycle, string[] yaml, string[] host, params string[] takenElsewhere) =>
        ItemListPlanner.Plan(lifecycle, Owner, Field, yaml, host,
            id => takenElsewhere.Contains(id, StringComparer.OrdinalIgnoreCase));

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
    public void ScopedId_IsStable_Prefixed_AndNeverANumber()
    {
        var id = Scoped("12");

        Assert.Equal(id, Scoped("12"));
        Assert.StartsWith(ItemListIdentity.ScopedPrefix, id);
        Assert.Equal(27, id.Length);
        Assert.False(ItemListIdentity.IsHostAllocated(id));
    }

    [Fact]
    public void ScopedId_DiffersPerOwner_PerField_AndPerSourceId()
    {
        var otherOwner = ItemListIdentity.ParagraphOwner(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        Assert.NotEqual(Scoped("12"), ItemListIdentity.ScopedId(otherOwner, Field, "12"));
        Assert.NotEqual(Scoped("12"), ItemListIdentity.ScopedId(Owner, "Slides", "12"));
        Assert.NotEqual(Scoped("12"), Scoped("13"));
        Assert.NotEqual(
            ItemListIdentity.ScopedId(ItemListIdentity.PageOwner(Guid.Empty), Field, "1"),
            ItemListIdentity.ScopedId(ItemListIdentity.ParagraphOwner(Guid.Empty), Field, "1"));
    }

    [Fact]
    public void ScopedId_IgnoresCaseOfFieldAndId()
    {
        Assert.Equal(ItemListIdentity.ScopedId(Owner, "tiers", "EN-TIER-1"), ItemListIdentity.ScopedId(Owner, "Tiers", "en-tier-1"));
    }

    [Fact]
    public void Matches_TheSameId_OrTheScopedFormInEitherDirection()
    {
        Assert.True(ItemListIdentity.Matches("en-tier-1", "EN-TIER-1", Owner, Field));
        Assert.True(ItemListIdentity.Matches("12", Scoped("12"), Owner, Field));
        Assert.True(ItemListIdentity.Matches(Scoped("12"), "12", Owner, Field));
        Assert.False(ItemListIdentity.Matches("12", "13", Owner, Field));
        Assert.False(ItemListIdentity.Matches("12", ItemListIdentity.ScopedId(Owner, "Slides", "12"), Owner, Field));
    }

    [Fact]
    public void CanonicalId_IsTheIdTheOtherWasDerivedFrom()
    {
        Assert.Equal("12", ItemListIdentity.CanonicalId("12", Scoped("12"), Owner, Field));
        Assert.Equal("12", ItemListIdentity.CanonicalId(Scoped("12"), "12", Owner, Field));
        Assert.Equal("en-tier-1", ItemListIdentity.CanonicalId("en-tier-1", "en-tier-1", Owner, Field));
    }

    [Fact]
    public void EntryOwner_IsTheSameOnBothHosts_SoNestedEntriesKeepMatching()
    {
        // Host A numbered the entry 12; host B holds it under the scoped id. Both key the
        // entry's own lists by the canonical id.
        var onB = ItemListIdentity.EntryOwner(Owner, Field, ItemListIdentity.CanonicalId("12", Scoped("12"), Owner, Field));
        var onA = ItemListIdentity.EntryOwner(Owner, Field, ItemListIdentity.CanonicalId(Scoped("12"), "12", Owner, Field));

        Assert.Equal(onA, onB);
    }

    // -----------------------------------------------------------------------
    // Overwrite
    // -----------------------------------------------------------------------

    [Fact]
    public void Overwrite_OnAnEmptyHost_CreatesEveryEntryInDocumentOrder()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "b", "a" }, Array.Empty<string>());

        Assert.All(plan.Entries, e => Assert.Equal(ItemListEntryAction.Create, e.Action));
        Assert.Equal(new[] { "b", "a" }, plan.FinalOrder);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Overwrite_MatchesUpdates_CreatesMissing_RemovesTheRest_InDocumentOrder()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "c", "a", "new" }, new[] { "a", "b", "c" });

        Assert.Equal(
            new[] { ItemListEntryAction.Overwrite, ItemListEntryAction.Overwrite, ItemListEntryAction.Create },
            plan.Entries.Select(e => e.Action));
        Assert.Equal(new[] { "b" }, plan.Remove);
        Assert.Equal(new[] { "c", "a", "new" }, plan.FinalOrder);
    }

    [Fact]
    public void Overwrite_EmptyDocument_RemovesEveryHostEntry()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, Array.Empty<string>(), new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, plan.Remove);
        Assert.Empty(plan.FinalOrder);
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
        Assert.Empty(plan.Remove);
        Assert.Equal(new[] { "a", "b", "c", "new-1", "new-2" }, plan.FinalOrder);
    }

    [Fact]
    public void Fill_WhenEveryEntryMatches_KeepsTheMembership()
    {
        var host = new[] { "b", "a" };

        var plan = Plan(ItemListLifecycle.Fill, new[] { "a", "b" }, host);

        Assert.True(plan.KeepsMembership(host));
    }

    [Fact]
    public void Overwrite_WithTheSameEntriesInTheSameOrder_KeepsTheMembership_AnotherOrderDoesNot()
    {
        var host = new[] { "a", "b" };

        Assert.True(Plan(ItemListLifecycle.Overwrite, new[] { "a", "b" }, host).KeepsMembership(host));
        Assert.False(Plan(ItemListLifecycle.Overwrite, new[] { "b", "a" }, host).KeepsMembership(host));
        Assert.False(Plan(ItemListLifecycle.Overwrite, new[] { "a" }, host).KeepsMembership(host));
    }

    // -----------------------------------------------------------------------
    // Target ids
    // -----------------------------------------------------------------------

    [Fact]
    public void CreatedEntry_KeepsATextId_AndGetsTheScopedIdForANumber()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "en-tier-1", "12" }, Array.Empty<string>());

        Assert.Equal(new[] { "en-tier-1", Scoped("12") }, plan.Entries.Select(e => e.TargetId));
        Assert.Empty(plan.ForeignIds);
    }

    [Fact]
    public void NumberUsedElsewhereOnTheHost_IsNotAForeignId_ItIsNeverWrittenAsANumber()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "12" }, Array.Empty<string>(), takenElsewhere: "12");

        Assert.Equal(Scoped("12"), Assert.Single(plan.Entries).TargetId);
        Assert.Empty(plan.ForeignIds);
    }

    [Fact]
    public void TextIdUsedElsewhereOnTheHost_IsForeign_AndTheEntryGetsItsScopedId()
    {
        var plan = Plan(ItemListLifecycle.Fill, new[] { "en-tier-1" }, Array.Empty<string>(), takenElsewhere: "en-tier-1");

        Assert.Equal(Scoped("en-tier-1"), Assert.Single(plan.Entries).TargetId);
        Assert.Equal(new[] { "en-tier-1" }, plan.ForeignIds);
    }

    [Fact]
    public void EntryIsLookedForOnlyInTheOwnersList_AnItemElsewhereIsNeverAMatch()
    {
        var plan = Plan(ItemListLifecycle.Overwrite, new[] { "en-tier-1" }, new[] { "en-tier-9" }, takenElsewhere: "en-tier-1");

        Assert.Equal(ItemListEntryAction.Create, Assert.Single(plan.Entries).Action);
        Assert.Equal(new[] { "en-tier-9" }, plan.Remove);
    }

    [Fact]
    public void HostEntryUnderTheScopedId_IsMatched_AndKeepsItsHostId()
    {
        var plan = Plan(ItemListLifecycle.Fill, new[] { "12" }, new[] { Scoped("12") });

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(ItemListEntryAction.Fill, entry.Action);
        Assert.Equal(Scoped("12"), entry.TargetId);
        Assert.Equal("12", entry.CanonicalId);
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
    public void TwoEntriesMatchingOneHostEntry_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Plan(ItemListLifecycle.Overwrite, new[] { "12", Scoped("12") }, new[] { "12" }));

        Assert.Contains("both match the host entry '12'", ex.Message);
    }
}
