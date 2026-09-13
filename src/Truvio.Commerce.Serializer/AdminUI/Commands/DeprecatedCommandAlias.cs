using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Behaviour shared by the deprecated command names kept through the 1.0 beta
/// (SerializerSerialize, SerializerDeserialize, SerializeSubtree). An alias subclasses the
/// canonical command and changes nothing about binding, parameters, status or the work done; a
/// message response gets <see cref="Notice"/> appended. File responses (a package zip) are
/// returned unchanged.
/// </summary>
internal static class DeprecatedCommandAlias
{
    public const string RemovedIn = "1.0.0";

    public static string Notice(string alias, string canonical) =>
        $"Deprecated: '{alias}' is an alias of '{canonical}' and is removed in the {RemovedIn} release. Call '{canonical}' instead.";

    public static CommandResult Decorate(CommandResult result, string? alias, string canonical)
    {
        if (alias is null || result.Model is FileResult)
            return result;

        var notice = Notice(alias, canonical);
        return new CommandResult
        {
            Status = result.Status,
            Model = result.Model,
            Message = string.IsNullOrEmpty(result.Message) ? notice : $"{result.Message} {notice}"
        };
    }
}
