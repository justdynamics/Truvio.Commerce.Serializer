using Dynamicweb.Content;
using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

public sealed class SavePredicateCommand : CommandBase<PredicateEditModel>
{
    /// <summary>
    /// Optional override for testing -- bypasses ConfigPathResolver.
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Phase 37-03: identifier validator used to whitelist Table / NameColumn / ExcludeFields /
    /// IncludeFields / XmlColumns. Tests inject a fixture loader; production path uses the
    /// default ctor (live INFORMATION_SCHEMA lookup).
    /// </summary>
    public SqlIdentifierValidator? IdentifierValidator { get; set; }

    /// <summary>
    /// Phase 37-03: WHERE-clause validator. Tests can substitute a no-op if needed; production
    /// callers use the default instance.
    /// </summary>
    public SqlWhereClauseValidator? WhereValidator { get; set; }

    public override CommandResult Handle()
    {
        if (Model is null)
            return new() { Status = CommandResult.ResultType.Invalid, Message = "Model data must be given" };

        if (string.IsNullOrWhiteSpace(Model.Name))
            return new() { Status = CommandResult.ResultType.Invalid, Message = "Name is required" };

        // Phase 41 D-13 + T-41-01: validate Mode is a known SerializerMode value. Mirrors
        // ConfigLoader's case-insensitive Enum.TryParse gate on the JSON read path so admin-UI
        // saves and config-file loads share identical Mode validation semantics.
        if (!Enum.TryParse<SerializerMode>(Model.Mode, ignoreCase: true, out _))
            return new()
            {
                Status = CommandResult.ResultType.Invalid,
                Message = $"Mode must be 'Replace' or 'Merge' (case-insensitive); got '{Model.Mode}'."
            };

        try
        {
            var configPath = ConfigPath ?? ConfigPathResolver.FindOrCreateConfigFile();
            var config = ConfigLoader.Load(configPath);
            // Phase 40 D-01: predicates are a single flat list. Mode is per-predicate.
            var predicates = config.Predicates.ToList();

            // D-02: ProviderType locked after creation -- use existing type on update
            string providerType;
            if (Model.Index >= 0 && Model.Index < predicates.Count)
            {
                providerType = predicates[Model.Index].ProviderType;
            }
            else
            {
                providerType = Model.ProviderType;
            }

            if (string.IsNullOrWhiteSpace(providerType))
                return new() { Status = CommandResult.ResultType.Invalid, Message = "Provider Type is required" };

            // Provider-branched validation
            if (providerType == "Content")
            {
                if (Model.AreaId <= 0)
                    return new() { Status = CommandResult.ResultType.Invalid, Message = "Area is required for Content predicates" };
                // PageId is optional: PageId<=0 means full-Area selection (path "/").
            }
            else if (providerType == "SqlTable")
            {
                if (string.IsNullOrWhiteSpace(Model.Table))
                    return new() { Status = CommandResult.ResultType.Invalid, Message = "Table is required for SqlTable predicates" };
            }
            else
            {
                return new() { Status = CommandResult.ResultType.Invalid, Message = $"Unknown provider type: {providerType}" };
            }

            // Validate unique name (per D-11), excluding current index on edit
            var duplicateIndex = predicates.FindIndex(p =>
                string.Equals(p.Name, Model.Name, StringComparison.OrdinalIgnoreCase));
            if (duplicateIndex >= 0 && duplicateIndex != Model.Index)
                return new() { Status = CommandResult.ResultType.Invalid, Message = $"A predicate with the name '{Model.Name}' already exists (duplicate)" };

            // Shared filtering fields (apply to both Content and SqlTable). SelectMultiDual-bound
            // properties arrive as List<string>; Textarea-bound ones as newline-joined strings.
            var excludeFields = Sanitize(Model.ExcludeFields);

            var excludeXmlElements = (Model.ExcludeXmlElements ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToList();

            var xmlColumns = Sanitize(Model.XmlColumns);

            var excludeAreaColumns = Sanitize(Model.ExcludeAreaColumns);

            // Phase 37-03: runtime-exclude opt-in list (SqlTable only, ignored for Content).
            var includeFields = Sanitize(Model.IncludeFields);

            // Phase 37-05 / LINK-02: SqlTable link-resolution column opt-in.
            var resolveLinksInColumns = Sanitize(Model.ResolveLinksInColumns);

            // Build predicate based on provider type
            ProviderPredicateDefinition predicate;

            if (providerType == "Content")
            {
                // Resolve page path from PageId via DW Services when available.
                // PageId<=0 -> full-Area selection, path is "/".
                string path;
                if (Model.PageId <= 0)
                {
                    path = "/";
                }
                else
                {
                    try
                    {
                        var page = Services.Pages?.GetPage(Model.PageId);
                        path = page?.GetBreadcrumbPath()
                            ?? (Model.Index >= 0 && Model.Index < predicates.Count
                                ? predicates[Model.Index].Path
                                : $"/page-{Model.PageId}");
                    }
                    catch
                    {
                        // DW runtime not available (e.g., unit tests) -- use fallback path
                        path = Model.Index >= 0 && Model.Index < predicates.Count
                            ? predicates[Model.Index].Path
                            : $"/page-{Model.PageId}";
                    }
                }

                var excludes = Sanitize(Model.Excludes);

                predicate = new ProviderPredicateDefinition
                {
                    Name = Model.Name.Trim(),
                    Mode = ParseMode(Model.Mode),
                    ProviderType = "Content",
                    Path = path,
                    AreaId = Model.AreaId,
                    PageId = Model.PageId,
                    IncludeLanguageLayers = Model.IncludeLanguageLayers,
                    Excludes = excludes,
                    ExcludeFields = excludeFields,
                    ExcludeXmlElements = excludeXmlElements,
                    ExcludeAreaColumns = excludeAreaColumns
                };
            }
            else // SqlTable
            {
                var serviceCaches = (Model.ServiceCaches ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

                // PR #22 review: the edit screen has no fields for keyColumns / replaceStrategy,
                // so an edit carries them over from the predicate being replaced instead of
                // dropping them on save.
                var existing = Model.Index >= 0 && Model.Index < predicates.Count
                    && string.Equals(predicates[Model.Index].ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase)
                    ? predicates[Model.Index]
                    : null;

                predicate = new ProviderPredicateDefinition
                {
                    Name = Model.Name.Trim(),
                    Mode = ParseMode(Model.Mode),
                    ProviderType = "SqlTable",
                    KeyColumns = existing?.KeyColumns.ToList() ?? new List<string>(),
                    ReplaceStrategy = existing?.ReplaceStrategy,
                    Table = Model.Table?.Trim(),
                    NameColumn = string.IsNullOrWhiteSpace(Model.NameColumn) ? null : Model.NameColumn.Trim(),
                    CompareColumns = string.IsNullOrWhiteSpace(Model.CompareColumns) ? null : Model.CompareColumns.Trim(),
                    ServiceCaches = serviceCaches,
                    ExcludeFields = excludeFields,
                    ExcludeXmlElements = excludeXmlElements,
                    XmlColumns = xmlColumns,
                    // Phase 37-03: WHERE clause + runtime-exclude opt-in
                    Where = string.IsNullOrWhiteSpace(Model.WhereClause) ? null : Model.WhereClause.Trim(),
                    IncludeFields = includeFields,
                    // Phase 37-05: SqlTable link-resolution column opt-in (LINK-02).
                    ResolveLinksInColumns = resolveLinksInColumns
                };

                // Phase 37-03: validate identifiers + WHERE clause at save-time. Tests inject
                // fixture validators; production call sites leave both null and we skip here --
                // ConfigLoader.Load on next read will re-validate against the live schema.
                if (IdentifierValidator != null)
                {
                    var whereValidator = WhereValidator ?? new SqlWhereClauseValidator();
                    var validationError = RunSqlTableValidation(predicate, IdentifierValidator, whereValidator);
                    if (validationError != null)
                        return new() { Status = CommandResult.ResultType.Invalid, Message = validationError };
                }
            }

            if (Model.Index < 0)
            {
                // New predicate
                predicates.Add(predicate);
            }
            else if (Model.Index < predicates.Count)
            {
                // Update existing
                predicates[Model.Index] = predicate;
            }
            else
            {
                return new() { Status = CommandResult.ResultType.Error, Message = "Invalid predicate index" };
            }

            // Phase 40 D-01: persist back to the single flat predicate list.
            var updated = config with { Predicates = predicates };
            ConfigWriter.Save(updated, configPath);

            return new() { Status = CommandResult.ResultType.Ok, Model = Model };
        }
        catch (Exception ex)
        {
            return new() { Status = CommandResult.ResultType.Error, Message = ex.Message };
        }
    }

    /// <summary>Trim entries and drop blanks from a SelectMultiDual-bound list.</summary>
    private static List<string> Sanitize(List<string>? values) =>
        (values ?? new List<string>())
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();

    /// <summary>
    /// Parse the string-typed Model.Mode into the SerializerMode enum stored on
    /// ProviderPredicateDefinition. Case-insensitive (matches ConfigLoader's Enum.TryParse
    /// pathway). Throws ArgumentException for unknown values; the early-validation gate at the
    /// top of Handle() prevents that path from being reachable in normal flow.
    /// </summary>
    private static SerializerMode ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Mode must be 'Replace' or 'Merge' (case-insensitive); got empty value.");
        return Enum.Parse<SerializerMode>(raw, ignoreCase: true);
    }

    /// <summary>
    /// Phase 37-03: validate every identifier + Where clause on an SqlTable predicate. Returns
    /// the first error as a user-facing string, or null if everything passes. Validation mirrors
    /// ConfigLoader.ValidateIdentifiers so admin-UI saves and config-file loads have identical
    /// gates.
    /// </summary>
    private static string? RunSqlTableValidation(
        ProviderPredicateDefinition predicate,
        SqlIdentifierValidator idValidator,
        SqlWhereClauseValidator whereValidator)
    {
        try { idValidator.ValidateTable(predicate.Table!); }
        catch (InvalidOperationException ex) { return ex.Message; }

        if (!string.IsNullOrWhiteSpace(predicate.NameColumn))
        {
            try { idValidator.ValidateColumn(predicate.Table!, predicate.NameColumn!); }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        foreach (var col in predicate.ExcludeFields)
        {
            try { idValidator.ValidateColumn(predicate.Table!, col); }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        foreach (var col in predicate.IncludeFields)
        {
            try { idValidator.ValidateColumn(predicate.Table!, col); }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        foreach (var col in predicate.XmlColumns)
        {
            try { idValidator.ValidateColumn(predicate.Table!, col); }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        // Phase 37-05: ResolveLinksInColumns must reference real columns on the table.
        foreach (var col in predicate.ResolveLinksInColumns)
        {
            try { idValidator.ValidateColumn(predicate.Table!, col); }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        if (!string.IsNullOrWhiteSpace(predicate.Where))
        {
            try
            {
                var cols = idValidator.GetColumns(predicate.Table!);
                whereValidator.Validate(predicate.Where!, cols);
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        return null;
    }
}
