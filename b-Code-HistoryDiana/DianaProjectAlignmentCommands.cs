using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>Provides read-only project manifest, current-document, and cross-project alignment views.</summary>
internal static class DianaProjectAlignmentCommands
{
    private const int MaximumDocumentBytes = 512 * 1024;
    private static readonly string[] AlignedProjects =
    [
        "2026-020-HistoryJanus",
        "2026-021-HistoryMercury",
        "2026-024-HistoryMinerva",
        "2026-023-HistoryVulcan",
    ];

    public static void Register(CommandRegistry registry, Func<string, string> resolveProject)
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
            Handler = CommandDescriptor.Sync(context =>
            {
                var project = ReadManifest(resolveProject(context.RequireString("name")));
                return CommandResult.Ok(project.Name, project);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.docs",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "按项目 manifest 读取现行概览、技术、决策或验证文档",
            Example = "diana.project.docs name=2026-020-HistoryJanus kind=overview",
            Readonly = true,
            Parameters =
            [
                Text("name", "已登记工作树名称", required: true, position: 0),
                Text("kind", "文档类型：overview、technicalContract、decisions、verification", "overview", position: 1),
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var root = resolveProject(context.RequireString("name"));
                var manifest = ReadManifest(root);
                var kind = (context.GetString("kind") ?? "overview").Trim();
                var relativePath = kind switch
                {
                    "overview" => manifest.Overview,
                    "technicalContract" => manifest.TechnicalContract,
                    "decisions" => manifest.Decisions,
                    "verification" => manifest.Verification,
                    _ => throw new ArgumentException("kind 必须是 overview、technicalContract、decisions 或 verification"),
                };
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
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.project.align",
            Domain = "HistoryDiana",
            CommandClass = "project",
            Summary = "检查 Janus、Mercury、Minerva、Vulcan 四个项目的 manifest 与文档入口对齐状态",
            Example = "diana.project.align",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var results = AlignedProjects.Select(name => AlignOne(name, resolveProject)).ToList();
                var passed = results.Count(item => item.Aligned);
                return CommandResult.Ok($"四项目对齐检查：{passed}/{results.Count} 通过", new
                {
                    ExpectedProjects = AlignedProjects,
                    Passed = passed,
                    Total = results.Count,
                    Projects = results,
                });
            }),
        });
    }

    private static AlignmentResult AlignOne(string name, Func<string, string> resolveProject)
    {
        try
        {
            var root = resolveProject(name);
            var manifest = ReadManifest(root);
            var documents = new[] { manifest.Overview, manifest.TechnicalContract, manifest.Decisions, manifest.Verification };
            var documentStates = documents.Select(path => new DocumentState(path, File.Exists(ResolveDeclaredFile(root, path)))).ToList();
            var aligned = manifest.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                          && manifest.SourceRoots.Count > 0
                          && documentStates.All(item => item.Exists);
            return new AlignmentResult(name, aligned, manifest.Name, manifest.Version, documentStates, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new AlignmentResult(name, false, null, null, [], ex.Message);
        }
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
        return new ProjectManifest(
            RequiredString(project, "name"),
            RequiredString(project, "version"),
            RequiredString(project, "status"),
            ReadStringArray(paths, "activeRoots"),
            sourceRoots,
            RequiredString(documents, "overview"),
            RequiredString(documents, "technicalContract"),
            RequiredString(documents, "decisions"),
            RequiredString(documents, "verification"),
            commands.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase));
    }

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
        IReadOnlyList<string> SourceRoots, string Overview, string TechnicalContract,
        string Decisions, string Verification, IReadOnlyDictionary<string, string> Commands);

    private sealed record DocumentState(string Path, bool Exists);

    private sealed record AlignmentResult(
        string Project, bool Aligned, string? ManifestName, string? Version,
        IReadOnlyList<DocumentState> Documents, string? Error);
}
