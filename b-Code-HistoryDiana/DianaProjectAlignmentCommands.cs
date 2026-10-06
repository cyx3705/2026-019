using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>只读查看已登记项目的 manifest、现行文档和对齐结果。文档读 manifest 里声明的路径，不写死四份合同。</summary>
internal static class DianaProjectAlignmentCommands
{
    private const int MaximumDocumentBytes = 512 * 1024;

    public static void Register(ICommandRegistrar registry, Func<string, string> resolveProject, Func<IEnumerable<string>> projectNames)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolveProject);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.manifest",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "读取已登记项目的身份、版本、活动目录和验证命令",
            Example = "diana.project.manifest name=2026-020-HistoryJanus",
            Readonly = true,
            Parameters = [Text("name", "已登记工作树名称", required: true, position: 0)],
            Handler = CommandDescriptor.Sync(context => DianaCommandGuard.Run(() =>
            {
                var project = ReadManifest(resolveProject(context.RequireString("name")));
                return CommandResult.Ok(project.Name, project);
            })),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.docs",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "维护已授权项目时按 manifest documents 读取现行文档；查别的模块怎么调用用 diana.docs.catalog/read",
            Example = "diana.project.docs name=2026-021-HistoryMercury kind=conventions",
            Readonly = true,
            Parameters =
            [
                Text("name", "已登记工作树名称，例如 2026-021-HistoryMercury", required: true, position: 0),
                new ParameterSpec
                {
                    Name = "kind",
                    // 不写 Default：省略时要看这份 manifest 有没有 conventions。写了默认值，总线会代填，两种仓就分不开。
                    Description = "manifest documents 里的键。省略时：声明了 conventions 就读它，否则读 overview",
                    Position = 1,
                    AllowedValues = ["conventions", "overview", "technicalContract", "decisions", "verification"],
                },
            ],
            Handler = CommandDescriptor.Sync(context => DianaCommandGuard.Run(() =>
            {
                var root = resolveProject(context.RequireString("name"));
                var manifest = ReadManifest(root);
                var (kind, relativePath) = ResolveDocument(manifest, context.GetString("kind"));
                var path = ResolveDeclaredFile(root, relativePath);
                var info = new FileInfo(path);
                if (info.Length > MaximumDocumentBytes)
                    throw new InvalidOperationException("文档超过 512 KiB 上限");
                return CommandResult.Ok(relativePath, new
                {
                    Project = manifest.Name,
                    Kind = kind,
                    Path = relativePath,
                    Content = File.ReadAllText(path),
                });
            })),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.align",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "检查已登记项目的 manifest 与文档入口；省略 name 动态列举项目库，仅检查入口存在性",
            Example = "diana.project.align",
            Readonly = true,
            Parameters = [Text("name", "只检查该已登记项目；省略则检查库根所有带 manifest 的 Git 项目")],
            Handler = CommandDescriptor.Sync(context => DianaCommandGuard.Run(() =>
            {
                var selected = context.GetString("name");
                var names = string.IsNullOrWhiteSpace(selected) ? projectNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray() : new[] { selected.Trim() };
                var results = names.Select(name => AlignOne(name, resolveProject)).ToList();
                var passed = results.Count(item => item.Aligned);
                return CommandResult.Ok($"项目入口对齐检查：{passed}/{results.Count} 通过", new
                {
                    ExpectedProjects = names,
                    Passed = passed,
                    Total = results.Count,
                    Projects = results,
                });
            })),
        });
    }

    private static AlignmentResult AlignOne(string name, Func<string, string> resolveProject)
    {
        try
        {
            var root = resolveProject(name);
            var manifest = ReadManifest(root);
            // 入口以 manifest 实际声明的为准。改造完的仓只有 conventions；还没改的仍是四份旧键。缺一则不算对齐。
            var documentStates = manifest.Documents
                .Select(item => new DocumentState(item.Key, item.Value, File.Exists(ResolveDeclaredFile(root, item.Value))))
                .ToList();
            var aligned = MatchesProjectIdentity(name, manifest.Name)
                          && manifest.SourceRoots.Count > 0
                          && documentStates.Count > 0
                          && documentStates.All(item => item.Exists);
            return new AlignmentResult(name, aligned, manifest.Name, manifest.Version, documentStates, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new AlignmentResult(name, false, null, null, [], ex.Message);
        }
    }

    internal static bool MatchesProjectIdentity(string projectDirectory, string manifestName)
    {
        var marker = projectDirectory.IndexOf("-History", StringComparison.OrdinalIgnoreCase);
        var expected = marker >= 0 ? projectDirectory[(marker + 1)..] : projectDirectory;
        return expected.Equals(manifestName, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectManifest ReadManifest(string root)
    {
        var path = ResolveDeclaredFile(root, "project.manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var json = document.RootElement;
        var project = RequiredObject(json, "project");
        var paths = RequiredObject(json, "paths");
        var documents = RequiredObject(json, "documents");
        var commands = RequiredObject(json, "commands");
        var sourceRoots = ReadStringArray(paths, "sourceRoots");
        var documentPaths = documents.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.Value.GetString()))
            .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        if (documentPaths.Count == 0)
            throw new InvalidOperationException("manifest documents 没有可读取的路径");
        return new ProjectManifest(
            RequiredString(project, "name"),
            RequiredString(project, "version"),
            RequiredString(project, "status"),
            ReadStringArray(paths, "activeRoots"),
            sourceRoots,
            documentPaths,
            commands.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase));
    }

    private static (string Kind, string RelativePath) ResolveDocument(ProjectManifest manifest, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            if (manifest.Documents.TryGetValue("conventions", out var conventions))
                return ("conventions", conventions);
            if (manifest.Documents.TryGetValue("overview", out var overview))
                return ("overview", overview);
            throw new ArgumentException("未指定 kind，且 manifest 既没有 conventions 也没有 overview。可用键: " + JoinKeys(manifest));
        }

        var kind = requested.Trim();
        if (!manifest.Documents.TryGetValue(kind, out var relativePath))
            throw new ArgumentException($"manifest documents 没有键 {kind}。可用键: {JoinKeys(manifest)}");
        return (kind, relativePath);
    }

    private static string JoinKeys(ProjectManifest manifest)
        => string.Join("、", manifest.Documents.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

    private static string ResolveDeclaredFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("manifest 路径必须是项目根下的相对路径");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("manifest 路径越出项目根");
        return fullPath;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException($"manifest 缺少对象: {name}");

    private static string RequiredString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidOperationException($"manifest 字段为空: {name}")
            : throw new InvalidOperationException($"manifest 缺少字符串: {name}");

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToList()
            : throw new InvalidOperationException($"manifest 缺少数组: {name}");

    private static ParameterSpec Text(string name, string description, string? defaultValue = null, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Default = defaultValue, Required = required, Position = position };

    private sealed record ProjectManifest(
        string Name, string Version, string Status, IReadOnlyList<string> ActiveRoots,
        IReadOnlyList<string> SourceRoots, IReadOnlyDictionary<string, string> Documents,
        IReadOnlyDictionary<string, string> Commands);

    private sealed record DocumentState(string Kind, string Path, bool Exists);

    private sealed record AlignmentResult(
        string Project, bool Aligned, string? ManifestName, string? Version,
        IReadOnlyList<DocumentState> Documents, string? Error);
}
