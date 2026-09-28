using Dynamicweb.Content.Items;
using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.CoreUI.Data;
using Dynamicweb.CoreUI.Editors;
using Dynamicweb.CoreUI.Editors.Inputs;
using Dynamicweb.CoreUI.Editors.Lists;
using Dynamicweb.CoreUI.Editors.Selectors;
using Dynamicweb.CoreUI.Screens;
using static Dynamicweb.CoreUI.Editors.Inputs.ListBase;

namespace Truvio.Commerce.Serializer.AdminUI.Screens;

public sealed class PredicateEditScreen : EditScreenBase<PredicateEditModel>
{
    protected override void BuildEditScreen()
    {
        // Shared fields always visible
        var sharedFields = new List<EditorBase>
        {
            EditorFor(m => m.Name),
            EditorFor(m => m.ProviderType),
            EditorFor(m => m.Mode)  // Phase 40 D-06: per-predicate mode editor
        };

        var groups = new List<LayoutWrapper>
        {
            new("Configuration", sharedFields)
        };

        // Per D-09: only show provider-specific fields when ProviderType is selected
        if (Model?.ProviderType == "Content")
        {
            groups.Add(new("Content Settings", new List<EditorBase>
            {
                EditorFor(m => m.AreaId),
                EditorFor(m => m.PageId),
                EditorFor(m => m.IncludeLanguageLayers),
                EditorFor(m => m.Excludes)
            }));
            groups.Add(new("Filtering", new List<EditorBase>
            {
                EditorFor(m => m.ExcludeFields),
                EditorFor(m => m.ExcludeXmlElements)
            }));
            groups.Add(new("Area Column Filtering", new List<EditorBase>
            {
                EditorFor(m => m.ExcludeAreaColumns)
            }));
        }
        else if (Model?.ProviderType == "SqlTable")
        {
            groups.Add(new("SQL Table Settings", new List<EditorBase>
            {
                EditorFor(m => m.Table).WithReloadOnChange(),
                EditorFor(m => m.NameColumn),
                EditorFor(m => m.CompareColumns),
                EditorFor(m => m.WhereClause),
                EditorFor(m => m.ServiceCaches)
            }));
            groups.Add(new("Filtering", new List<EditorBase>
            {
                EditorFor(m => m.XmlColumns),
                EditorFor(m => m.ExcludeFields),
                EditorFor(m => m.IncludeFields),
                EditorFor(m => m.ExcludeXmlElements)
            }));
            groups.Add(new("Cross-Environment Link Resolution", new List<EditorBase>
            {
                EditorFor(m => m.ResolveLinksInColumns)
            }));
        }
        // else: no ProviderType selected — show nothing below Configuration (D-09)

        AddComponents("Predicate", groups);
    }

    protected override EditorBase? GetEditor(string property) => property switch
    {
        nameof(PredicateEditModel.ProviderType) => CreateProviderTypeSelect(),
        // Phase 40 D-06: Mode is editable on both new and existing predicates.
        nameof(PredicateEditModel.Mode) => new Select
        {
            // Phase 41 D-11: clean labels (no parens). Explanatory copy lives on the Mode
            // [ConfigurableProperty] hint per D-12 (Plan 41-03 lands the hint copy + string-Mode
            // model migration).
            SortOrder = OrderBy.Default,
            Options = new List<ListOption>
            {
                new() { Value = nameof(SerializerMode.Replace), Label = "Replace" },
                new() { Value = nameof(SerializerMode.Merge), Label = "Merge" }
            }
        },
        nameof(PredicateEditModel.AreaId) => SelectorBuilder.CreateAreaSelector(
            value: Model?.AreaId > 0 ? Model.AreaId : null,
            hideDeactivated: true
        ).WithReloadOnChange(),
        nameof(PredicateEditModel.PageId) => SelectorBuilder.CreatePageSelector(
            value: Model?.PageId > 0 ? Model.PageId : null,
            areaId: Model?.AreaId > 0 ? Model.AreaId : null,
            hint: "Select root page for this predicate"
        ),
        nameof(PredicateEditModel.Excludes) => CreateContentPathSelectMultiDual(Model?.AreaId, Model?.Excludes),
        nameof(PredicateEditModel.ServiceCaches) => new Textarea
        {
            Label = "Service Caches",
            Explanation = "One fully-qualified DW cache type per line. Cleared after deserialization."
        },
        nameof(PredicateEditModel.ExcludeFields) => Model?.ProviderType == "SqlTable"
            ? CreateColumnSelectMultiDual(Model?.Table, Model?.ExcludeFields,
                "Exclude Fields", "Select columns to exclude from serialization.")
            : CreateItemTypeFieldSelectMultiDual(Model?.ExcludeFields,
                "Exclude Fields",
                "Select ItemType / PropertyItem field systemNames to exclude from serialization. " +
                "Applies to every page, paragraph, and area ItemType reached by this predicate."),
        nameof(PredicateEditModel.XmlColumns) => CreateColumnSelectMultiDual(Model?.Table, Model?.XmlColumns,
            "XML Columns", "Select columns containing XML to pretty-print in YAML."),
        nameof(PredicateEditModel.ExcludeXmlElements) => new Textarea
        {
            Label = "Exclude XML Elements",
            Explanation = "One element name per line. These XML elements will be stripped from embedded XML blobs."
        },
        nameof(PredicateEditModel.ExcludeAreaColumns) => CreateAreaColumnSelectMultiDual(
            Model?.AreaId, Model?.ExcludeAreaColumns,
            "Exclude Area Columns", "Select area table columns to exclude from serialization. " +
            "AreaName keeps the target's website name when a Replace writes the area."),
        // Phase 37-03: SqlTable WHERE + runtime-exclude opt-in
        nameof(PredicateEditModel.WhereClause) => new Textarea
        {
            Label = "Where Clause",
            Explanation = "SQL WHERE clause applied at serialize. Identifiers must exist in the table schema. "
                         + "No semicolons, comments, or subqueries. Example: AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')"
        },
        nameof(PredicateEditModel.IncludeFields) => CreateColumnSelectMultiDual(Model?.Table, Model?.IncludeFields,
            "Include Fields",
            "Columns that stay in serialized output even if auto-excluded by the runtime-exclusion registry."),
        // Phase 37-05: SqlTable cross-environment link resolution opt-in (LINK-02).
        nameof(PredicateEditModel.ResolveLinksInColumns) => CreateColumnSelectMultiDual(Model?.Table, Model?.ResolveLinksInColumns,
            "Resolve Links In Columns",
            "Columns whose string values contain Default.aspx?ID=N references. At deserialize, " +
            "source page IDs are rewritten to target page IDs using the cross-environment map. " +
            "Example: UrlPathRedirect"),
        _ => null
    };

    /// <summary>
    /// Excludes as a picker over the area's REAL content paths instead of free text — a
    /// mistyped path silently excludes nothing, so the options come from walking the live
    /// page tree. Saved paths that no longer match a live page are merged into the option
    /// set so they stay visible (and removable) rather than vanishing on next edit.
    /// Value is NOT set here: EditScreenBase.BuildEditor binds the editor's Value from the
    /// model property (List&lt;string&gt;) after GetEditor returns — anything assigned here
    /// is overwritten with the raw model value (ItemTypeEditScreen precedent).
    /// </summary>
    private SelectMultiDual CreateContentPathSelectMultiDual(int? areaId, List<string>? currentValues)
    {
        var editor = new SelectMultiDual
        {
            Label = "Excludes",
            Explanation = "Select pages to exclude from sync. An exclude covers the page and its entire subtree.",
            SortOrder = OrderBy.Default
        };

        if (areaId is null or <= 0)
        {
            editor.Explanation = "Select an area to pick exclude paths.";
            return editor;
        }

        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in Dynamicweb.Content.Services.Pages.GetRootPagesForArea(areaId.Value))
                CollectContentPaths(root, "/" + root.MenuText, paths);
        }
        catch (Exception ex)
        {
            editor.Explanation = $"Could not read the area's pages: {ex.Message}";
        }

        foreach (var s in NonEmpty(currentValues))
            paths.Add(s);

        editor.Options = paths
            .Select(p => new ListOption { Value = p, Label = p })
            .ToList();

        return editor;
    }

    /// <summary>
    /// Non-empty entries of a saved multi-value list, NOT trimmed: the framework binds the
    /// raw model values as the editor's Value, so merged options must match them verbatim
    /// or the saved entry renders as unselected.
    /// </summary>
    private static List<string> NonEmpty(List<string>? values) =>
        (values ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    private static void CollectContentPaths(Dynamicweb.Content.Page page, string path, SortedSet<string> paths)
    {
        paths.Add(path);
        foreach (var child in Dynamicweb.Content.Services.Pages.GetPagesByParentID(page.ID))
            CollectContentPaths(child, path + "/" + child.MenuText, paths);
    }

    private SelectMultiDual CreateColumnSelectMultiDual(string? tableName, List<string>? currentValues, string label, string explanation)
    {
        var editor = new SelectMultiDual
        {
            Label = label,
            Explanation = explanation,
            SortOrder = OrderBy.Default
        };

        if (string.IsNullOrWhiteSpace(tableName))
        {
            editor.Explanation = "Enter a table name to see available columns.";
            return editor;
        }

        // Validate table name to prevent SQL injection via INFORMATION_SCHEMA queries
        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            editor.Explanation = "Invalid table name format.";
            return editor;
        }

        try
        {
            var metadataReader = new DataGroupMetadataReader(new DwSqlExecutor());
            var columnTypes = metadataReader.GetColumnTypes(tableName);

            if (columnTypes.Count == 0)
            {
                editor.Explanation = "Table not found in database. Verify the table name.";
                return editor;
            }

            // Saved values that are no longer live columns stay visible (and removable):
            // merge them into the options. Value itself is bound from the model property
            // by EditScreenBase.BuildEditor after GetEditor returns.
            var columns = new SortedSet<string>(columnTypes.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var s in NonEmpty(currentValues))
                columns.Add(s);

            editor.Options = columns
                .Select(c => new ListOption { Value = c, Label = c })
                .ToList();
        }
        catch (Exception ex)
        {
            editor.Explanation = $"Could not query database columns: {ex.Message}";
        }

        return editor;
    }

    private SelectMultiDual CreateAreaColumnSelectMultiDual(int? areaId, List<string>? currentValues, string label, string explanation)
    {
        var editor = new SelectMultiDual
        {
            Label = label,
            Explanation = explanation,
            SortOrder = OrderBy.Default
        };

        if (areaId is null or <= 0)
        {
            editor.Explanation = "Select an area to see available columns.";
            return editor;
        }

        try
        {
            var metadataReader = new DataGroupMetadataReader(new DwSqlExecutor());
            var columnTypes = metadataReader.GetColumnTypes("Area");

            if (columnTypes.Count == 0)
            {
                editor.Explanation = "Area table not found in database.";
                return editor;
            }

            // Issue #43: AreaName stays pickable. Listing it keeps the target's website name
            // when a Replace writes the area row (the name is source-wins otherwise).
            var dtoColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AreaID", "AreaSort", "AreaItemType", "AreaItemId", "AreaUniqueId"
            };

            var columns = new SortedSet<string>(
                columnTypes.Keys.Where(c => !dtoColumns.Contains(c)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var s in NonEmpty(currentValues))
                columns.Add(s);

            editor.Options = columns
                .Select(c => new ListOption { Value = c, Label = c })
                .ToList();
        }
        catch (Exception ex)
        {
            editor.Explanation = $"Could not query area columns: {ex.Message}";
        }

        return editor;
    }

    /// <summary>
    /// Build a SelectMultiDual whose options are the distinct ItemType field systemNames
    /// across every ItemType registered with DW's ItemManager. The union spans page, paragraph,
    /// and area-level ItemTypes — a Content predicate can reach any of them — so a single
    /// dropdown covers the full field surface a user may want to exclude.
    /// </summary>
    private SelectMultiDual CreateItemTypeFieldSelectMultiDual(List<string>? currentValues, string label, string explanation)
    {
        var editor = new SelectMultiDual
        {
            Label = label,
            Explanation = explanation,
            SortOrder = OrderBy.Default
        };

        try
        {
            var metadata = ItemManager.Metadata.GetMetadata();
            if (metadata?.Items == null || metadata.Items.Count == 0)
            {
                editor.Explanation = "No ItemTypes registered. Check that Truvio Commerce is initialized.";
                return editor;
            }

            // Union all field systemNames across every registered ItemType. GetItemFields
            // includes inherited fields (matches ItemTypeBySystemNameQuery pattern).
            var fieldNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var itemType in metadata.Items)
            {
                try
                {
                    var fields = ItemManager.Metadata.GetItemFields(itemType);
                    foreach (var f in fields)
                    {
                        if (!string.IsNullOrWhiteSpace(f.SystemName))
                            fieldNames.Add(f.SystemName);
                    }
                }
                catch
                {
                    // One broken ItemType shouldn't hide the rest — skip and continue.
                }
            }

            // Merge saved values so they stay visible even when no longer live field names.
            // Value itself is bound from the model property by EditScreenBase.BuildEditor.
            foreach (var s in NonEmpty(currentValues))
                fieldNames.Add(s);

            editor.Options = fieldNames
                .Select(f => new ListOption { Value = f, Label = f })
                .ToList();
        }
        catch (Exception ex)
        {
            editor.Explanation = $"Could not read ItemType metadata: {ex.Message}";
        }

        return editor;
    }

    private Select CreateProviderTypeSelect()
    {
        var select = new Select
        {
            SortOrder = OrderBy.Default,
            Options = new List<ListOption>
            {
                new() { Value = "Content", Label = "Content" },
                new() { Value = "SqlTable", Label = "SQL Table" }
            }
        };

        // D-02: ProviderType locked after creation — only reload on change for new predicates
        if (Model?.Index < 0)
            return select.WithReloadOnChange();

        // For existing predicates, show current value but don't trigger reload
        // (SavePredicateCommand preserves original ProviderType on updates)
        return select;
    }

    protected override string GetScreenName() =>
        Model?.Index >= 0 ? $"Edit Predicate: {Model.Name}" : "New Predicate";

    protected override CommandBase<PredicateEditModel> GetSaveCommand() => new SavePredicateCommand();
}
