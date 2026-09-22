using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Resolution order for the match key of a SqlTable write: the declared PRIMARY KEY, the
/// entry's keyColumns, a UNIQUE index, then the full non-identity column tuple.
/// </summary>
[Trait("Category", "HeapKeyResolution")]
public class KeyResolutionTests
{
    private static TableMetadata Metadata(
        IEnumerable<string>? keyColumns = null,
        IEnumerable<string>? identityColumns = null) => new()
    {
        TableName = "DynamicStructures",
        KeyColumns = [.. keyColumns ?? []],
        IdentityColumns = [.. identityColumns ?? ["DynamicStructureId"]],
        AllColumns = ["DynamicStructureId", "DynamicStructureUniqueId", "DynamicStructureTitle"]
    };

    private static List<UniqueIndexDefinition> Index(string name, params string[] columns) =>
        [new UniqueIndexDefinition { Name = name, Columns = columns }];

    [Fact]
    public void PrimaryKey_WinsOverEverythingElse()
    {
        var resolution = KeyResolution.Resolve(
            Metadata(keyColumns: ["DynamicStructureId"]),
            declaredKeyColumns: ["DynamicStructureTitle"],
            uniqueIndexes: () => Index("UX", "DynamicStructureUniqueId"));

        Assert.Equal(KeyResolutionSource.PrimaryKey, resolution.Source);
        Assert.Equal(["DynamicStructureId"], resolution.KeyColumns);
        Assert.False(resolution.IsInferred);
        Assert.Equal("primary key", resolution.Describe());
    }

    [Fact]
    public void DeclaredKeyColumns_WinOverAUniqueIndex()
    {
        var resolution = KeyResolution.Resolve(
            Metadata(),
            declaredKeyColumns: ["DynamicStructureTitle"],
            uniqueIndexes: () => Index("UX", "DynamicStructureUniqueId"));

        Assert.Equal(KeyResolutionSource.DeclaredKeyColumns, resolution.Source);
        Assert.Equal(["DynamicStructureTitle"], resolution.KeyColumns);
        Assert.True(resolution.IsInferred);
        Assert.Equal("keyColumns", resolution.Describe());
    }

    [Fact]
    public void DeclaredKeyColumns_TakeTheLiveSpellingOfTheColumn()
    {
        var resolution = KeyResolution.Resolve(
            Metadata(),
            declaredKeyColumns: ["dynamicstructureuniqueid"]);

        Assert.Equal(["DynamicStructureUniqueId"], resolution.KeyColumns);
    }

    [Fact]
    public void DeclaredKeyColumn_MissingFromTheTable_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyResolution.Resolve(Metadata(), declaredKeyColumns: ["NoSuchColumn"]));

        Assert.Contains("NoSuchColumn", ex.Message);
        Assert.Contains("DynamicStructures", ex.Message);
    }

    [Fact]
    public void UniqueIndex_WinsOverTheAllColumnsFallback()
    {
        var resolution = KeyResolution.Resolve(
            Metadata(),
            declaredKeyColumns: null,
            uniqueIndexes: () => Index("UX_DynamicStructures_UniqueId", "DynamicStructureUniqueId"));

        Assert.Equal(KeyResolutionSource.UniqueIndex, resolution.Source);
        Assert.Equal(["DynamicStructureUniqueId"], resolution.KeyColumns);
        Assert.Equal("unique index (UX_DynamicStructures_UniqueId)", resolution.Describe());
    }

    [Fact]
    public void UniqueIndex_NarrowestCandidateWins()
    {
        var candidates = new List<UniqueIndexDefinition>
        {
            new() { Name = "UX_Composite", Columns = ["DynamicStructureUniqueId", "DynamicStructureTitle"] },
            new() { Name = "UX_Single", Columns = ["DynamicStructureUniqueId"] }
        };

        var resolution = KeyResolution.Resolve(Metadata(), null, () => candidates);

        Assert.Equal("UX_Single", resolution.IndexName);
    }

    [Fact]
    public void UniqueIndex_OverAnIdentityColumnOnly_IsNotUsed()
    {
        // An auto-id index is environment-local, so it is no safer than the truncate it replaces.
        var resolution = KeyResolution.Resolve(
            Metadata(),
            null,
            () => Index("UX_Identity", "DynamicStructureId"));

        Assert.Equal(KeyResolutionSource.AllColumns, resolution.Source);
    }

    [Fact]
    public void UniqueIndex_OverAColumnTheTableDoesNotHave_IsNotUsed()
    {
        var resolution = KeyResolution.Resolve(Metadata(), null, () => Index("UX_Stale", "GoneColumn"));

        Assert.Equal(KeyResolutionSource.AllColumns, resolution.Source);
    }

    [Fact]
    public void AllColumnsFallback_ExcludesIdentityColumns()
    {
        var resolution = KeyResolution.Resolve(Metadata(), null);

        Assert.Equal(KeyResolutionSource.AllColumns, resolution.Source);
        Assert.Equal(["DynamicStructureUniqueId", "DynamicStructureTitle"], resolution.KeyColumns);
        Assert.Equal("all columns", resolution.Describe());
    }

    [Fact]
    public void AllColumnsFallback_NarrowsToTheColumnsThePayloadCarries()
    {
        var resolution = KeyResolution.Resolve(
            Metadata(),
            null,
            null,
            payloadColumns: ["DynamicStructureUniqueId"]);

        Assert.Equal(["DynamicStructureUniqueId"], resolution.KeyColumns);
    }

    [Fact]
    public void NoColumnLeftToMatchOn_Throws()
    {
        var identityOnly = new TableMetadata
        {
            TableName = "OnlyAnId",
            KeyColumns = [],
            IdentityColumns = ["Id"],
            AllColumns = ["Id"]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => KeyResolution.Resolve(identityOnly, null));

        Assert.Contains("refusing to write", ex.Message);
    }

    [Fact]
    public void UniqueIndexes_AreOnlyQueriedWhenNoEarlierStepResolves()
    {
        var queried = 0;
        IReadOnlyList<UniqueIndexDefinition> Count()
        {
            queried++;
            return [];
        }

        KeyResolution.Resolve(Metadata(keyColumns: ["DynamicStructureId"]), null, Count);
        KeyResolution.Resolve(Metadata(), ["DynamicStructureUniqueId"], Count);
        Assert.Equal(0, queried);

        KeyResolution.Resolve(Metadata(), null, Count);
        Assert.Equal(1, queried);
    }
}
