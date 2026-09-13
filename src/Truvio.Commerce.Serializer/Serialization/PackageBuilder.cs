using System.IO.Compression;
using System.Text.RegularExpressions;
using Dynamicweb.Content;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Builds an ad-hoc content package zip (<c>PackageDownload</c>): serializes a page subtree
/// through the regular <see cref="ContentSerializer"/> (lenient link sweep — references out
/// of the exported subtree resolve against the target DB at import time), applies the
/// requested scope, optionally bundles referenced assets, and zips the result.
/// </summary>
public static class PackageBuilder
{
    public const string ScopePageAndSubpages = "PageAndSubpages";
    public const string ScopePageOnly = "PageOnly";
    public const string ScopeSubpagesOnly = "SubpagesOnly";

    /// <summary>Folder inside the package that carries bundled assets, mirrored relative to
    /// the Files root. Tree reading ignores it (no area.yml); <see cref="PackageUnzipper"/>
    /// unzips it with the content and reports it as not restored.</summary>
    public const string AssetsFolderName = "_assets";

    /// <summary>
    /// Matches /Files/... references inside YAML text. Conservative character class —
    /// stops at quotes, whitespace, escapes, and markup delimiters; trailing punctuation
    /// is trimmed afterwards.
    /// </summary>
    private static readonly Regex _filesReferencePattern = new(
        @"/Files/[^""'\s\\<>|?*&#;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record PackageResult(string ZipPath, string ZipFileName);

    /// <summary>
    /// Serialize the subtree rooted at <paramref name="pageId"/> and zip it. Throws on
    /// failure; the caller maps exceptions to a command error.
    /// </summary>
    public static PackageResult Build(int pageId, int areaId, string scope, bool includeAssets, string? filesRoot)
    {
        var page = Services.Pages.GetPage(pageId)
            ?? throw new InvalidOperationException($"Page {pageId} not found");

        var pageName = page.MenuText ?? $"Page{pageId}";
        var contentPath = ContentPathBuilder.BuildContentPath(page);

        var tempDir = Path.Combine(Path.GetTempPath(), "Serializer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tempConfig = new SerializerConfiguration
            {
                OutputDirectory = tempDir,
                Predicates = new List<ProviderPredicateDefinition>
                {
                    new()
                    {
                        Name = "ad-hoc-serialize",
                        Mode = SerializerMode.Replace,
                        ProviderType = "Content",
                        Path = contentPath,
                        AreaId = areaId,
                        Excludes = new List<string>()
                    }
                }
            };

            var serializer = new ContentSerializer(tempConfig, lenientLinkSweep: true);
            serializer.Serialize();

            ApplyScope(tempDir, pageId, scope);

            if (includeAssets && !string.IsNullOrEmpty(filesRoot))
                BundleReferencedAssets(tempDir, filesRoot);

            var zipFileName = $"Serializer_{SanitizeFileName(pageName)}_{DateTime.Now:yyyy-MM-dd}.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), "Serializer", zipFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            var fileCount = Directory.GetFiles(tempDir, "*.yml", SearchOption.AllDirectories).Length;
            var logContent = $"Serializer Export\n" +
                             $"Page: {pageName} (ID={pageId})\n" +
                             $"Area: {areaId}\n" +
                             $"Content Path: {contentPath}\n" +
                             $"Scope: {scope}\n" +
                             $"Assets included: {includeAssets}\n" +
                             $"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                             $"Files: {fileCount} YAML files\n";
            File.WriteAllText(Path.Combine(tempDir, "export.log"), logContent);

            ZipFile.CreateFromDirectory(tempDir, zipPath);
            return new PackageResult(zipPath, zipFileName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // -------------------------------------------------------------------------
    // Scope
    // -------------------------------------------------------------------------

    /// <summary>
    /// Post-serialize tree surgery for the scope choice. The serializer always emits the
    /// full subtree; "Only this page" drops the child page directories, "Only subpages"
    /// downgrades the root page to a structural stub (created if missing on upload, fields
    /// never overwritten — the existing ancestor-stub mechanism) and drops its grid rows.
    /// </summary>
    internal static void ApplyScope(string tempDir, int rootPageId, string scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Equals(ScopePageAndSubpages, StringComparison.OrdinalIgnoreCase))
            return;

        var rootPageDir = FindPageDirBySourceId(tempDir, rootPageId)
            ?? throw new InvalidOperationException($"Exported tree does not contain page {rootPageId}.");

        if (scope.Equals(ScopePageOnly, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dir in Directory.GetDirectories(rootPageDir))
            {
                if (File.Exists(Path.Combine(dir, "page.yml")))
                    Directory.Delete(dir, recursive: true);
            }
            return;
        }

        if (scope.Equals(ScopeSubpagesOnly, StringComparison.OrdinalIgnoreCase))
        {
            var pageYml = Path.Combine(rootPageDir, "page.yml");
            var text = File.ReadAllText(pageYml);
            File.WriteAllText(pageYml, text.Replace("\"isStructuralStub\": false", "\"isStructuralStub\": true"));

            foreach (var dir in Directory.GetDirectories(rootPageDir))
            {
                if (File.Exists(Path.Combine(dir, "grid-row.yml")))
                    Directory.Delete(dir, recursive: true);
            }
            return;
        }

        throw new InvalidOperationException(
            $"Unknown scope '{scope}'. Expected {ScopePageAndSubpages}, {ScopePageOnly} or {ScopeSubpagesOnly}.");
    }

    private static string? FindPageDirBySourceId(string root, int pageId)
    {
        var marker = $"\"sourcePageId\": {pageId}";
        foreach (var pageYml in Directory.EnumerateFiles(root, "page.yml", SearchOption.AllDirectories))
        {
            if (File.ReadLines(pageYml).Any(l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal)))
                return Path.GetDirectoryName(pageYml);
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Assets
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scan the exported YAML for /Files/ references and copy the referenced files into
    /// the package under <see cref="AssetsFolderName"/> (path mirrored relative to the
    /// Files root). Templates and System are deliberately excluded — layouts and item
    /// types ship with the design, and the manifest pre-flight verifies them on upload.
    /// Returns the number of files bundled.
    /// </summary>
    internal static int BundleReferencedAssets(string tempDir, string filesRoot)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var yml in Directory.EnumerateFiles(tempDir, "*.yml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(yml);
            foreach (Match m in _filesReferencePattern.Matches(text))
                references.Add(m.Value.TrimEnd('.', ',', ')', ']', '}'));
        }

        var copied = 0;
        foreach (var reference in references)
        {
            var relative = reference["/Files/".Length..].Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0 || relative.Contains(".."))
                continue;

            var firstSegment = relative.Split(Path.DirectorySeparatorChar)[0];
            if (firstSegment.Equals("Templates", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("System", StringComparison.OrdinalIgnoreCase))
                continue;

            var sourcePath = Path.Combine(filesRoot, relative);
            if (!File.Exists(sourcePath))
                continue;

            var destPath = Path.Combine(tempDir, AssetsFolderName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(sourcePath, destPath, overwrite: true);
            copied++;
        }
        return copied;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
