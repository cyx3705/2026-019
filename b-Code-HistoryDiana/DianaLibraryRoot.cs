using System.IO;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>
/// 项目库根：一项目一仓的 HistoryClio。旧 Vesta 配置改写到 Clio。
/// </summary>
internal static class DianaLibraryRoot
{
    public const string Default = @"C:\OneHistory\HistoryClio";
    public const string LegacyVesta = @"C:\OneHistory\HistoryVesta";
    public const string KeyLibraryRoot = "proj.libraryroot";
    public const string KeyWorktreeRoot = "proj.worktreeroot";

    public static string Resolve(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = settings.Get(KeyLibraryRoot);
        if (!string.IsNullOrWhiteSpace(current))
            return Coerce(current);
        var legacy = settings.Get(KeyWorktreeRoot);
        if (!string.IsNullOrWhiteSpace(legacy))
            return Coerce(legacy);
        return Default;
    }

    public static string Coerce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(LegacyVesta, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(full, "HistoryVesta.git")))
        {
            if (Directory.Exists(Default))
                return Default;
        }

        return full;
    }

    public static bool IsGitProject(string projectPath)
    {
        var git = Path.Combine(projectPath, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
