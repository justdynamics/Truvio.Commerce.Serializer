using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #15: which editors declare a field as a page/paragraph/item reference — the only
/// fields on which a bare number may be remapped source→target.
/// </summary>
public class ReferenceFieldClassifierTests
{
    [Theory]
    [InlineData("Dynamicweb.Content.Items.Editors.LinkEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.ItemLinkEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.ButtonEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.LinkEditor, Dynamicweb")]
    [InlineData("linkeditor")]
    [InlineData("Acme.Custom.PageEditor")]
    [InlineData("Acme.Custom.ParagraphEditor")]
    public void IsReferenceEditor_TrueForReferenceEditors(string typeName)
    {
        Assert.True(ReferenceFieldClassifier.IsReferenceEditor(typeName));
    }

    [Theory]
    [InlineData("Dynamicweb.Content.Items.Editors.TextEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.StringEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.NumberEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.IntegerEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.RichTextEditor")]
    [InlineData("Dynamicweb.Content.Items.Editors.MediaEditor")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsReferenceEditor_FalseForEverythingElse(string? typeName)
    {
        Assert.False(ReferenceFieldClassifier.IsReferenceEditor(typeName));
    }

    [Fact]
    public void IsReferenceEditor_DoesNotMatchOnASubstringOfAnUnrelatedName()
    {
        // "LinkedDataEditor" is not a link editor; the rule matches the SUFFIX, not "link".
        Assert.False(ReferenceFieldClassifier.IsReferenceEditor("Acme.LinkedDataEditor"));
    }
}
