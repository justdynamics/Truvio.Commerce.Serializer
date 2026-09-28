using System.Text.RegularExpressions;
using Dynamicweb.Data;
using Moq;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #42.
///
/// <para>
/// (1) Stale SaveArea: a Replace onto an EXISTING area with no area item wrote the area
/// properties by SQL, then created the area item and called <c>Services.Areas.SaveArea</c> on
/// the Area object read BEFORE that UPDATE, reverting every property (AreaCulture,
/// AreaItemTypePageProperty). The binding is now written by a narrow SQL UPDATE of
/// AreaItemType / AreaItemId only.
/// </para>
///
/// <para>
/// (2) Owner rule: an item type must exist before it is used on an area. A missing area item
/// type or page-property item type fails the entry (an entry error, not a warning) before any
/// area write.
/// </para>
///
/// <para>
/// <c>DeserializePredicate</c> reads the live area through Dynamicweb's static services, so
/// the end-to-end ordering is pinned against the source, the same way
/// <see cref="ContentDeserializerAreaItemBindingTests"/> does; the decision and the SQL are
/// exercised directly.
/// </para>
/// </summary>
[Trait("Category", "Issue42")]
public class ContentDeserializerAreaItemTypeGuardTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
        return dir.FullName;
    }

    private static string DeserializePredicateBody()
    {
        var start = Source.IndexOf("private DeserializeResult DeserializePredicate(", StringComparison.Ordinal);
        Assert.True(start > 0, "DeserializePredicate not found.");
        var end = Source.IndexOf("private void WriteAreaProperties(", start, StringComparison.Ordinal);
        Assert.True(end > start, "WriteAreaProperties not found after DeserializePredicate.");
        return Source.Substring(start, end - start);
    }

    private static SerializedArea HeadlessArea(string? itemType = "Headless_Master", string? pagePropertyType = "Headless_PageProperties")
    {
        var props = new Dictionary<string, object> { ["AreaCulture"] = "en-US" };
        if (pagePropertyType != null)
            props["AreaItemTypePageProperty"] = pagePropertyType;
        return new SerializedArea
        {
            AreaId = Guid.NewGuid(),
            Name = "Headless",
            SortOrder = 1,
            ItemType = itemType,
            Properties = props
        };
    }

    private static Func<string, bool> Registered(params string[] types)
    {
        var set = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    // -------------------------------------------------------------------------
    // Owner rule: missing item types fail the entry
    // -------------------------------------------------------------------------

    [Fact]
    public void MissingAreaItemType_IsAnEntryError_NamingTypeAreaAndRemedy()
    {
        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            1, HeadlessArea(), excludedColumns: null, Registered("Headless_PageProperties"));

        var error = Assert.Single(errors);
        Assert.Equal(
            "Area 1 references item type 'Headless_Master' (AreaItemType) which is not registered on this host. " +
            "Deliver the item type (System/Items/ItemType_Headless_Master.xml) and recycle before deserializing content.",
            error);
    }

    [Fact]
    public void MissingPagePropertyItemType_IsAnEntryError()
    {
        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            3, HeadlessArea(), excludedColumns: null, Registered("Headless_Master"));

        var error = Assert.Single(errors);
        Assert.Contains("Area 3 references item type 'Headless_PageProperties' (AreaItemTypePageProperty)", error);
        Assert.Contains("System/Items/ItemType_Headless_PageProperties.xml", error);
    }

    [Fact]
    public void BothTypesMissing_ReportsBoth()
    {
        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            1, HeadlessArea(), excludedColumns: null, Registered());

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void RegisteredTypes_Pass()
    {
        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            1, HeadlessArea(), excludedColumns: null, Registered("Headless_Master", "Headless_PageProperties"));

        Assert.Empty(errors);
    }

    [Fact]
    public void AreaWithoutItemTypes_Passes_WithoutConsultingTheHost()
    {
        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            1, HeadlessArea(itemType: null, pagePropertyType: null), excludedColumns: null,
            _ => throw new InvalidOperationException("must not be asked"));

        Assert.Empty(errors);
    }

    [Fact]
    public void ExcludedPagePropertyColumn_IsNotChecked_BecauseTheTargetKeepsItsOwnValue()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AreaItemTypePageProperty" };

        var errors = ContentDeserializer.FindMissingAreaItemTypes(
            1, HeadlessArea(), excluded, Registered("Headless_Master"));

        Assert.Empty(errors);
    }

    [Fact]
    public void Guard_RunsBeforeEveryAreaWrite_AndReturnsAFailedEntry()
    {
        // Nothing is written when the guard fails: the check and its early return sit before
        // the area INSERT, the area property UPDATE and the area item creation.
        var body = DeserializePredicateBody();

        var guard = body.IndexOf("FindMissingAreaItemTypes(", StringComparison.Ordinal);
        var failReturn = body.IndexOf("return new DeserializeResult { Failed = 1, Errors = itemTypeErrors };", StringComparison.Ordinal);
        var create = body.IndexOf("CreateAreaFromProperties(entry.AreaId", StringComparison.Ordinal);
        var update = body.IndexOf("WriteAreaProperties(entry.AreaId", StringComparison.Ordinal);
        var itemCreate = body.IndexOf("new Dynamicweb.Content.Items.Item(area.ItemType)", StringComparison.Ordinal);

        Assert.True(guard > 0, "Area item-type guard missing from DeserializePredicate.");
        Assert.True(failReturn > guard, "A failing guard must return an entry error.");
        Assert.True(create > failReturn, "Guard must precede the area INSERT.");
        Assert.True(update > failReturn, "Guard must precede the area property UPDATE.");
        Assert.True(itemCreate > failReturn, "Guard must precede the area item creation.");
    }

    // -------------------------------------------------------------------------
    // Stale SaveArea: the item binding no longer rewrites the area row
    // -------------------------------------------------------------------------

    [Fact]
    public void AreaItemBinding_WritesOnlyItemTypeAndItemId()
    {
        var captured = new List<string>();
        var executor = new Mock<ISqlExecutor>();
        executor.Setup(e => e.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
                .Callback<CommandBuilder>(cb => captured.Add(cb.ToString()))
                .Returns(1);

        var deserializer = new ContentDeserializer(
            new ContentEntry { EntryId = "content/area-1", Files = Array.Empty<string>(), AreaId = 1, AreaName = "Standard", Path = "/", PageId = 0 },
            Path.GetTempPath(),
            sqlExecutor: executor.Object);

        deserializer.InvokeWriteAreaItemBindingForTest(1, "Headless_Master", "1");

        var sql = Assert.Single(captured);
        Assert.StartsWith("UPDATE [Area] SET", sql);
        var columns = Regex.Matches(sql, @"\[(\w+)\]").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Every property the preceding UPDATE wrote (AreaCulture, AreaItemTypePageProperty, ...)
        // is absent, so the binding cannot revert them.
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Area", "AreaItemType", "AreaItemId", "AreaID" }, columns);
    }

    [Fact]
    public void PropertiesSurviveItemCreation_NoSaveAreaOfAStaleObject()
    {
        // The regression: SaveArea(targetArea) wrote the pre-UPDATE Area object back over
        // the area properties. No full-row area save may remain in the deserializer.
        Assert.DoesNotContain("Services.Areas.SaveArea(", Source);

        var body = DeserializePredicateBody();
        var update = body.IndexOf("WriteAreaProperties(entry.AreaId", StringComparison.Ordinal);
        var reread = body.IndexOf("targetArea = Services.Areas.GetArea(entry.AreaId) ?? targetArea;", StringComparison.Ordinal);
        var binding = body.IndexOf("WriteAreaItemBinding(entry.AreaId, area.ItemType, targetAreaItemId);", StringComparison.Ordinal);
        Assert.True(reread > update, "The area must be re-read after the property UPDATE.");
        Assert.True(binding > update, "The item binding must be written by SQL after the property UPDATE.");
    }

    [Fact]
    public void UpdatePath_CreatesAMissingPagePropertyItem_AfterThePageSaved_ThenSavesItsId()
    {
        // Pages created while the area had no page-property item type have no
        // PagePropertyItemId; a later run's update path gives them one. The item is created only
        // after the page saved, so a failing save leaves no orphan item row, and the id is then
        // persisted by a second save.
        Assert.Contains("private bool EnsurePagePropertyItem(Page page)", Source);
        const string save = "Services.Pages.SavePage(existingPage, skipLanguages: true);";
        foreach (var ensureCall in new[]
                 {
                     "            if (EnsurePagePropertyItem(existingPage))\r\n                Services.Pages.SavePage",
                     "                if (EnsurePagePropertyItem(existingPage))\r\n                {"
                 })
        {
            var normalized = Source.Replace("\r\n", "\n");
            var ensure = normalized.IndexOf(ensureCall.Replace("\r\n", "\n"), StringComparison.Ordinal);
            Assert.True(ensure > 0, $"Update path must ensure the page property item: {ensureCall}");
            var firstSave = normalized.LastIndexOf(save, ensure, StringComparison.Ordinal);
            Assert.True(firstSave > 0 && ensure - firstSave < 400, "The page save must come right before the item create.");
            var secondSave = normalized.IndexOf(save, ensure, StringComparison.Ordinal);
            Assert.True(secondSave > ensure && secondSave - ensure < 200, "A created item id must be persisted by a second save.");
        }
    }
}
