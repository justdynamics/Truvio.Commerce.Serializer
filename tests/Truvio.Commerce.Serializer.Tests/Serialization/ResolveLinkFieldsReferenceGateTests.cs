using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #15 at the call site: <see cref="ContentDeserializer.ResolveLinkFields"/> lets the
/// raw-numeric short-circuit fire only for fields the item type declares as references.
/// The literal case is the reported defect — <c>ImageAspectRatio: "0"</c> arriving as a page id.
/// </summary>
public class ResolveLinkFieldsReferenceGateTests
{
    private static Dictionary<int, int> Map() => new() { { 47, 8514 }, { 0, 8453 } };

    private static Dictionary<string, object?> Fields() => new()
    {
        ["ServicePage"] = "47",          // LinkEditor — a real page reference
        ["ImageAspectRatio"] = "0",      // a literal ratio, not a reference
        ["ImagePatternImages"] = "47"    // a literal that collides with a source page id
    };

    [Fact]
    public void ResolveLinkFields_OnlyRemapsRawNumerics_OnDeclaredReferenceFields()
    {
        var resolver = new InternalLinkResolver(Map());
        var referenceFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ServicePage" };

        var changed = ContentDeserializer.ResolveLinkFields(
            Fields(), resolver, key => $"item|Swift-v2_ProductMediaTable|1|{key}", referenceFields);

        Assert.Equal("8514", Assert.Contains("ServicePage", changed));
        Assert.DoesNotContain("ImageAspectRatio", changed);   // unchanged -> not written back
        Assert.DoesNotContain("ImagePatternImages", changed);
    }

    [Fact]
    public void ResolveLinkFields_WithUnknownMetadata_StaysPermissive()
    {
        // null = item-type metadata unreadable (replace mode ships the XML in the same run).
        var resolver = new InternalLinkResolver(Map());

        var changed = ContentDeserializer.ResolveLinkFields(
            Fields(), resolver, key => $"item|X|1|{key}", referenceFields: null);

        Assert.Equal("8514", Assert.Contains("ServicePage", changed));
        Assert.Equal("8514", Assert.Contains("ImagePatternImages", changed));
        // Page id 0 is never mapped, whatever the field.
        Assert.DoesNotContain("ImageAspectRatio", changed);
    }

    [Fact]
    public void ResolveLinkFields_LinkFormIsRewrittenOnEveryField()
    {
        var resolver = new InternalLinkResolver(Map());
        var fields = new Dictionary<string, object?> { ["SomeText"] = "see Default.aspx?ID=47" };

        var changed = ContentDeserializer.ResolveLinkFields(
            fields, resolver, key => $"item|X|1|{key}",
            referenceFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("see Default.aspx?ID=8514", Assert.Contains("SomeText", changed));
    }
}
