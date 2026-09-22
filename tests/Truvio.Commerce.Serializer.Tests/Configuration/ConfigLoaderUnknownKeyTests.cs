using Truvio.Commerce.Serializer.Configuration;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Engine issue #16: an unrecognised top-level config key used to be dropped by
/// <c>JsonSerializerOptions</c> without a trace, so a config naming a setting the engine no
/// longer has ran on the built-in default and appeared to work by coincidence. The dead
/// <c>deployOutputSubfolder</c> / <c>seedOutputSubfolder</c> names are the motivating case.
/// </summary>
[Trait("Category", "Issue16")]
public class ConfigLoaderUnknownKeyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _warnings = new();

    public ConfigLoaderUnknownKeyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigLoaderUnknownKey_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        ConfigLoader._testWarningSink.Value = _warnings.Add;
    }

    public void Dispose()
    {
        ConfigLoader._testWarningSink.Value = null;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string MinimalConfig(string extraKeys) => $$"""
    {
      "outputDirectory": "{{_tempDir.Replace("\\", "\\\\")}}",
      {{extraKeys}}
      "predicates": []
    }
    """;

    [Theory]
    [InlineData("deployOutputSubfolder", "replaceOutputSubfolder")]
    [InlineData("seedOutputSubfolder", "mergeOutputSubfolder")]
    public void Load_DeadRenamedKey_Rejected_NamingBothKeys(string deadKey, string replacement)
    {
        var path = WriteConfig(MinimalConfig($"\"{deadKey}\": \"somewhere-else\","));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, identifierValidator: null));

        Assert.Contains(deadKey, ex.Message);
        Assert.Contains(replacement, ex.Message);
    }

    [Fact]
    public void Load_UnknownKey_Warns_NamingTheKey_AndStillLoads()
    {
        var path = WriteConfig(MinimalConfig("\"bogusSetting\": 42,"));

        var config = ConfigLoader.Load(path, identifierValidator: null);

        Assert.NotNull(config);
        Assert.Contains(_warnings, w => w.Contains("bogusSetting") && w.Contains("unknown top-level"));
    }

    [Fact]
    public void Load_KnownKeysOnly_ProducesNoUnknownKeyWarning()
    {
        var path = WriteConfig(MinimalConfig(
            "\"replaceOutputSubfolder\": \"replace\", \"mergeOutputSubfolder\": \"merge\", \"showMergeIndicators\": false,"));

        ConfigLoader.Load(path, identifierValidator: null);

        Assert.DoesNotContain(_warnings, w => w.Contains("unknown top-level"));
    }

    [Fact]
    public void Load_UnderscorePrefixedCommentKey_IsNotReported()
    {
        // The shipped ecommerce-predicates-example.json carries a "_comment" array.
        var path = WriteConfig(MinimalConfig("\"_comment\": [\"notes\"],"));

        ConfigLoader.Load(path, identifierValidator: null);

        Assert.DoesNotContain(_warnings, w => w.Contains("_comment"));
    }

    [Fact]
    public void ValidateTopLevelKeys_SectionShapeKeys_AreNotReportedAsUnknown()
    {
        // 'replace' / 'merge' get their own section-shape rejection in Validate(); this gate
        // must not pre-empt it with a different message.
        ConfigLoader.ValidateTopLevelKeys("""{"replace": {}, "merge": {}}""", "in-memory");

        Assert.Empty(_warnings);
    }
}
