using System.IO.Compression;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Providers.Content;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>A zip that <see cref="PackageUnzipper"/> refuses: bad input, not a server error.</summary>
public sealed class PackageRejectedException(string message) : InvalidOperationException(message);

/// <summary>
/// Validates a zip and unzips it into one SerializeRoot mode folder, replacing that folder, so
/// the next <c>Deserialize</c> of the mode reads it. Two shapes are accepted:
/// <list type="bullet">
/// <item><b>Mode tree</b>: <c>{mode}-manifest.json</c> at the zip root, next to <c>_content/</c>
/// and <c>_sql/</c> (the contents of a <c>SerializeRoot/{mode}/</c> folder). Unzipped as is.</item>
/// <item><b>Content package</b>: <c>&lt;area&gt;/area.yml</c> at the zip root and no manifest
/// (<see cref="PackageBuilder"/> output). Unzipped under <c>_content/</c>, and a whole-area
/// manifest entry for the given area id is written, the same entry shape a full serialize of
/// that area writes.</item>
/// </list>
/// Every entry is checked before anything is written: no absolute paths, no <c>..</c> segments,
/// no NTFS stream names, entry count and uncompressed size within <see cref="Limits"/>. The zip
/// is unzipped into a staging folder and swapped in only when complete, so a rejected or failed
/// zip leaves the existing mode folder untouched.
/// </summary>
public static class PackageUnzipper
{
    public const long DefaultMaxZipBytes = 256L * 1024 * 1024;
    public const long DefaultMaxUnzippedBytes = 1024L * 1024 * 1024;
    public const int DefaultMaxEntries = 100_000;

    public sealed record Limits(long MaxZipBytes, long MaxUnzippedBytes, int MaxEntries)
    {
        public static Limits Default { get; } = new(DefaultMaxZipBytes, DefaultMaxUnzippedBytes, DefaultMaxEntries);
    }

    public enum PackageShape { ModeTree, ContentPackage }

    /// <param name="FileCount">Files unzipped (a generated manifest is not counted).</param>
    /// <param name="Bytes">Uncompressed bytes written.</param>
    /// <param name="TargetPath">The mode folder the zip now occupies.</param>
    /// <param name="AssetCount">Files under a content package's <c>_assets/</c>; not restored into the Files archive.</param>
    public sealed record UnzipResult(PackageShape Shape, int FileCount, long Bytes, string TargetPath, int AssetCount);

    /// <summary>
    /// Unzip <paramref name="zipPath"/> into <paramref name="modeRoot"/>. The staging folder is
    /// created under <paramref name="stagingParent"/>, which must be on the same volume and
    /// outside SerializeRoot (the deserializer reads every folder under SerializeRoot as a mode).
    /// Throws <see cref="PackageRejectedException"/> for a zip that fails validation.
    /// </summary>
    public static UnzipResult Unzip(string zipPath, string modeRoot, string stagingParent,
        SerializerMode mode, int areaId = 0, Limits? limits = null)
    {
        limits ??= Limits.Default;
        var modeName = mode.ToString().ToLowerInvariant();

        var zipBytes = new FileInfo(zipPath).Length;
        if (zipBytes > limits.MaxZipBytes)
            throw new PackageRejectedException(
                $"The zip is {zipBytes} bytes; the limit is {limits.MaxZipBytes} bytes.");

        using var archive = ZipFile.OpenRead(zipPath);

        var files = new List<(ZipArchiveEntry Entry, string Name)>();
        long declaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var name = ValidateEntryName(entry.FullName);
            if (name.EndsWith('/'))
                continue; // directory entry

            files.Add((entry, name));
            if (files.Count > limits.MaxEntries)
                throw new PackageRejectedException(
                    $"The zip holds more than {limits.MaxEntries} files; the limit is {limits.MaxEntries}.");

            declaredBytes += entry.Length;
            if (declaredBytes > limits.MaxUnzippedBytes)
                throw new PackageRejectedException(
                    $"The zip unzips to more than {limits.MaxUnzippedBytes} bytes; the limit is {limits.MaxUnzippedBytes} bytes.");
        }

        var shape = DetectShape(files.Select(f => f.Name).ToList(), modeName, areaId);
        var prefix = shape == PackageShape.ContentPackage ? "_content/" : "";
        var assetsPrefix = PackageBuilder.AssetsFolderName + "/";

        var target = Path.GetFullPath(modeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var staging = Path.Combine(Path.GetFullPath(stagingParent), $".unzip-{Guid.NewGuid():N}");
        var stagingPrefix = staging + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(staging);

        try
        {
            long written = 0;
            var assets = 0;
            foreach (var (entry, name) in files)
            {
                var destPath = Path.GetFullPath(Path.Combine(staging, prefix + name));
                if (!destPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new PackageRejectedException(
                        $"Zip entry '{entry.FullName}' resolves outside the target folder (path traversal); rejected.");

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using (var input = entry.Open())
                using (var output = File.Create(destPath))
                    written += CopyBounded(input, output, limits.MaxUnzippedBytes - written, limits.MaxUnzippedBytes);

                if (shape == PackageShape.ContentPackage && name.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                    assets++;
            }

            if (shape == PackageShape.ContentPackage)
            {
                var contentEntry = ContentProvider.BuildContentEntryForArea(areaId, staging);
                new ManifestWriter().Write(staging, modeName, new[] { contentEntry });
            }

            SwapIn(staging, target);
            return new UnzipResult(shape, files.Count, written, target, assets);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Normalises separators and rejects names that can land outside the target on any
    /// platform: rooted or drive-qualified paths, <c>..</c> segments, and <c>:</c> (NTFS
    /// alternate data streams). The later full-path check is the backstop.
    /// </summary>
    internal static string ValidateEntryName(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':'))
            throw new PackageRejectedException(
                $"Zip entry '{fullName}' is an absolute or drive-qualified path (path traversal); rejected.");

        if (name.Split('/').Any(segment => segment == ".."))
            throw new PackageRejectedException(
                $"Zip entry '{fullName}' contains a '..' segment (path traversal); rejected.");

        return name;
    }

    internal static PackageShape DetectShape(IReadOnlyList<string> names, string modeName, int areaId)
    {
        var expectedManifest = $"{modeName}-manifest.json";

        if (!names.Any(n => n.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            throw new PackageRejectedException("The zip holds no YAML files, so it is not a serialized tree.");

        var rootManifests = names
            .Where(n => !n.Contains('/') && n.EndsWith("-manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rootManifests.Count > 0)
        {
            if (!rootManifests.Contains(expectedManifest, StringComparer.OrdinalIgnoreCase))
                throw new PackageRejectedException(
                    $"The zip is a mode tree with {rootManifests[0]}, not {expectedManifest}. Call PackageUnzip with the Mode that matches the manifest.");
            return PackageShape.ModeTree;
        }

        if (names.Any(n => n.Count(c => c == '/') == 1
                           && !n.StartsWith('_')
                           && n.EndsWith("/area.yml", StringComparison.OrdinalIgnoreCase)))
        {
            if (areaId <= 0)
                throw new PackageRejectedException(
                    "The zip is a content package (PackageDownload output) and carries no manifest. Pass AreaId, the id of the website the content is deserialized into.");
            return PackageShape.ContentPackage;
        }

        if (names.Any(n => n.EndsWith("/" + expectedManifest, StringComparison.OrdinalIgnoreCase)))
            throw new PackageRejectedException(
                $"{expectedManifest} sits in a subfolder of the zip. Zip the contents of the SerializeRoot/{modeName} folder, not the folder itself.");

        if (names.Any(n => n.StartsWith("_content/", StringComparison.OrdinalIgnoreCase)
                           || n.StartsWith("_sql/", StringComparison.OrdinalIgnoreCase)))
            throw new PackageRejectedException(
                $"The zip holds a mode tree without {expectedManifest} at its root, and Deserialize reads that manifest. Serialize the mode again and zip the whole folder.");

        throw new PackageRejectedException(
            $"The zip is not a serialized tree: expected {expectedManifest} at the root (a mode tree) or <area>/area.yml (a PackageDownload content package).");
    }

    /// <summary>
    /// Copies at most <paramref name="remaining"/> bytes. The declared entry sizes were checked
    /// up front; this bounds what the streams actually produce.
    /// </summary>
    private static long CopyBounded(Stream input, Stream output, long remaining, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > remaining)
                throw new PackageRejectedException(
                    $"The zip unzips to more than {limit} bytes; the limit is {limit} bytes.");
            output.Write(buffer, 0, read);
        }
        return total;
    }

    /// <summary>Replace <paramref name="target"/> with <paramref name="staging"/>, restoring the old folder if the move fails.</summary>
    private static void SwapIn(string staging, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!Directory.Exists(target))
        {
            Directory.Move(staging, target);
            return;
        }

        var previous = staging + "-previous";
        Directory.Move(target, previous);
        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            Directory.Move(previous, target);
            throw;
        }
        TryDelete(previous);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
