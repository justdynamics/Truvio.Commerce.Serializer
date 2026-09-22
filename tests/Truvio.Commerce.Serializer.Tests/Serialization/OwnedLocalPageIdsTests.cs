using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// PR #26 review blocker: the destination-read set for issue #13 was every page id on the
/// target host, so an unresolvable source id that happened to equal an unrelated host page id
/// was logged as "already local", left pointing at the wrong page, and never escalated under
/// strict mode. The set is now the host pages this composition owns: pages whose GUID is in
/// the YAML set being deserialized.
/// </summary>
public class OwnedLocalPageIdsTests
{
    private static SerializedPage Page(Guid id, int? sourceId, params SerializedPage[] children) => new()
    {
        PageUniqueId = id,
        SourcePageId = sourceId,
        Name = "p",
        MenuText = "p",
        UrlName = "p",
        SortOrder = 1,
        Children = children.ToList()
    };

    private static readonly string ContentDeserializerSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact] // red control: with the host-wide set this collision passed silently as "already local"
    public void CollidingSourceId_WithUnrelatedHostPage_StillWarns()
    {
        var owned = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var yaml = new List<SerializedPage> { Page(owned, sourceId: 47) };
        var host = new Dictionary<Guid, int> { [owned] = 8514, [unrelated] = 9001 };
        var map = InternalLinkResolver.BuildSourceToTargetMap(yaml, host);
        var log = new List<string>();
        var resolver = new InternalLinkResolver(map, log.Add,
            localPageIds: ContentDeserializer.OwnedLocalPageIds(yaml, host));

        var result = resolver.ResolveLinks("Default.aspx?ID=9001");

        Assert.Equal("Default.aspx?ID=9001", result);
        Assert.Equal(1, resolver.GetStats().unresolved);
        Assert.Equal(0, resolver.AlreadyLocalCount);
        Assert.Contains(log, l => l.Contains("WARNING: Unresolvable page ID 9001"));
    }

    [Fact] // the old wiring, pinned as the defect: every host id masks the collision
    public void OldWiring_AllHostPageIds_MasksTheCollision()
    {
        var owned = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var yaml = new List<SerializedPage> { Page(owned, sourceId: 47) };
        var host = new Dictionary<Guid, int> { [owned] = 8514, [unrelated] = 9001 };
        var log = new List<string>();
        var resolver = new InternalLinkResolver(InternalLinkResolver.BuildSourceToTargetMap(yaml, host), log.Add,
            localPageIds: new HashSet<int>(host.Values));

        resolver.ResolveLinks("Default.aspx?ID=9001");

        Assert.Equal(0, resolver.GetStats().unresolved);
        Assert.Equal(1, resolver.AlreadyLocalCount);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void OwnedLocalPageIds_IncludesOwnedPagesWithoutSourceIdAndChildren_ExcludesUnrelated()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var noSourceId = Guid.NewGuid();
        var notOnHost = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var yaml = new List<SerializedPage>
        {
            Page(root, 47, Page(child, 48)),
            Page(noSourceId, null),
            Page(notOnHost, 49),
            Page(Guid.Empty, 50)
        };
        var host = new Dictionary<Guid, int>
        {
            [root] = 8514, [child] = 8515, [noSourceId] = 8516, [unrelated] = 9001
        };

        var owned = ContentDeserializer.OwnedLocalPageIds(yaml, host);

        Assert.Equal(new[] { 8514, 8515, 8516 }, owned.OrderBy(i => i));
    }

    [Fact] // issue #13 stays fixed: an owned target id re-read from the destination is left alone
    public void DestinationRead_OwnedTargetId_StillLeftAloneSilently()
    {
        var owned = Guid.NewGuid();
        var yaml = new List<SerializedPage> { Page(owned, sourceId: 47) };
        var host = new Dictionary<Guid, int> { [owned] = 8514, [Guid.NewGuid()] = 9001 };
        var log = new List<string>();
        var resolver = new InternalLinkResolver(InternalLinkResolver.BuildSourceToTargetMap(yaml, host), log.Add,
            localPageIds: ContentDeserializer.OwnedLocalPageIds(yaml, host));

        Assert.Equal("Default.aspx?ID=8514", resolver.ResolveLinks("Default.aspx?ID=8514"));
        Assert.Equal(1, resolver.AlreadyLocalCount);
        Assert.Equal(0, resolver.GetStats().unresolved);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Theory] // strict mode: the collision escalates with the owned set, and did not with the host-wide set
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void CollidingSourceId_EscalatesUnderStrictMode_OnlyWithTheOwnedSet(bool ownedSet, int expectedWarnings)
    {
        var owned = Guid.NewGuid();
        var yaml = new List<SerializedPage> { Page(owned, sourceId: 47) };
        var host = new Dictionary<Guid, int> { [owned] = 8514, [Guid.NewGuid()] = 9001 };
        var escalator = new StrictModeEscalator(strict: true, log: null);
        // Same routing as SerializerOrchestrator's log wrapper: a WARNING line is recorded.
        void Log(string msg)
        {
            if (msg.TrimStart().StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                escalator.RecordOnly(msg);
        }
        var local = ownedSet
            ? ContentDeserializer.OwnedLocalPageIds(yaml, host)
            : new HashSet<int>(host.Values);
        var resolver = new InternalLinkResolver(InternalLinkResolver.BuildSourceToTargetMap(yaml, host), Log,
            localPageIds: local);

        resolver.ResolveLinks("Default.aspx?ID=9001");

        Assert.Equal(expectedWarnings, escalator.WarningCount);
    }

    [Fact] // source-level guard: neither resolver site may hand over the whole host again
    public void ContentDeserializer_NeverPassesEveryHostPageIdAsLocal()
    {
        Assert.DoesNotContain("new HashSet<int>(allGuidCache.Values)", ContentDeserializerSource);
        Assert.Equal(2, CountOccurrences(ContentDeserializerSource, "OwnedLocalPageIds(allYamlPages, allGuidCache)"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
