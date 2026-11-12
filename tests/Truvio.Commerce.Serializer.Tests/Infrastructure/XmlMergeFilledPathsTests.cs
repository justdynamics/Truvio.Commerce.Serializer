using Truvio.Commerce.Serializer.Infrastructure;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Content ownership R2: the XML merge reports WHERE it filled (<see cref="XmlFillPath"/>), and
/// <see cref="XmlMergeHelper.TransformFilled"/> rewrites values inside those parts only. The link
/// pass uses the pair to resolve source ids in elements a Merge filled into existing module
/// settings without re-reading the elements the host already had.
/// </summary>
public class XmlMergeFilledPathsTests
{
    private static string Mark(string value) => "[" + value + "]";

    private static string Describe(XmlFillPath path) =>
        string.Join("/", path.Elements) + (path.Attribute is null ? "" : "@" + path.Attribute);

    [Fact]
    public void FilledPaths_NameAddedAndEmptyElements_NotKeptOnes()
    {
        const string target = "<Root><A>1</A><B></B></Root>";
        const string source = "<Root><A>9</A><B>2</B><C>3</C></Root>";

        var (merged, fills, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        Assert.Equal("<Root><A>1</A><B>2</B><C>3</C></Root>", merged);
        Assert.Equal(2, fills.Count);
        Assert.Equal(new[] { "B", "C" }, paths.Select(Describe));
    }

    [Fact]
    public void FilledPaths_NestedElement_CarriesTheWholePath()
    {
        const string target = "<Root><Group><A>1</A></Group></Root>";
        const string source = "<Root><Group><A>1</A><B>2</B></Group></Root>";

        var (_, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        Assert.Equal(new[] { "Group/B" }, paths.Select(Describe));
    }

    [Fact]
    public void FilledPaths_UseTheNameAttributeAsTheKey()
    {
        const string target = "<Parameters><Parameter name=\"Url\">a</Parameter></Parameters>";
        const string source = "<Parameters><Parameter name=\"Url\">b</Parameter><Parameter name=\"Page\">c</Parameter></Parameters>";

        var (_, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        Assert.Equal(new[] { "Page" }, paths.Select(Describe));
    }

    [Fact]
    public void FilledPaths_AttributeFill_NamesTheAttribute()
    {
        const string target = "<Root><A>1</A></Root>";
        const string source = "<Root link=\"x\"><A>1</A></Root>";

        var (_, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        Assert.Equal(new[] { "@link" }, paths.Select(Describe));
    }

    [Fact]
    public void FilledPaths_NothingFilled_IsEmpty_AndTheDiagnosticsOverloadAgrees()
    {
        const string xml = "<Root><A>1</A></Root>";

        var (merged, fills, paths) = XmlMergeHelper.MergeWithFilledPaths(xml, xml);
        var (mergedOld, fillsOld) = XmlMergeHelper.MergeWithDiagnostics(xml, xml);

        Assert.Empty(paths);
        Assert.Empty(fills);
        Assert.Equal(mergedOld, merged);
        Assert.Empty(fillsOld);
    }

    [Fact]
    public void TransformFilled_TouchesOnlyTheNamedParts()
    {
        const string target = "<Root><A>1</A><Group><X>5</X></Group></Root>";
        const string source = "<Root><A>9</A><B>2</B><Group><X>6</X><Y>7</Y></Group></Root>";
        var (merged, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        var result = XmlMergeHelper.TransformFilled(merged, paths, Mark);

        Assert.Equal("<Root><A>1</A><Group><X>5</X><Y>[7]</Y></Group><B>[2]</B></Root>", result);
    }

    [Fact]
    public void TransformFilled_AFilledSubtree_IsTransformedWithEverythingBelowIt_ExceptTheNameKey()
    {
        const string target = "<Root><A>1</A></Root>";
        const string source = "<Root><A>1</A><Group name=\"G\" link=\"l\"><X>5</X></Group></Root>";
        var (merged, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        var result = XmlMergeHelper.TransformFilled(merged, paths, Mark);

        Assert.Equal("<Root><A>1</A><Group name=\"G\" link=\"[l]\"><X>[5]</X></Group></Root>", result);
    }

    [Fact]
    public void TransformFilled_AFilledAttribute_IsTransformedAlone()
    {
        const string target = "<Root><A>1</A></Root>";
        const string source = "<Root link=\"x\"><A>1</A></Root>";
        var (merged, _, paths) = XmlMergeHelper.MergeWithFilledPaths(target, source);

        Assert.Equal("<Root link=\"[x]\"><A>1</A></Root>", XmlMergeHelper.TransformFilled(merged, paths, Mark));
    }

    [Fact]
    public void TransformFilled_NothingChanges_ReturnsTheSameString()
    {
        // Unformatted input with insignificant differences must not be re-serialized (and the
        // paragraph re-saved) when no value changed.
        const string xml = "<Root><A>1</A>\n  <B>2</B></Root>";
        var paths = new[] { new XmlFillPath(new[] { "B" }, null) };

        Assert.Same(xml, XmlMergeHelper.TransformFilled(xml, paths, v => v));
        Assert.Same(xml, XmlMergeHelper.TransformFilled(xml, Array.Empty<XmlFillPath>(), Mark));
    }

    [Fact]
    public void TransformFilled_AMissingPathOrBrokenXml_LeavesTheTextAlone()
    {
        const string xml = "<Root><A>1</A></Root>";

        Assert.Same(xml, XmlMergeHelper.TransformFilled(xml, new[] { new XmlFillPath(new[] { "Gone" }, null) }, Mark));
        Assert.Equal("<Root><A>", XmlMergeHelper.TransformFilled("<Root><A>", new[] { new XmlFillPath(new[] { "A" }, null) }, Mark));
        Assert.Null(XmlMergeHelper.TransformFilled(null, new[] { new XmlFillPath(new[] { "A" }, null) }, Mark));
    }

    [Fact]
    public void TransformFilled_KeepsTheDeclaration()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Root><A>1</A></Root>";

        var result = XmlMergeHelper.TransformFilled(xml, new[] { new XmlFillPath(new[] { "A" }, null) }, Mark);

        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?><Root><A>[1]</A></Root>", result);
    }
}
