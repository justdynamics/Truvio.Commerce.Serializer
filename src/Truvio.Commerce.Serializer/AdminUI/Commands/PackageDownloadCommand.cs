using Dynamicweb.Content;
using Truvio.Commerce.Serializer.AdminUI.Security;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// API form of Download Package with flat parameters: builds the subtree zip for a page and
/// returns it as a file download (a copy lands in the configured Download folder). It does not
/// touch the serialized baseline; for that, call <see cref="SerializeCommand"/> with a scope.
/// The admin UI goes through <see cref="Screens.DownloadPackageScreen"/> +
/// <see cref="DownloadPackageCommand"/>; both route into <see cref="PackageBuilder"/>.
///
/// Use via Management API: POST /Admin/Api/PackageDownload {"PageId":12,"AreaId":1,"Scope":"PageOnly"}
/// </summary>
public class PackageDownloadCommand : CommandBase
{
    public int PageId { get; set; }
    public int AreaId { get; set; }

    /// <summary>Content scope: PageAndSubpages (default), PageOnly, or SubpagesOnly.</summary>
    public string Scope { get; set; } = PackageBuilder.ScopePageAndSubpages;

    /// <summary>Bundle referenced images/files from the Files archive into the package.</summary>
    public bool IncludeAssets { get; set; }

    /// <summary>Route name of a deprecated alias subclass; null on this command.</summary>
    internal virtual string? DeprecatedRoute => null;

    public override CommandResult Handle() =>
        DeprecatedCommandAlias.Decorate(HandleCore(), DeprecatedRoute, "PackageDownload");

    private CommandResult HandleCore()
    {
        if (PageId <= 0)
            return new() { Status = CommandResult.ResultType.Invalid, Message = "PageId is required" };
        if (AreaId <= 0)
            return new() { Status = CommandResult.ResultType.Invalid, Message = "AreaId is required" };

        try
        {
            var page = Services.Pages.GetPage(PageId);
            if (page == null)
                return new() { Status = CommandResult.ResultType.Error, Message = $"Page {PageId} not found" };

            if (!PackageAccess.CanDownload(page))
                return new()
                {
                    Status = CommandResult.ResultType.NotAllowed,
                    Message = "You do not have permission to download packages for this page."
                };

            var filesRoot = DownloadPackageCommand.ResolveFilesRoot();
            var result = PackageBuilder.Build(PageId, AreaId, Scope, IncludeAssets, filesRoot);

            DownloadPackageCommand.CopyToDownloadDir(result.ZipPath, result.ZipFileName);

            var zipStream = new FileStream(result.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Delete);
            return new CommandResult
            {
                Status = CommandResult.ResultType.Ok,
                Model = new FileResult
                {
                    FileStream = zipStream,
                    ContentType = "application/zip",
                    FileDownloadName = result.ZipFileName
                }
            };
        }
        catch (Exception ex)
        {
            return new() { Status = CommandResult.ResultType.Error, Message = $"Serialize failed: {ex.Message}" };
        }
    }
}
