using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R7: the dry-run field diff of a list entry compares values in the form a
/// document carries them, so a value read back from YAML equals the host value it came from.
/// </summary>
public class ItemListFieldDiffTests
{
    private static readonly Func<string, bool> None = _ => false;

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "False")]
    [InlineData(42, "42")]
    [InlineData(42L, "42")]
    [InlineData("text", "text")]
    public void HostValue_EqualsItsYamlText(object host, string yaml)
    {
        Assert.Empty(ItemListFieldDiff.Overwrite(
            new Dictionary<string, object> { ["F"] = yaml },
            new Dictionary<string, object?> { ["F"] = host }, None));
    }

    [Fact]
    public void NullEmptyAndAbsent_AreTheSame()
    {
        Assert.Empty(ItemListFieldDiff.Overwrite(
            new Dictionary<string, object> { ["A"] = "" },
            new Dictionary<string, object?> { ["A"] = null, ["B"] = "", ["C"] = DBNull.Value }, None));
    }

    [Fact]
    public void Overwrite_NamesChangedAndNulledFields_SkipsIgnoredOnes()
    {
        var lines = ItemListFieldDiff.Overwrite(
            new Dictionary<string, object> { ["Title"] = "New", ["Sort"] = "9" },
            new Dictionary<string, object?> { ["Title"] = "Old", ["Perk"] = "Kept?", ["Sort"] = 1L, ["Id"] = "x" },
            name => name is "Sort" or "Id");

        Assert.Equal(new[] { "Title: 'Old' -> 'New'", "Perk: 'Kept?' -> '' (not in the document)" }, lines);
    }

    [Fact]
    public void Fill_NamesOnlyEmptyHostFieldsTheDocumentFills()
    {
        var lines = ItemListFieldDiff.Fill(
            new Dictionary<string, object> { ["Title"] = "New", ["Perk"] = "P", ["Badge"] = "" },
            new Dictionary<string, object?> { ["Title"] = "Old", ["Perk"] = null, ["Badge"] = null }, None);

        Assert.Equal(new[] { "Perk: '' -> 'P'" }, lines);
    }

    [Fact]
    public void ABlankButton_IsEmpty_AButtonWithALinkIsNot()
    {
        const string blank = "{\"SelectedValue\":\"\",\"Label\":\"\",\"Link\":\"\",\"LinkType\":\"page\",\"Style\":\"primary\"}";
        const string linked = "{\"SelectedValue\":\"42\",\"Label\":\"Go\",\"Link\":\"Default.aspx?ID=42\",\"LinkType\":\"page\",\"Style\":\"primary\"}";

        Assert.Empty(ItemListFieldDiff.Overwrite(new Dictionary<string, object>(), new Dictionary<string, object?> { ["Button"] = blank }, None));
        Assert.Single(ItemListFieldDiff.Overwrite(new Dictionary<string, object>(), new Dictionary<string, object?> { ["Button"] = linked }, None));
    }

    [Fact]
    public void AMapping_ComparesAsYaml()
    {
        var host = new Dictionary<object, object> { ["Link"] = "Default.aspx?ID=1", ["Label"] = "Go" };
        var same = new Dictionary<object, object> { ["Link"] = "Default.aspx?ID=1", ["Label"] = "Go" };
        var other = new Dictionary<object, object> { ["Link"] = "Default.aspx?ID=2", ["Label"] = "Go" };

        Assert.Empty(ItemListFieldDiff.Overwrite(new Dictionary<string, object> { ["B"] = same }, new Dictionary<string, object?> { ["B"] = host }, None));
        Assert.Single(ItemListFieldDiff.Overwrite(new Dictionary<string, object> { ["B"] = other }, new Dictionary<string, object?> { ["B"] = host }, None));
    }
}
