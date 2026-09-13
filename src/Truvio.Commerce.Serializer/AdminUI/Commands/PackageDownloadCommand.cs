using Dynamicweb.Content;
using Truvio.Commerce.Serializer.AdminUI.Security;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Builds the subtree zip for a page through <see cref="PackageBuilder"/> and returns it as a
/// file download (a copy lands in the configured Download folder). It does not touch the
/// serialized baseline; for that, call <see cref="SerializeCommand"/> with a scope. The zip goes
/// back onto a host with <see cref="PackageUnzipCommand"/> followed by <c>Deserialize</c>.
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

            var filesRoot = ResolveFilesRoot();
            var result = PackageBuilder.Build(PageId, AreaId, Scope, IncludeAssets, filesRoot);

            CopyToDownloadDir(result.ZipPath, result.ZipFileName);

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

    private static string? ResolveFilesRoot()
    {
        try
        {
            var configPath = ConfigPathResolver.FindOrCreateConfigFile();
            return ConfigPathResolver.GetFilesRoot(configPath);
        }
        catch
        {
            return ConfigPathResolver.TryGetDwFilesRoot();
        }
    }

    private static void CopyToDownloadDir(string zipPath, string zipFileName)
    {
        try
        {
            var configPath = ConfigPathResolver.FindOrCreateConfigFile();
            var config = ConfigLoader.Load(configPath);

            var filesDir = ConfigPathResolver.GetFilesRoot(configPath);
            var systemDir = Path.Combine(filesDir, "System");
            var paths = config.EnsureDirectories(systemDir);

            var destPath = Path.Combine(paths.Download, zipFileName);
            File.Copy(zipPath, destPath, overwrite: true);
        }
        catch
        {
            // Download copy is best-effort — don't fail the file response
        }
    }
}
