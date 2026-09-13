namespace Truvio.Commerce.Serializer.AdminUI.Models;

/// <summary>
/// Result payload for <see cref="Commands.PackageUnzipCommand"/>: what was unzipped and where.
/// </summary>
public sealed class PackageUnzipResultModel
{
    public string Mode { get; set; } = "";

    /// <summary>"ModeTree" or "ContentPackage".</summary>
    public string Shape { get; set; } = "";

    public int FileCount { get; set; }
    public long Bytes { get; set; }
    public string TargetPath { get; set; } = "";

    /// <summary>Bundled asset files unzipped with a content package; not restored into the Files archive.</summary>
    public int AssetCount { get; set; }
}
