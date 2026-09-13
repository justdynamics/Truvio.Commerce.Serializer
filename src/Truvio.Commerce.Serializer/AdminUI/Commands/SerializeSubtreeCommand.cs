namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Deprecated name of <see cref="PackageDownloadCommand"/>, kept through the 1.0 beta so
/// <c>POST /Admin/Api/SerializeSubtree</c> keeps working. It never serialized into the baseline:
/// it builds the Download Package zip. Same parameters and zip response. A subtree serialize into
/// the baseline is <c>Serialize</c> with an inline <c>scope</c>. Removed in the 1.0.0 release.
/// </summary>
[Obsolete("Renamed to PackageDownloadCommand (API route 'PackageDownload'). For a subtree serialize into the baseline use SerializeCommand with a Scope. This alias is removed in the 1.0.0 release.")]
public sealed class SerializeSubtreeCommand : PackageDownloadCommand
{
    internal override string? DeprecatedRoute => "SerializeSubtree";
}
