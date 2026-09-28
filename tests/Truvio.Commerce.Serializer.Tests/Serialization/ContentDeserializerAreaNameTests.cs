using Dynamicweb.Data;
using Moq;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #43: Replace is source-wins for the area row. area.yml carries the website name as
/// its top-level <c>name</c>, not in <c>properties</c>, so the property UPDATE never wrote it
/// and an EXISTING target area kept its own name (a blank database's wizard area stayed
/// <c>Standard</c>). The whole-area entry now writes <c>[AreaName]</c> with the properties;
/// <c>excludeAreaColumns: [AreaName]</c> keeps the target's name.
/// </summary>
[Trait("Category", "Issue43")]
public class ContentDeserializerAreaNameTests
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

    private static TargetSchemaCache AreaSchema() => new(_ =>
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AreaID", "AreaName", "AreaSort", "AreaUniqueId", "AreaCulture"
        };
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AreaID"] = "int",
            ["AreaName"] = "nvarchar",
            ["AreaSort"] = "int",
            ["AreaUniqueId"] = "uniqueidentifier",
            ["AreaCulture"] = "nvarchar"
        };
        return (cols, types);
    });

    private static (ContentDeserializer Deserializer, List<CommandBuilder> Captured) Build()
    {
        var captured = new List<CommandBuilder>();
        var executor = new Mock<ISqlExecutor>();
        executor.Setup(e => e.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
                .Callback<CommandBuilder>(captured.Add)
                .Returns(1);
        var deserializer = new ContentDeserializer(
            new ContentEntry { EntryId = "content/area-1", Files = Array.Empty<string>(), AreaId = 1, AreaName = "Headless", Path = "/", PageId = 0 },
            Path.GetTempPath(),
            schemaCache: AreaSchema(),
            sqlExecutor: executor.Object);
        return (deserializer, captured);
    }

    private static Dictionary<string, object> Properties() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AreaCulture"] = "en-US"
    };

    // Same reflection read of CommandBuilder's bound values as SqlNullRoundTripTests.
    private static List<object?> ParameterValues(CommandBuilder cb)
    {
        var infos = (System.Collections.IEnumerable)typeof(CommandBuilder)
            .GetProperty("ParameterInfos", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cb)!;
        return infos.Cast<object>()
            .Select(p => p.GetType().GetProperty("Value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(p))
            .ToList();
    }

    [Fact]
    public void ExistingArea_GetsTheYamlName()
    {
        var (deserializer, captured) = Build();

        deserializer.InvokeUpdateAreaFromPropertiesForTest(1, Properties(), excludeFields: null, excludeAreaColumns: null, areaName: "Headless");

        var cb = Assert.Single(captured);
        var sql = cb.ToString();
        Assert.StartsWith("UPDATE [Area] SET [AreaName] = ", sql);
        Assert.Contains("[AreaCulture] = ", sql);
        Assert.Contains("Headless", ParameterValues(cb));
    }

    [Fact]
    public void AreaNameInExcludeAreaColumns_KeepsTheTargetName()
    {
        var (deserializer, captured) = Build();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AreaName" };

        deserializer.InvokeUpdateAreaFromPropertiesForTest(1, Properties(), excludeFields: null, excludeAreaColumns: excluded, areaName: "Headless");

        var cb = Assert.Single(captured);
        Assert.DoesNotContain("[AreaName]", cb.ToString());
        Assert.Contains("[AreaCulture] = ", cb.ToString());
    }

    [Fact]
    public void NameOnly_StillWritesTheName()
    {
        var (deserializer, captured) = Build();

        deserializer.InvokeUpdateAreaFromPropertiesForTest(1, new Dictionary<string, object>(), excludeFields: null, excludeAreaColumns: null, areaName: "Headless");

        var cb = Assert.Single(captured);
        Assert.StartsWith("UPDATE [Area] SET [AreaName] = ", cb.ToString());
    }

    [Fact]
    public void NameOnly_Excluded_WritesNothing()
    {
        var (deserializer, captured) = Build();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AreaName" };

        deserializer.InvokeUpdateAreaFromPropertiesForTest(1, new Dictionary<string, object>(), excludeFields: null, excludeAreaColumns: excluded, areaName: "Headless");

        Assert.Empty(captured);
    }

    [Fact]
    public void CreatedArea_StillInsertsTheYamlName()
    {
        // Unchanged behaviour: the INSERT carries area.Name. The whole-area UPDATE that follows
        // writes the same name, so a created area ends with the YAML name either way.
        var (deserializer, captured) = Build();
        var area = new SerializedArea { AreaId = Guid.NewGuid(), Name = "Headless Nederlands", SortOrder = 2, Properties = Properties() };

        deserializer.InvokeCreateAreaFromPropertiesForTest(2, area, excludeFields: null);

        var cb = Assert.Single(captured);
        Assert.Contains("[AreaName]", cb.ToString());
        Assert.Contains("Headless Nederlands", ParameterValues(cb));
    }

    [Fact]
    public void WholeAreaEntry_PassesTheYamlNameToTheAreaUpdate()
    {
        Assert.Contains(
            "WriteAreaProperties(entry.AreaId, area.Properties, areaPropsExclude, excludeAreaColumnsSet, area.Name);",
            Source);
    }
}
