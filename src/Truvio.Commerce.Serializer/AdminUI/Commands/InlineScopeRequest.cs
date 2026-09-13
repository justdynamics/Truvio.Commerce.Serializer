using System.Text.Json;
using Dynamicweb.Content;
using Dynamicweb.CoreUI.Data;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>Host-side plumbing for the inline <c>scope</c> parameter of <see cref="SerializeCommand"/> and <see cref="DeserializeCommand"/>.</summary>
internal static class InlineScopeRequest
{
    /// <summary>
    /// Returns the scope from the JSON body, or from the <c>?scope=</c> query string (JSON) when the
    /// body carried none (D-38-11 precedent: POST query parameters are not bound). A malformed query
    /// value yields an Invalid result.
    /// </summary>
    public static (InlineScope? Scope, CommandResult? Invalid) Read(InlineScope? fromBody)
    {
        if (fromBody is not null)
            return (fromBody, null);

        var json = Dynamicweb.Context.Current?.Request?["scope"];
        if (string.IsNullOrWhiteSpace(json))
            return (null, null);

        try
        {
            return (InlineScope.Parse(json), null);
        }
        catch (JsonException ex)
        {
            return (null, new CommandResult
            {
                Status = CommandResult.ResultType.Invalid,
                Message = $"Invalid scope: the 'scope' query parameter is not valid JSON ({ex.Message})."
            });
        }
    }

    public static CommandResult Rejected(InlineScope scope, InlineScopeResolution resolution) => new()
    {
        Status = CommandResult.ResultType.Invalid,
        Message = $"Inline scope rejected ({scope}): {string.Join(" ", resolution.Errors)}"
    };

    /// <summary>
    /// Resolves a scope page id to (area id, content path) in predicate space: a language-layer
    /// page resolves to its master page, because predicates are authored against the master area.
    /// </summary>
    public static (int AreaId, string Path)? ResolvePage(int pageId)
    {
        try
        {
            var page = Services.Pages.GetPage(pageId);
            if (page is null)
                return null;
            if (page.MasterPageId > 0 && Services.Pages.GetPage(page.MasterPageId) is { } master)
                page = master;
            return (page.AreaId, ContentPathBuilder.BuildContentPath(page));
        }
        catch
        {
            return null;
        }
    }

    public static SqlIdentifierValidator IdentifierValidator() =>
        ConfigLoader.TestOverrideIdentifierValidator ?? new SqlIdentifierValidator();
}
