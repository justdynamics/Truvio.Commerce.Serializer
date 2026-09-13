#pragma warning disable CS0618 // the deprecated aliases are the subject under test
using System.Reflection;
using Truvio.Commerce.Serializer.AdminUI.Commands;
using Dynamicweb.CoreUI.Data;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.AdminUI;

/// <summary>
/// The pre-1.0 command names stay as thin deprecated subclasses through the beta: same parameters
/// and status, message responses end with a deprecation notice.
/// </summary>
public class DeprecatedCommandAliasTests
{
    [Theory]
    [InlineData(typeof(SerializerSerializeCommand), typeof(SerializeCommand))]
    [InlineData(typeof(SerializerDeserializeCommand), typeof(DeserializeCommand))]
    [InlineData(typeof(SerializeSubtreeCommand), typeof(PackageDownloadCommand))]
    public void Alias_SubclassesCanonical_AndIsObsolete(Type alias, Type canonical)
    {
        Assert.True(alias.IsSubclassOf(canonical));
        Assert.True(alias.IsSealed);
        Assert.NotNull(alias.GetCustomAttribute<ObsoleteAttribute>());
        Assert.Null(canonical.GetCustomAttribute<ObsoleteAttribute>());
    }

    [Fact]
    public void SerializerSerialize_SameStatus_MessageEndsWithNotice()
    {
        var alias = new SerializerSerializeCommand { Mode = "bogus" }.Handle();
        var canonical = new SerializeCommand { Mode = "bogus" }.Handle();

        Assert.Equal(canonical.Status, alias.Status);
        Assert.Equal(CommandResult.ResultType.Invalid, alias.Status);
        Assert.StartsWith(canonical.Message!, alias.Message);
        Assert.EndsWith("Deprecated: 'SerializerSerialize' is an alias of 'Serialize' and is removed in the 1.0.0 release. Call 'Serialize' instead.", alias.Message);
        Assert.DoesNotContain("Deprecated", canonical.Message);
    }

    [Fact]
    public void SerializerDeserialize_SameStatus_MessageEndsWithNotice()
    {
        var alias = new SerializerDeserializeCommand { Mode = "bogus" }.Handle();
        var canonical = new DeserializeCommand { Mode = "bogus" }.Handle();

        Assert.Equal(canonical.Status, alias.Status);
        Assert.EndsWith("Deprecated: 'SerializerDeserialize' is an alias of 'Deserialize' and is removed in the 1.0.0 release. Call 'Deserialize' instead.", alias.Message);
    }

    [Fact]
    public void SerializeSubtree_IsPackageDownload()
    {
        var alias = new SerializeSubtreeCommand { PageId = 0, AreaId = 1 }.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, alias.Status);
        Assert.StartsWith("PageId is required", alias.Message);
        Assert.Contains("'SerializeSubtree' is an alias of 'PackageDownload'", alias.Message);
    }
}
