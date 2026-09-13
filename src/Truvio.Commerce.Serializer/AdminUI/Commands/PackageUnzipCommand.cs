using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.AdminUI.Security;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Unzips a serialized tree that is already on the host into <c>SerializeRoot/{mode}/</c>,
/// replacing that folder, so the next <see cref="DeserializeCommand"/> of the mode reads it.
/// The command uploads nothing: the zip gets onto the host through the standard file upload
/// (<c>POST /Admin/Api/Upload</c>). Accepts a mode tree zip or a <see cref="PackageDownloadCommand"/>
/// zip; validation, limits and the staged swap are in <see cref="PackageUnzipper"/>.
///
/// Use via Management API:
///   POST /Admin/Api/PackageUnzip {"FilePath":"/Files/System/Serializer/Upload/layer.zip","Mode":"replace"}
///   POST /Admin/Api/PackageUnzip {"FilePath":"Serializer_Home_2026-09-13.zip","Mode":"replace","AreaId":1}
/// </summary>
public class PackageUnzipCommand : CommandBase
{
    /// <summary>Serializer mode folder to replace: "replace" (default) or "merge". Case-insensitive.</summary>
    public string Mode { get; set; } = "replace";

    /// <summary>
    /// /Files-rooted path of the zip. A bare file name resolves to
    /// <c>/Files/System/Serializer/Upload/</c>. The path must stay inside the Files folder.
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary>Website id for a content package zip (it carries no manifest). Ignored for a mode tree zip.</summary>
    public int AreaId { get; set; }

    public override CommandResult Handle()
    {
        if (!Enum.TryParse<SerializerMode>(Mode?.Trim(), ignoreCase: true, out var serializerMode))
            return new()
            {
                Status = CommandResult.ResultType.Invalid,
                Message = $"Invalid mode '{Mode}'. Expected 'replace' or 'merge' (case-insensitive)."
            };

        if (string.IsNullOrWhiteSpace(FilePath))
            return new()
            {
                Status = CommandResult.ResultType.Invalid,
                Message = "FilePath is required: the /Files path of a zip uploaded through /Admin/Api/Upload."
            };

        if (!PackageAccess.CanUnzip())
            return new()
            {
                Status = CommandResult.ResultType.NotAllowed,
                Message = "You do not have permission to unzip packages."
            };

        try
        {
            var configPath = ConfigPathResolver.FindConfigFile();
            if (configPath == null)
                return new() { Status = CommandResult.ResultType.Error, Message = "Serializer.config.json not found" };

            var filesRoot = Path.GetFullPath(ConfigPathResolver.GetFilesRoot(configPath));
            var paths = SerializerPathResolver.EnsureDirectories(Path.Combine(filesRoot, "System"));

            var virtualPath = ResolveVirtualPath(FilePath);
            var physicalPath = Path.GetFullPath(Dynamicweb.Core.SystemInformation.MapPath(virtualPath));
            if (!IsInside(physicalPath, filesRoot))
                return new() { Status = CommandResult.ResultType.Invalid, Message = $"FilePath '{FilePath}' resolves outside the Files folder." };
            if (!File.Exists(physicalPath))
                return new() { Status = CommandResult.ResultType.Error, Message = $"Zip file not found: {virtualPath}" };

            var modeName = serializerMode.ToString().ToLowerInvariant();
            var modeRoot = Path.Combine(paths.SerializeRoot, modeName);

            // Staging sits in the Serializer folder, outside SerializeRoot, where the
            // deserializer would read it as a sibling mode.
            var result = PackageUnzipper.Unzip(physicalPath, modeRoot, paths.Root, serializerMode, AreaId);

            var message = $"[{serializerMode}] Unzipped {result.FileCount} file(s), {result.Bytes} bytes, from {virtualPath} " +
                          $"into SerializeRoot/{modeName} ({DescribeShape(result.Shape)}). Run Deserialize (Mode={modeName}) to apply.";
            if (result.AssetCount > 0)
                message += $" {result.AssetCount} bundled asset file(s) under _content/{PackageBuilder.AssetsFolderName} were not restored into the Files archive.";

            return new CommandResult
            {
                Status = CommandResult.ResultType.Ok,
                Message = message,
                Model = new PackageUnzipResultModel
                {
                    Mode = modeName,
                    Shape = result.Shape.ToString(),
                    FileCount = result.FileCount,
                    Bytes = result.Bytes,
                    TargetPath = result.TargetPath,
                    AssetCount = result.AssetCount
                }
            };
        }
        catch (PackageRejectedException ex)
        {
            return new() { Status = CommandResult.ResultType.Invalid, Message = ex.Message };
        }
        catch (Exception ex)
        {
            return new() { Status = CommandResult.ResultType.Error, Message = $"PackageUnzip failed: {ex.Message}" };
        }
    }

    internal static string ResolveVirtualPath(string filePath)
    {
        var trimmed = filePath.Trim().Replace('\\', '/');
        return trimmed.StartsWith('/')
            ? trimmed
            : $"/Files/System/Serializer/Upload/{Path.GetFileName(trimmed)}";
    }

    internal static bool IsInside(string physicalPath, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeShape(PackageUnzipper.PackageShape shape) =>
        shape == PackageUnzipper.PackageShape.ContentPackage
            ? $"content package for area {AreaId}, manifest written"
            : "mode tree";
}
