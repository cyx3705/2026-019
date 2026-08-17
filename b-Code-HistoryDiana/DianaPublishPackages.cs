using System.IO;
using System.Text.Json;

namespace HistoryDiana;

internal static class DianaPublishPackages
{
    internal const string PublishDirectoryName = "z-Publish";
    internal const string HistoryDirectoryName = "history";

    internal static string PackageDirectoryName(string name, string version)
        => $"{name}-v{version}";

    internal static string ResolveCurrent(string projectRoot, string expectedName)
    {
        var publishRoot = Path.Combine(projectRoot, PublishDirectoryName);
        var packages = EnumerateCurrent(publishRoot)
            .Where(package => package.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return packages.Count switch
        {
            1 => packages[0].Path,
            0 => throw new DirectoryNotFoundException(
                $"{publishRoot} 中没有 {expectedName}-v<版本> 候选包。"),
            _ => throw new InvalidOperationException(
                $"{publishRoot} 中存在多个 {expectedName} 候选包，发布区必须只保留一个当前候选。"),
        };
    }

    internal static string ResolveHostSnapshot(string vulcanProjectRoot)
    {
        try
        {
            return ResolveCurrent(vulcanProjectRoot, "HistoryVulcan");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            var publishRoot = Path.Combine(vulcanProjectRoot, PublishDirectoryName);
            var hostExe = Path.Combine(publishRoot, "host", "HistoryVulcan.exe");
            if (File.Exists(hostExe) && File.Exists(Path.Combine(publishRoot, "SHA256SUMS")))
                return Path.GetFullPath(publishRoot);
            throw;
        }
    }

    internal static IReadOnlyList<PublishedPackage> EnumerateCurrent(string publishRoot)
    {
        if (!Directory.Exists(publishRoot))
            return [];

        var result = new List<PublishedPackage>();
        foreach (var directory in Directory.GetDirectories(publishRoot, "History*-v*", SearchOption.TopDirectoryOnly))
        {
            if (TryRead(directory, out var package))
                result.Add(package);
        }

        return result
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TryRead(string path, out PublishedPackage package)
    {
        package = default!;
        var moduleManifest = Path.Combine(path, "module.manifest.json");
        var hostManifest = Path.Combine(path, "manifest.json");
        var manifest = File.Exists(moduleManifest)
            ? moduleManifest
            : File.Exists(hostManifest) ? hostManifest : null;
        if (manifest == null || !File.Exists(Path.Combine(path, "SHA256SUMS")))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var identityProperty = manifest.Equals(moduleManifest, StringComparison.OrdinalIgnoreCase)
                ? "name"
                : "product";
            if (!document.RootElement.TryGetProperty(identityProperty, out var identity)
                || !document.RootElement.TryGetProperty("version", out var versionElement)
                || identity.GetString() is not { Length: > 0 } name
                || versionElement.GetString() is not { Length: > 0 } version)
            {
                return false;
            }

            var expectedDirectory = PackageDirectoryName(name, version);
            if (!Path.GetFileName(path).Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            package = new PublishedPackage(name, version, Path.GetFullPath(path), manifest);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal sealed record PublishedPackage(string Name, string Version, string Path, string ManifestPath);
}
