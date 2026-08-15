using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>Registers the HistoryDiana read-only worktree inspection commands.</summary>
public sealed class HistoryDianaCommands : IModuleContextAware
{
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vs", ".pio", "bin", "obj", "TestResults", "node_modules", "Library", "Temp", "Logs", "UserSettings",
    };

    private ISettingsService? _settings;
    private IModuleContext? _context;

    /// <summary>Attaches the host-owned settings store and stages all module commands.</summary>
    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_settings != null)
            throw new InvalidOperationException("HistoryDiana 命令已附着到宿主上下文。");

        _settings = context.Settings;
        _context = context;
        context.RegisterCommands(RegisterCommands);
    }

    private void RegisterCommands(CommandRegistry registry)
    {
        var settings = _settings
            ?? throw new InvalidOperationException("HistoryDiana 尚未附着到 HistoryVulcan 宿主上下文。");
        var context = _context
            ?? throw new InvalidOperationException("HistoryDiana 尚未附着到 HistoryVulcan 宿主上下文。");
        // 工具箱的另外两类：kit（哈希/编码/标识/时间）与 relay（MCP 工具中继）。
        // 按类分文件，但注册入口只有这一处。docs 通道按现场 z-* 扫描登记。
        DianaKitCommands.Register(registry);
        DianaRelayCommands.Register(registry);
        DianaProjectAlignmentCommands.Register(registry, name => ResolveProject(name, out _));
        DianaZDocCommands.Register(registry, settings);
        // 候选模块试用：只进内存、不做发现、只供调用，见 DianaTrialCommands 的类型注释。
        DianaTrialCommands.Register(registry, context);
        // 发布管线的命令面：分离子进程 + 日志追踪，见 DianaReleaseCommands 的类型注释。
        DianaReleaseCommands.Register(registry, context);
        // AI 工作区：位置可配，默认 F:i工作区，见 DianaWorktreeCommands。
        DianaWorktreeCommands.Register(registry, context);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.summary",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "汇总一个已登记工作树的文件、体积和一级目录热点",
            Example = "diana.project.summary name=2026-020-HistoryJanus top=10",
            Parameters =
            [
                Text("name", "已登记工作树名称，例如 2026-020-HistoryJanus", required: true, position: 0),
                Bool("includeGenerated", "是否包含 bin、obj、.vs、node_modules 等生成目录", "false"),
                Int("top", "格式和一级目录统计最多返回多少项，范围 1~50", "10"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var result = Summary(
                    context.RequireString("name"),
                    context.GetBool("includeGenerated"),
                    context.GetInt("top", 10));
                return CommandResult.Ok($"已汇总 {result.Project}: {result.Files} 个文件，{result.Size}", result);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.recent",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "列出一个已登记工作树最近修改的文件",
            Example = "diana.project.recent name=2026-020-HistoryJanus days=7 limit=30",
            Parameters =
            [
                Text("name", "已登记工作树名称，例如 2026-020-HistoryJanus", required: true, position: 0),
                Int("days", "回看天数，范围 1~3650", "7"),
                Int("limit", "最多返回文件数，范围 1~200", "30"),
                Bool("includeGenerated", "是否包含 bin、obj、.vs、node_modules 等生成目录", "false"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var result = Recent(
                    context.RequireString("name"),
                    context.GetInt("days", 7),
                    context.GetInt("limit", 30),
                    context.GetBool("includeGenerated"));
                return CommandResult.Ok($"已找到 {result.Count} 个最近修改文件", result);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.largest",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "列出一个已登记工作树中最大的文件",
            Example = "diana.project.largest name=2026-020-HistoryJanus limit=20 minMb=1",
            Parameters =
            [
                Text("name", "已登记工作树名称，例如 2026-020-HistoryJanus", required: true, position: 0),
                Int("limit", "最多返回文件数，范围 1~200", "20"),
                Double("minMb", "最小体积 MB，范围 0~1048576", "1"),
                Bool("includeGenerated", "是否包含 bin、obj、.vs、node_modules 等生成目录", "false"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var result = Largest(
                    context.RequireString("name"),
                    context.GetInt("limit", 20),
                    context.GetDouble("minMb", 1),
                    context.GetBool("includeGenerated"));
                return CommandResult.Ok($"已找到 {result.Count} 个大文件", result);
            }),
        });
    }

    private SummaryResult Summary(string name, bool includeGenerated, int top)
    {
        RequireRange(top, 1, 50, nameof(top));
        var scan = Scan(name, includeGenerated);

        var extensions = scan.Files
            .GroupBy(file => file.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SizeGroup(group.Key, group.Count(), group.Sum(file => file.Length),
                FormatBytes(group.Sum(file => file.Length))))
            .OrderByDescending(group => group.Bytes)
            .ThenByDescending(group => group.Files)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var directories = scan.Files
            .GroupBy(file => file.TopDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SizeGroup(group.Key, group.Count(), group.Sum(file => file.Length),
                FormatBytes(group.Sum(file => file.Length))))
            .OrderByDescending(group => group.Bytes)
            .ThenByDescending(group => group.Files)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var totalBytes = scan.Files.Sum(file => file.Length);
        return new SummaryResult(
            scan.ProjectName,
            scan.ProjectPath,
            scan.Files.Count,
            totalBytes,
            FormatBytes(totalBytes),
            scan.Files.Count == 0
                ? null
                : scan.Files.Max(file => file.LastWriteUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            scan.SkippedDirectories,
            scan.UnreadableEntries,
            extensions,
            directories);
    }

    private RecentResult Recent(string name, int days, int limit, bool includeGenerated)
    {
        RequireRange(days, 1, 3650, nameof(days));
        RequireRange(limit, 1, 200, nameof(limit));
        var scan = Scan(name, includeGenerated);
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var files = scan.Files
            .Where(file => file.LastWriteUtc >= cutoff)
            .OrderByDescending(file => file.LastWriteUtc)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(file => new FileResult(file.RelativePath, file.Length, FormatBytes(file.Length),
                file.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")))
            .ToList();

        return new RecentResult(
            scan.ProjectName,
            cutoff.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            files.Count,
            files,
            scan.SkippedDirectories,
            scan.UnreadableEntries);
    }

    private LargestResult Largest(string name, int limit, double minMb, bool includeGenerated)
    {
        RequireRange(limit, 1, 200, nameof(limit));
        if (double.IsNaN(minMb) || double.IsInfinity(minMb) || minMb < 0 || minMb > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(minMb), "minMb 必须在 0~1048576 之间");

        var scan = Scan(name, includeGenerated);
        var threshold = (long)(minMb * 1024 * 1024);
        var files = scan.Files
            .Where(file => file.Length >= threshold)
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(file => new FileResult(file.RelativePath, file.Length, FormatBytes(file.Length),
                file.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")))
            .ToList();

        return new LargestResult(
            scan.ProjectName,
            minMb,
            files.Count,
            files,
            scan.SkippedDirectories,
            scan.UnreadableEntries);
    }

    private ScanResult Scan(string name, bool includeGenerated)
    {
        var projectPath = ResolveProject(name, out var projectName);
        var files = new List<FileSnapshot>();
        var directories = new Stack<string>();
        directories.Push(projectPath);
        var skippedDirectories = 0;
        var unreadableEntries = 0;

        while (directories.Count > 0)
        {
            var current = directories.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current).ToArray();
                childFiles = Directory.EnumerateFiles(current).ToArray();
            }
            catch (IOException)
            {
                unreadableEntries++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                unreadableEntries++;
                continue;
            }

            foreach (var directory in childDirectories)
            {
                var directoryName = Path.GetFileName(directory);
                if (directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || (!includeGenerated && GeneratedDirectories.Contains(directoryName)))
                {
                    skippedDirectories++;
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedDirectories++;
                        continue;
                    }
                }
                catch (IOException)
                {
                    unreadableEntries++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    unreadableEntries++;
                    continue;
                }

                directories.Push(directory);
            }

            foreach (var filePath in childFiles)
            {
                if (Path.GetFileName(filePath).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(filePath);
                    var relative = Path.GetRelativePath(projectPath, filePath);
                    var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                    var topDirectory = separator < 0 ? "(root)" : relative[..separator];
                    var extension = string.IsNullOrEmpty(info.Extension)
                        ? "(none)"
                        : info.Extension.ToLowerInvariant();
                    files.Add(new FileSnapshot(relative, topDirectory, extension, info.Length, info.LastWriteTimeUtc));
                }
                catch (IOException)
                {
                    unreadableEntries++;
                }
                catch (UnauthorizedAccessException)
                {
                    unreadableEntries++;
                }
            }
        }

        return new ScanResult(projectName, projectPath, files, skippedDirectories, unreadableEntries);
    }

    private string ResolveProject(string name, out string projectName)
    {
        projectName = (name ?? "").Trim();
        if (projectName.Length == 0
            || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || projectName.Contains(Path.DirectorySeparatorChar)
            || projectName.Contains(Path.AltDirectorySeparatorChar)
            || projectName is "." or "..")
        {
            throw new ArgumentException("name 必须是已登记工作树的单一目录名", nameof(name));
        }

        var settings = _settings
            ?? throw new InvalidOperationException("HistoryDiana 尚未附着到 HistoryVulcan 宿主上下文。");
        var rootValue = DianaLibraryRoot.Resolve(settings);
        if (!Directory.Exists(rootValue))
            throw new InvalidOperationException("HistoryVulcan 设置缺少有效的 proj.libraryroot；请先配置 HistoryJanus 项目库。");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
        var candidate = Path.GetFullPath(Path.Combine(root, projectName));
        var parent = Directory.GetParent(candidate)?.FullName;
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标越出 HistoryClio 项目库根，已拒绝");
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException($"项目不存在: {projectName}");
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"项目根是重解析点，已拒绝: {projectName}");
        if (!DianaLibraryRoot.IsGitProject(candidate))
            throw new InvalidOperationException($"目录不是独立 Git 仓库: {projectName}");

        return candidate;
    }

    private static void RequireRange(int value, int minimum, int maximum, string parameter)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameter, $"{parameter} 必须在 {minimum}~{maximum} 之间");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null) => new()
    {
        Name = name,
        Description = description,
        Required = required,
        Position = position,
    };

    private static ParameterSpec Int(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Int,
        Default = defaultValue,
    };

    private static ParameterSpec Double(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Double,
        Default = defaultValue,
    };

    private static ParameterSpec Bool(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Bool,
        Default = defaultValue,
    };

    private sealed record FileSnapshot(
        string RelativePath,
        string TopDirectory,
        string Extension,
        long Length,
        DateTime LastWriteUtc);

    private sealed record ScanResult(
        string ProjectName,
        string ProjectPath,
        List<FileSnapshot> Files,
        int SkippedDirectories,
        int UnreadableEntries);

    private sealed record SizeGroup(string Name, int Files, long Bytes, string Size);

    private sealed record FileResult(string Path, long Bytes, string Size, string Modified);

    private sealed record SummaryResult(
        string Project,
        string ProjectPath,
        int Files,
        long Bytes,
        string Size,
        string? LatestWrite,
        int SkippedDirectories,
        int UnreadableEntries,
        IReadOnlyList<SizeGroup> Extensions,
        IReadOnlyList<SizeGroup> Directories);

    private sealed record RecentResult(
        string Project,
        string Since,
        int Count,
        IReadOnlyList<FileResult> Files,
        int SkippedDirectories,
        int UnreadableEntries);

    private sealed record LargestResult(
        string Project,
        double MinimumMb,
        int Count,
        IReadOnlyList<FileResult> Files,
        int SkippedDirectories,
        int UnreadableEntries);
}
