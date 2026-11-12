using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// A <c>mode</c> or <c>onExisting</c> value is one enum name. <c>Enum.TryParse</c> also takes a
/// number and a comma-joined list, which it ORs into a member that happens to be defined
/// ("overwrite, keep" = Keep, "Replace,Merge" = Merge); those must read as unknown, in the
/// config and in a document header.
/// </summary>
public class EnumNameTests : IDisposable
{
    private readonly string _tempDir;

    public EnumNameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EnumName_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string predicateJson)
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        File.WriteAllText(path,
            "{ \"outputDirectory\": \"" + _tempDir.Replace("\\", "\\\\") + "\", " +
            "\"predicates\": [ " + predicateJson + " ] }");
        return path;
    }

    [Theory]
    [InlineData("keep", ExistingDocumentLifecycle.Keep)]
    [InlineData(" Fill ", ExistingDocumentLifecycle.Fill)]
    [InlineData("OVERWRITE", ExistingDocumentLifecycle.Overwrite)]
    public void ParseLifecycle_TakesOneName_CaseInsensitive(string text, ExistingDocumentLifecycle expected)
    {
        Assert.Equal(expected, DocumentHeader.ParseLifecycle(text));
        Assert.True(DocumentHeader.IsValidLifecycle(text));
    }

    [Theory]
    [InlineData("overwrite, keep")]   // Enum.TryParse: 0 | 2 = Keep
    [InlineData("overwrite,fill")]    // Enum.TryParse: 0 | 1 = Fill
    [InlineData("keep,keep")]
    [InlineData("fill, keep")]
    [InlineData("2")]
    [InlineData("+1")]
    [InlineData("keeps")]
    public void ParseLifecycle_RejectsListsNumbersAndUnknownNames(string text)
    {
        Assert.Null(DocumentHeader.ParseLifecycle(text));
        Assert.False(DocumentHeader.IsValidLifecycle(text));
        Assert.False(DocumentHeader.IsKeep(new DocumentHeader { Mode = "replace", OnExisting = text }));
    }

    [Theory]
    [InlineData("Replace,Merge")]     // Enum.TryParse: 0 | 1 = Merge
    [InlineData("replace, replace")]
    [InlineData("1")]
    [InlineData("7")]
    public void ParseMode_RejectsListsAndNumbers(string text)
    {
        Assert.Null(DocumentHeader.ParseMode(new DocumentHeader { Mode = text }));
        // The document then runs with the pass strategy, not with a mode nobody wrote.
        Assert.Equal(ConflictStrategy.SourceWins,
            DocumentHeader.StrategyFor(new DocumentHeader { Mode = text }, ConflictStrategy.SourceWins));
    }

    [Theory]
    [InlineData("merge", SerializerMode.Merge)]
    [InlineData(" Replace ", SerializerMode.Replace)]
    public void ParseMode_TakesOneName_CaseInsensitive(string text, SerializerMode expected) =>
        Assert.Equal(expected, DocumentHeader.ParseMode(new DocumentHeader { Mode = text }));

    [Theory]
    [InlineData("Replace,Merge")]
    [InlineData("1")]
    public void Load_RejectsAPredicateModeThatIsNotOneName(string mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"editorial\", \"mode\": \"" + mode + "\", \"providerType\": \"Content\", \"areaId\": 3, \"path\": \"/\" }")));
        Assert.Contains($"Unknown mode '{mode}'", ex.Message);
    }

    [Fact]
    public void Load_RejectsACommaJoinedOnExisting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"editorial\", \"mode\": \"Replace\", \"providerType\": \"Content\", \"areaId\": 3, \"path\": \"/\", " +
            "\"onExisting\": \"overwrite, keep\" }")));
        Assert.Contains("onExisting 'overwrite, keep' is not supported", ex.Message);
    }

    [Fact]
    public void Load_RejectsAnOverrideModeThatIsNotOneName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"editorial\", \"mode\": \"Replace\", \"providerType\": \"Content\", \"areaId\": 3, \"path\": \"/\", " +
            "\"ownershipOverrides\": [ { \"pageUniqueId\": \"11111111-1111-1111-1111-111111111111\", \"mode\": \"Replace,Merge\" } ] }")));
        Assert.Contains("mode 'Replace,Merge' is not supported", ex.Message);
    }

    [Fact]
    public void AnOverrideWithAListMode_StampsThePredicateMode()
    {
        var page = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var policy = OwnershipStampPolicy.FromPredicate(new ProviderPredicateDefinition
        {
            Name = "editorial", Mode = SerializerMode.Replace, ProviderType = "Content", AreaId = 3, Path = "/",
            OwnershipOverrides = new List<OwnershipOverride> { new() { PageUniqueId = page, Mode = "Replace,Merge" } }
        });

        var header = policy.PageHeader(new SerializedPage
        {
            PageUniqueId = page, Name = "Home", MenuText = "Home", UrlName = "home", SortOrder = 1, IsActive = true
        });

        Assert.Equal("replace", header.Mode);
    }
}
