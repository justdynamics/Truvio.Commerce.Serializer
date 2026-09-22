using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Issue #32: an option-list field whose source yields a page or paragraph id is a reference
/// whatever its editor, while #15's guarantee holds (a numeric literal in any other field
/// stays literal). Fixtures are field fragments copied from the Swift 2.4 item-type XML on
/// foundry.mydwsite4.com (Files/System/Items), editor configuration elided.
/// </summary>
public class OptionSourceReferenceTests
{
    private const string ProductListComponentSelector = """
        <?xml version="1.0" encoding="utf-16" standalone="yes"?>
        <items>
          <item category="Swift-v2/Ecom" name="Product list component selector" systemName="Swift-v2_ProductListComponentSelector">
            <fields>
              <field name="Component source" systemName="ComponentSource" description="" type="System.String, System.Private.CoreLib" excludeFromSearch="True" defaultValueCulture="en-DK" defaultValue="">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Title" valueField="PageId" itemSystemName="Swift-v2_ProductListComponent" itemSourceType="3" itemSourceId="0" includeChildItems="False" includeParagraphItems="False" />
                </options>
              </field>
              <field name="Title" systemName="Title" description="" type="System.String, System.Private.CoreLib" excludeFromSearch="False" defaultValueCulture="en-US" defaultValue="">
                <editor type="Dynamicweb.Content.Items.Editors.TextEditor, Dynamicweb">
                  <editorConfuguration />
                </editor>
              </field>
            </fields>
          </item>
        </items>
        """;

    private const string ProductComponentSelector = """
        <items>
          <item name="Product component selector" systemName="Swift-v2_ProductComponentSelector">
            <fields>
              <field name="Component source" systemName="ComponentSource" description="" type="System.String, System.Private.CoreLib" excludeFromSearch="True" defaultValueCulture="en-DK" defaultValue="">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Title" valueField="PageId" itemSystemName="Swift-v2_ProductComponent" itemSourceType="3" itemSourceId="0" includeChildItems="False" includeParagraphItems="False" />
                </options>
              </field>
            </fields>
          </item>
        </items>
        """;

    private const string ProductBom = """
        <items>
          <item name="Product BOM" systemName="Swift-v2_ProductBom">
            <fields>
              <field name="List component source" systemName="ListComponentSource" description="" type="System.String, System.Private.CoreLib" excludeFromSearch="True">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Title" valueField="PageId" itemSystemName="Swift-v2_ProductComponent" itemSourceType="3" itemSourceId="0" includeChildItems="False" includeParagraphItems="False" />
                </options>
              </field>
            </fields>
          </item>
        </items>
        """;

    /// <summary>ImageAspectRatio (#15) and a RadioButtonList with literal numeric options.</summary>
    private const string ProductMediaTableAndGrid = """
        <items>
          <item name="Product media table" systemName="Swift-v2_ProductMediaTable">
            <fields>
              <field name="Image aspect ratio" systemName="ImageAspectRatio" description="" type="System.String, System.Private.CoreLib" excludeFromSearch="False" defaultValueCulture="en-GB" defaultValue="1-1">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="Static">
                  <Static>
                    <option name="Original" value="0" icon="Templates/Designs/Swift-v2/Assets/images/ItemTypes/AspectOriginal.svg" />
                    <option name="1:1" value="100%25" icon="Templates/Designs/Swift-v2/Assets/images/ItemTypes/Aspect_1-1.svg" />
                    <option name="4:3" value="75%25" icon="Templates/Designs/Swift-v2/Assets/images/ItemTypes/Aspect_4-3.svg" />
                  </Static>
                </options>
              </field>
              <field name="Grid columns" systemName="GridSize" description="Choose between 3, 4 or 5 columns in the grid." type="System.String, System.Private.CoreLib" excludeFromSearch="True" defaultValueCulture="en-GB" defaultValue="3">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="Static">
                  <Static>
                    <option name="3 columns" value="3" icon="/Templates/Designs/Swift-v2/Assets/images/ItemTypes/ArticleListGrid3.svg" />
                    <option name="4 columns" value="4" icon="/Templates/Designs/Swift-v2/Assets/images/ItemTypes/ArticleListGrid4.svg" />
                    <option name="5 columns" value="5" icon="/Templates/Designs/Swift-v2/Assets/images/ItemTypes/ArticleListGrid5.svg" />
                  </Static>
                </options>
              </field>
              <field name="Tags" systemName="Tags" description="" type="System.Collections.Generic.IEnumerable`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib" excludeFromSearch="False">
                <editor type="Dynamicweb.Content.Items.Editors.CheckboxListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Title" valueField="Id" itemSystemName="Swift_ArticleTag" itemSourceType="3" itemSourceId="3" includeChildItems="False" includeParagraphItems="True" />
                </options>
              </field>
              <field name="Service page" systemName="ServicePage" description="" type="System.String, System.Private.CoreLib">
                <editor type="Dynamicweb.Content.Items.Editors.LinkEditor, Dynamicweb">
                  <editorConfuguration />
                </editor>
              </field>
            </fields>
          </item>
        </items>
        """;

    private const string Emails = """
        <items>
          <item name="Emails" systemName="Swift_Emails">
            <fields>
              <field name="Background theme" systemName="EmailTheme" description="The default background theme " type="System.String, System.Private.CoreLib" excludeFromSearch="False">
                <editor type="Dynamicweb.Content.Items.Editors.RadioButtonListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Name" valueField="ParagraphId" itemSystemName="Swift_Theme" itemSourceType="3" itemSourceId="0" includeChildItems="False" includeParagraphItems="True" />
                </options>
              </field>
            </fields>
          </item>
        </items>
        """;

    private const string ArticleListFilter = """
        <items>
          <item name="Article list filter" systemName="Swift_ArticleListFilter">
            <fields>
              <field name="Article list categories" systemName="Lists" description="" type="System.Collections.Generic.IEnumerable`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib" excludeFromSearch="False">
                <editor type="Dynamicweb.Content.Items.Editors.CheckboxListEditor`1, Dynamicweb">
                  <editorConfuguration />
                </editor>
                <options sourceType="ItemType">
                  <ItemType nameField="Title" valueField="PageId" itemSystemName="Swift_ArticleListPage" itemSourceType="3" itemSourceId="0" includeChildItems="False" includeParagraphItems="False" />
                </options>
              </field>
            </fields>
          </item>
        </items>
        """;

    // Source page 38 = "Product Components / Product List" -> host 8505 (issue #32 measurement).
    private static Dictionary<int, int> PageMap() => new()
    {
        { 37, 8504 }, { 38, 8505 }, { 39, 8506 }, { 40, 8507 }, { 47, 8514 }
    };

    private static Dictionary<int, int> ParagraphMap() => new() { { 120, 22501 }, { 121, 22502 } };

    private static (InternalLinkResolver Resolver, List<string> Log) Resolver(
        Dictionary<int, int>? paragraphMap = null)
    {
        var log = new List<string>();
        return (new InternalLinkResolver(PageMap(), log.Add, sourceToTargetParagraphIds: paragraphMap), log);
    }

    private static Dictionary<string, object?> Resolve(
        string itemTypeXml, Dictionary<string, object?> fields, InternalLinkResolver resolver)
    {
        var refs = ReferenceFieldClassifier.FromItemTypeXml(itemTypeXml);
        return ContentDeserializer.ResolveLinkFields(
            fields, resolver, key => $"item|X|1|{key}", refs.EditorFields, refs.OptionFields);
    }

    [Theory]
    [InlineData("ItemType", "PageId", ReferenceKind.Page)]
    [InlineData("ItemType", "PageID", ReferenceKind.Page)]
    [InlineData("ItemType", "ParagraphId", ReferenceKind.Paragraph)]
    [InlineData("Sql", "PageId", ReferenceKind.Page)]
    [InlineData("ItemType", "Id", ReferenceKind.None)]
    [InlineData("ItemType", "CSSClassName", ReferenceKind.None)]
    [InlineData("Sql", "OrderContextId", ReferenceKind.None)]
    [InlineData("Sql", "VariantGroupId", ReferenceKind.None)]
    [InlineData("Static", "PageId", ReferenceKind.None)]
    [InlineData("Folder", null, ReferenceKind.None)]
    [InlineData(null, "PageId", ReferenceKind.None)]
    public void ClassifyOptionSource_OnlyPageAndParagraphValueFieldsAreReferences(
        string? sourceType, string? valueField, ReferenceKind expected)
    {
        Assert.Equal(expected, ReferenceFieldClassifier.ClassifyOptionSource(sourceType, valueField));
    }

    [Fact]
    public void FromItemTypeXml_ClassifiesOptionSourcesAndEditors()
    {
        var selector = ReferenceFieldClassifier.FromItemTypeXml(ProductListComponentSelector);
        Assert.Equal(ReferenceKind.Page, selector.OptionFields["ComponentSource"]);
        Assert.DoesNotContain("Title", selector.EditorFields);

        var media = ReferenceFieldClassifier.FromItemTypeXml(ProductMediaTableAndGrid);
        Assert.Empty(media.OptionFields);
        Assert.Equal(new[] { "ServicePage" }, media.EditorFields.ToArray());

        Assert.Equal(ReferenceKind.Paragraph,
            ReferenceFieldClassifier.FromItemTypeXml(Emails).OptionFields["EmailTheme"]);
    }

    [Theory]
    [InlineData(nameof(ProductListComponentSelector), "ComponentSource", "38", "8505")]
    [InlineData(nameof(ProductComponentSelector), "ComponentSource", "37", "8504")]
    [InlineData(nameof(ProductBom), "ListComponentSource", "39", "8506")]
    public void ComponentSelectorPageId_ResolvesThroughThePageMap(
        string fixture, string field, string source, string expected)
    {
        var xml = fixture switch
        {
            nameof(ProductListComponentSelector) => ProductListComponentSelector,
            nameof(ProductComponentSelector) => ProductComponentSelector,
            _ => ProductBom
        };
        var (resolver, log) = Resolver();

        var changed = Resolve(xml, new() { [field] = source }, resolver);

        Assert.Equal(expected, Assert.Contains(field, changed));
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void LiteralNumericsStayLiteral_EvenWhenTheyCollideWithMappedIds()
    {
        var (resolver, log) = Resolver(ParagraphMap());

        var changed = Resolve(ProductMediaTableAndGrid, new()
        {
            ["ImageAspectRatio"] = "0",   // #15: a ratio, not page 0
            ["GridSize"] = "38",          // static numeric option colliding with a mapped page id
            ["Tags"] = "120,121",         // item ids (valueField="Id"), colliding with paragraph ids
            ["ServicePage"] = "47"        // LinkEditor: still a reference
        }, resolver);

        Assert.DoesNotContain("ImageAspectRatio", changed);
        Assert.DoesNotContain("GridSize", changed);
        Assert.DoesNotContain("Tags", changed);
        Assert.Equal("8514", Assert.Contains("ServicePage", changed));
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void ParagraphIdOptionSource_ResolvesThroughTheParagraphMap()
    {
        // 38 is ALSO a source page id: the paragraph map must be the one consulted.
        var paragraphMap = new Dictionary<int, int>(ParagraphMap()) { { 38, 22600 } };
        var (resolver, log) = Resolver(paragraphMap);

        var changed = Resolve(Emails, new() { ["EmailTheme"] = "38" }, resolver);

        Assert.Equal("22600", Assert.Contains("EmailTheme", changed));
        Assert.Equal(1, resolver.GetStats().paragraphResolved);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void UnresolvablePageIdInOptionField_WarnsLikeOtherLinks()
    {
        var (resolver, log) = Resolver();

        var changed = Resolve(ProductListComponentSelector, new() { ["ComponentSource"] = "99" }, resolver);

        Assert.DoesNotContain("ComponentSource", changed);
        Assert.Contains(log, l => l.Contains("WARNING: Unresolvable page ID 99 in option field")
                                  && l.Contains("field 'item|X|1|ComponentSource'"));
        Assert.Equal(1, resolver.GetStats().unresolved);
    }

    [Fact]
    public void UnresolvableParagraphIdInOptionField_Warns()
    {
        var (resolver, log) = Resolver(ParagraphMap());

        Resolve(Emails, new() { ["EmailTheme"] = "999" }, resolver);

        Assert.Contains(log, l => l.Contains("WARNING: Unresolvable paragraph ID 999 in option field"));
        Assert.Equal(1, resolver.GetStats().paragraphUnresolved);
    }

    [Fact]
    public void ParagraphIdOptionSource_WithoutAParagraphMap_IsLeftAloneWithoutAWarning()
    {
        var (resolver, log) = Resolver(paragraphMap: null);

        var changed = Resolve(Emails, new() { ["EmailTheme"] = "120" }, resolver);

        Assert.DoesNotContain("EmailTheme", changed);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void AlreadyLocalPageIdInOptionField_IsLeftAloneWithoutAWarning()
    {
        // A merge over its own output reads 8505 back from the destination.
        var (resolver, log) = Resolver();

        var changed = Resolve(ProductListComponentSelector, new() { ["ComponentSource"] = "8505" }, resolver);

        Assert.DoesNotContain("ComponentSource", changed);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
        Assert.Equal(1, resolver.AlreadyLocalCount);
    }

    [Fact]
    public void CheckboxListOverPageIds_ResolvesEveryId()
    {
        var (resolver, _) = Resolver();

        var changed = Resolve(ArticleListFilter, new() { ["Lists"] = "38,40" }, resolver);

        Assert.Equal("8505,8507", Assert.Contains("Lists", changed));
    }

    [Fact]
    public void EmptyOrZeroOptionValue_IsNeverMapped()
    {
        var (resolver, log) = Resolver();

        var changed = Resolve(ProductBom, new() { ["ListComponentSource"] = "0" }, resolver);
        Assert.DoesNotContain("ListComponentSource", changed);

        changed = Resolve(ProductBom, new() { ["ListComponentSource"] = "" }, resolver);
        Assert.DoesNotContain("ListComponentSource", changed);
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }

    [Fact]
    public void DeferredPageIdInOptionField_IsRecordedAndFinalizedIdExact()
    {
        var log = new List<string>();
        var resolver = new InternalLinkResolver(PageMap(), log.Add,
            deferredSourcePageIds: new HashSet<int> { 55 });

        var changed = Resolve(ArticleListFilter, new() { ["Lists"] = "38,55" }, resolver);

        Assert.Equal("8505,55", Assert.Contains("Lists", changed));
        var record = Assert.Single(resolver.DeferredRecords);
        Assert.Equal(55, record.SourceId);
        Assert.Equal("8505,9001", DeferredLinkLedger.ReplaceSourceId("8505,55", 55, 9001));
        Assert.DoesNotContain(log, l => l.Contains("WARNING"));
    }
}
