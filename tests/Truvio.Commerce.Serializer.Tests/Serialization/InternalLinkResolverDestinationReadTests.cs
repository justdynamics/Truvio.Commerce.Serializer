using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #13: a Merge run over its own earlier output re-reads link values FROM THE
/// DESTINATION, where the previous run already rewrote them to local page ids. Those must
/// not be re-resolved, must not warn, and every genuine Unresolvable warning must name the
/// entry, document and field.
///
/// Issue #15: the raw-numeric short-circuit only applies to reference-typed fields, and page
/// id 0 is never mapped.
/// </summary>
public class InternalLinkResolverDestinationReadTests
{
    // 47 -> 8514 is the shape from the Foundry report: a source id and the host id it
    // became on the first pass.
    private static Dictionary<int, int> Map() => new() { { 47, 8514 }, { 12, 8559 } };

    // -----------------------------------------------------------------------
    // #13 — destination-read values
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveLinks_ValueAlreadyHoldingATargetId_IsLeftUnchangedAndDoesNotWarn()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(Map(), log.Add);

        var result = resolver.ResolveLinks("Default.aspx?ID=8559");

        Assert.Equal("Default.aspx?ID=8559", result);
        Assert.Equal(0, resolver.GetStats().unresolved);
        Assert.Equal(1, resolver.AlreadyLocalCount);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void ResolveLinks_LocalPageIdNotInTheMap_IsLeftUnchangedAndDoesNotWarn()
    {
        // A link to a host page this composition owns but the map misses (an owned page with no
        // SourcePageId) - still a destination value. The set passed here is the OWNED set from
        // ContentDeserializer.OwnedLocalPageIds, never every page on the host.
        var log = new List<string>();
        var resolver = new InternalLinkResolver(Map(), log.Add,
            localPageIds: new HashSet<int> { 9001 });

        var result = resolver.ResolveLinks("Default.aspx?ID=9001");

        Assert.Equal("Default.aspx?ID=9001", result);
        Assert.Equal(0, resolver.GetStats().unresolved);
        Assert.Equal(1, resolver.AlreadyLocalCount);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void ResolveLinks_GenuinelyUnknownId_StillWarnsAndCounts()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(Map(), log.Add);

        var result = resolver.ResolveLinks("Default.aspx?ID=77777");

        Assert.Equal("Default.aspx?ID=77777", result);
        Assert.Equal(1, resolver.GetStats().unresolved);
        Assert.Equal(0, resolver.AlreadyLocalCount);
        Assert.Contains(log, l => l.Contains("WARNING: Unresolvable page ID 77777"));
    }

    [Fact]
    public void ResolveLinks_UnresolvableWarning_NamesEntryDocumentAndField()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(Map(), log.Add)
        {
            CurrentEntry = "swift-content",
            CurrentDocument = "page 'About' (ID=8559)",
            CurrentLocator = "item|Swift-v2_Button|41|FirstButton"
        };

        resolver.ResolveLinks("Default.aspx?ID=77777");

        var warning = Assert.Single(log, l => l.Contains("WARNING: Unresolvable page ID 77777"));
        Assert.Contains("entry 'swift-content'", warning);
        Assert.Contains("document 'page 'About' (ID=8559)'", warning);
        Assert.Contains("field 'item|Swift-v2_Button|41|FirstButton'", warning);
    }

    [Fact]
    public void ResolveLinks_WithNoContextSet_WarningHasNoEmptyBrackets()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(Map(), log.Add);

        resolver.ResolveLinks("Default.aspx?ID=77777");

        var warning = Assert.Single(log, l => l.Contains("WARNING: Unresolvable page ID"));
        Assert.DoesNotContain("[", warning);
    }

    [Fact]
    public void ResolveLinks_SourceIdStillResolves_WhenTheSameValueCouldLookLocal()
    {
        // Source-map lookup wins: the already-local guard only covers the branch that
        // used to warn, so a first-pass Replace is unaffected.
        var resolver = new InternalLinkResolver(Map());
        Assert.Equal("Default.aspx?ID=8514", resolver.ResolveLinks("Default.aspx?ID=47"));
    }

    // -----------------------------------------------------------------------
    // #15 — raw numerics and page id 0
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveLinks_RawNumeric_IsNotRemappedOnANonReferenceField()
    {
        // ImageAspectRatio ships "0"; 47 is the collision case that produced 8453 on the host.
        var resolver = new InternalLinkResolver(Map());

        Assert.Equal("47", resolver.ResolveLinks("47", allowRawNumericPageIds: false));
        Assert.Equal("0", resolver.ResolveLinks("0", allowRawNumericPageIds: false));
    }

    [Fact]
    public void ResolveLinks_RawNumeric_IsRemappedOnAReferenceField()
    {
        var resolver = new InternalLinkResolver(Map());
        Assert.Equal("8514", resolver.ResolveLinks("47", allowRawNumericPageIds: true));
    }

    [Fact]
    public void ResolveLinks_RawZero_IsNeverMapped_EvenOnAReferenceField()
    {
        var mapWithZero = new Dictionary<int, int> { { 0, 8453 } };
        var resolver = new InternalLinkResolver(mapWithZero);

        Assert.Equal("0", resolver.ResolveLinks("0", allowRawNumericPageIds: true));
    }

    [Fact]
    public void ResolveLinks_LinkFormWithIdZero_IsLeftAloneAndDoesNotWarn()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { { 0, 8453 } }, log.Add);

        var result = resolver.ResolveLinks("Default.aspx?ID=0");

        Assert.Equal("Default.aspx?ID=0", result);
        Assert.Equal(0, resolver.GetStats().resolved);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void ResolveLinks_DefaultOverload_KeepsRawNumericRemapping()
    {
        // The single-argument overload is the SqlTable / page-shortcut path; unchanged.
        var resolver = new InternalLinkResolver(Map());
        Assert.Equal("8514", resolver.ResolveLinks("47"));
    }

    [Fact]
    public void BuildSourceToTargetMap_SkipsSourcePageIdZero()
    {
        var guid = Guid.NewGuid();
        var pages = new List<SerializedPage>
        {
            new()
            {
                PageUniqueId = guid,
                SourcePageId = 0,
                Name = "Zero",
                MenuText = "Zero",
                UrlName = "zero",
                SortOrder = 1
            }
        };

        var map = InternalLinkResolver.BuildSourceToTargetMap(pages, new Dictionary<Guid, int> { { guid, 8453 } });

        Assert.Empty(map);
    }
}
