using Dynamicweb.Content;
using Dynamicweb.Security.Permissions;

namespace Truvio.Commerce.Serializer.AdminUI.Security;

/// <summary>
/// DW unified-permission entity for the package commands (<c>PackageDownload</c> and
/// <c>PackageUnzip</c>). Per DW semantics the functions are open until an admin explicitly
/// manages them; built-in admins are always elevated. The keys are unchanged from the earlier
/// Download/Upload Package functions, so grants stored on a host keep applying.
/// </summary>
public sealed class PackagePermissionEntity : IPermissionEntity
{
    /// <summary>Lookup name — the "entity type" under which DW stores and resolves grants.</summary>
    public const string PermissionName = "Truvio Serializer Packages";

    public const string DownloadKey = "truvio-serializer-package-download";
    public const string UploadKey = "truvio-serializer-package-upload";

    private readonly string _key;

    public PackagePermissionEntity(string key) => _key = key;

    public string GetPermissionKey() => _key;

    public IEnumerable<IPermissionEntity> GetPermissionParents() => Enumerable.Empty<IPermissionEntity>();
}

/// <summary>Resolves stored permission keys back to entities — auto-discovered by DW's
/// AddInManager (see PermissionEntityLookupManager).</summary>
public sealed class PackagePermissionEntityLookup : IPermissionEntityLookup
{
    public string PermissionName => PackagePermissionEntity.PermissionName;

    public IPermissionEntity? GetPermissionEntityByKey(string key) =>
        key is PackagePermissionEntity.DownloadKey or PackagePermissionEntity.UploadKey
            ? new PackagePermissionEntity(key)
            : null;
}

/// <summary>
/// Access checks for the package commands. Checks fail CLOSED — an exception during
/// evaluation denies access.
/// </summary>
public static class PackageAccess
{
    /// <summary>Download: the function grant plus Read on the page being exported.</summary>
    public static bool CanDownload(Page? page)
    {
        if (page is null)
            return false;
        try
        {
            return new PackagePermissionEntity(PackagePermissionEntity.DownloadKey)
                       .GetPermission().HasPermission(PermissionLevel.Read)
                   && page.GetPermission().HasPermission(PermissionLevel.Read);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unzip (<see cref="Commands.PackageUnzipCommand"/>): there is no target area — the zip
    /// expands into the engine-owned <c>Files/System/Serializer/SerializeRoot/&lt;mode&gt;/</c>
    /// path — so this gates on the upload function grant only.
    /// </summary>
    public static bool CanUnzip()
    {
        try
        {
            return new PackagePermissionEntity(PackagePermissionEntity.UploadKey)
                       .GetPermission().HasPermission(PermissionLevel.Read);
        }
        catch
        {
            return false;
        }
    }
}
