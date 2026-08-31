using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using BaseVariable;
using HistoryDiana;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "HistoryDiana.Smoke", Guid.NewGuid().ToString("N"));
var commandCount = 0;
var classCount = 0;
try
{
    var projectName = "2026-999-HistoryDianaSmoke";
    var worktreeDirectory = Path.Combine(temporaryRoot, projectName);
    Directory.CreateDirectory(Path.Combine(worktreeDirectory, ".git"));
    File.WriteAllText(Path.Combine(worktreeDirectory, "sample.txt"), "HistoryDiana smoke test");

    var endpointPath = Path.Combine(temporaryRoot, "endpoint.json");
    File.WriteAllText(endpointPath, "{\"port\":59095,\"accessToken\":\"smoke-token\"}");
    var mcpConfigPath = Path.Combine(temporaryRoot, "mcp.json");
    var previousEndpoint = Environment.GetEnvironmentVariable("HISTORYVULCAN_ENDPOINT");
    var previousMcpConfig = Environment.GetEnvironmentVariable("HISTORYVULCAN_MCP_CONFIG");
    Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", endpointPath);
    try
    {
        var endpoint = DianaRuntime.ReadMcpEndpoint();
        Equal("http://127.0.0.1:59095/mcp", endpoint.Uri.ToString(), "MCP endpoint URI");
        Equal("smoke-token", endpoint.AccessToken, "MCP endpoint token");

        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", null);
        Environment.SetEnvironmentVariable("HISTORYVULCAN_MCP_CONFIG", mcpConfigPath);
        File.WriteAllText(mcpConfigPath,
            "{\"mcpServers\":{\"history-vulcan\":{\"url\":\"http://127.0.0.1:8777/mcp\"}}}");
        endpoint = DianaRuntime.ReadMcpEndpoint();
        Equal("http://127.0.0.1:8777/mcp", endpoint.Uri.ToString(), "Cursor MCP config URI");
        Equal<string?>(null, endpoint.AccessToken, "本机 MCP 不持券");

        File.WriteAllText(mcpConfigPath,
            "{\"mcpServers\":{\"history-vulcan\":{\"url\":\"https://example.com/mcp\"}}}");
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(), "MCP 端点必须限制为本机回环");

        File.WriteAllText(mcpConfigPath,
            "{\"mcpServers\":{\"history-vulcan\":{\"url\":8777}}}");
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(), "MCP URL 类型错误必须明确失败");

        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", "https://127.0.0.1:8777/mcp");
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(), "显式 MCP URL 也必须拒绝 HTTPS");
    }
    finally
    {
        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", previousEndpoint);
        Environment.SetEnvironmentVariable("HISTORYVULCAN_MCP_CONFIG", previousMcpConfig);
    }

    using (var rows = JsonDocument.Parse(
               "[{\"CommandName\":\"diana.view.windows\",\"Source\":\"module\"}," +
               "{\"CommandName\":\"vulcan.command.list\",\"Source\":\"framework:service\"}]"))
    {
        True(OhsmcpClient.TryReadModuleNames(rows.RootElement, out var moduleTools), "模块工具目录必须可解析");
        True(moduleTools.SetEquals(["diana_view_windows"]), "模块命令名必须映射为当前 MCP 工具名");
    }

    True(DianaProjectAlignmentCommands.MatchesProjectIdentity(
            "2026-023-HistoryVulcan", "HistoryVulcan"),
        "对齐检查必须把项目目录身份映射为 manifest 产品名");
    True(!DianaProjectAlignmentCommands.MatchesProjectIdentity(
            "2026-023-HistoryVulcan", "HistoryJanus"),
        "对齐检查必须拒绝不匹配的 manifest 产品名");

    var channelPackage = Path.Combine(
        temporaryRoot, "2026-020-HistoryJanus", "z-Publish", "HistoryJanus-v9.9.9");
    Directory.CreateDirectory(Path.Combine(channelPackage, "docs"));
    var apiPath = Path.Combine(channelPackage, "docs", "模块API.md");
    File.WriteAllText(apiPath, "# Janus API\nintro\n## 命令\ncommand-body\n## 窗口\nwindow-body\n");
    var changelogPath = Path.Combine(channelPackage, "docs", "变更摘要.md");
    var changelog = new StringBuilder("# 变更\n## 主要变化\n- 9.9.9 first-item\n  continued-line\n- 9.9.8 other-item\n## 附录\n");
    while (Encoding.UTF8.GetByteCount(changelog.ToString()) <= 12 * 1024)
        changelog.Append("padding-line-to-force-outline\n");
    File.WriteAllText(changelogPath, changelog.ToString());
    var channelManifestPath = Path.Combine(channelPackage, "module.manifest.json");
    File.WriteAllText(channelManifestPath,
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":false}
        """);
    File.WriteAllText(Path.Combine(channelPackage, "SHA256SUMS"), string.Join(Environment.NewLine,
        $"{Hash(apiPath)}  docs/模块API.md",
        $"{Hash(changelogPath)}  docs/变更摘要.md",
        $"{Hash(channelManifestPath)}  module.manifest.json") + Environment.NewLine);

    var invalidPackage = Path.Combine(
        temporaryRoot, "2026-998-HistoryBroken", "z-Publish", "HistoryBroken-v1.0.0");
    Directory.CreateDirectory(Path.Combine(invalidPackage, "docs"));
    var invalidDocumentPath = Path.Combine(invalidPackage, "docs", "不应出现.md");
    var invalidManifestPath = Path.Combine(invalidPackage, "module.manifest.json");
    File.WriteAllText(invalidDocumentPath, "# invalid package");
    File.WriteAllText(invalidManifestPath, "{invalid-json");
    File.WriteAllText(Path.Combine(invalidPackage, "SHA256SUMS"), string.Join(Environment.NewLine,
        $"{Hash(invalidDocumentPath)}  docs/不应出现.md",
        $"{Hash(invalidManifestPath)}  module.manifest.json") + Environment.NewLine);

    var unsignedPackage = Path.Combine(
        temporaryRoot, "2026-997-HistoryUnsigned", "z-Publish", "HistoryUnsigned-v1.0.0");
    Directory.CreateDirectory(Path.Combine(unsignedPackage, "docs"));
    var unsignedDocumentPath = Path.Combine(unsignedPackage, "docs", "不应出现.md");
    File.WriteAllText(unsignedDocumentPath, "# unsigned manifest");
    File.WriteAllText(Path.Combine(unsignedPackage, "module.manifest.json"),
        "{\"name\":\"HistoryUnsigned\",\"version\":\"1.0.0\"}");
    File.WriteAllText(Path.Combine(unsignedPackage, "SHA256SUMS"),
        $"{Hash(unsignedDocumentPath)}  docs/不应出现.md{Environment.NewLine}");

    var registry = new CommandRegistry();
    var log = new TestLog();
    var bus = new CommandBus(registry, log);
    var context = new TestModuleContext(bus, registry);
    new HistoryDianaCommands(() => temporaryRoot).Attach(context);

    var assembly = typeof(HistoryDianaCommands).Assembly;
    var moduleInfos = assembly.GetTypes()
        .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
        .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
        .ToList();
    Equal(1, moduleInfos.Count, "程序集只能提供一个模块入口");
    Equal("HistoryDiana", moduleInfos[0].ModuleName, "模块名");
    True(moduleInfos[0].Description.Contains("前端图形查看", StringComparison.Ordinal), "模块描述必须公开图形查看能力");
    Equal(assembly.GetName().Version?.ToString(3), moduleInfos[0].Version, "模块版本");
    True(moduleInfos[0].MainClassType is null, "命令必须由宿主上下文显式登记");

    var descriptors = registry.All().OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
    var names = descriptors.Select(item => item.Name).ToArray();
    foreach (var required in new[]
             {
                 "diana.kit.base64", "diana.kit.guid", "diana.kit.now", "diana.kit.sha256",
                 "diana.project.align", "diana.project.docs", "diana.project.largest",
                 "diana.project.manifest", "diana.project.recent", "diana.project.summary",
                 "diana.relay.call", "diana.relay.describe", "diana.relay.list",
                 "diana.docs.catalog", "diana.docs.read",
                 "diana.view.capture", "diana.view.windows",
             })
    {
        True(names.Contains(required), $"缺少命令 {required}");
    }

    True(!names.Any(name => name.StartsWith("diana.trial.", StringComparison.Ordinal)), "trial 命令面已迁出");
    True(!names.Any(name => name.StartsWith("diana.release.", StringComparison.Ordinal)), "release 命令面已迁出");
    True(!names.Any(name => name.StartsWith("diana.worktree.", StringComparison.Ordinal)), "worktree 命令面已迁出");
    True(names.All(name => name.StartsWith("diana.", StringComparison.Ordinal)), "命令前缀必须是 diana");
    True(descriptors.All(item => item.Domain == "HistoryDiana"), "命令域必须归属 HistoryDiana");
    SequenceEqual(
        new[] { "docs", "kit", "project", "relay", "view" },
        descriptors.Select(item => item.CommandClass!).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        "Diana 命令类集合");

    commandCount = descriptors.Count;
    classCount = descriptors.Select(item => item.CommandClass!).Distinct(StringComparer.Ordinal).Count();
    var writeCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "diana.relay.call",
        "diana.view.capture",
    };
    True(descriptors.Where(item => !writeCommands.Contains(item.Name)).All(item => item.Readonly),
        "白名单以外命令必须只读");
    True(descriptors.Single(item => item.Name == "diana.relay.call").Readonly == false,
        "relay.call 必须是写命令");
    True(descriptors.Single(item => item.Name == "diana.view.capture").Readonly == false,
        "view.capture 写入运行态 PNG");

    var windows = await bus.ExecuteAsync("diana.view.windows", "smoke");
    True(windows.Success && windows.Data is IReadOnlyList<WindowView>, "图形查看器必须安全列出可捕获窗口");
    var syntheticPng = Path.Combine(temporaryRoot, "viewer-smoke.png");
    DianaWindowCapture.SavePng(syntheticPng,
    [
        0, 0, 0, 0, 255, 255, 255, 0,
        0, 0, 255, 0, 0, 255, 0, 0,
    ], 2, 2);
    using (var stream = File.OpenRead(syntheticPng))
    {
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Equal(2, decoded.Frames[0].PixelWidth, "图形查看器 PNG 宽度");
        Equal(2, decoded.Frames[0].PixelHeight, "图形查看器 PNG 高度");
    }

    var summary = await bus.ExecuteAsync($"diana.project.summary name={projectName}", "smoke");
    var recent = await bus.ExecuteAsync($"diana.project.recent name={projectName} days=1", "smoke");
    var largest = await bus.ExecuteAsync($"diana.project.largest name={projectName} minMb=0", "smoke");
    True(summary.Success && recent.Success && largest.Success, "项目巡检命令执行");
    True(summary.Data is not null && recent.Data is not null && largest.Data is not null, "项目巡检返回结构化结果");
    True(!(await bus.ExecuteAsync($"diana.project.manifest name={projectName}", "smoke")).Success,
        "缺 manifest 的项目必须明确失败");

    var catalog = await bus.ExecuteAsync("diana.docs.catalog", "smoke");
    True(catalog.Success && catalog.Message.Contains("diana.docs.read domain=janus", StringComparison.Ordinal),
        "catalog 必须列出版本化候选通道");
    True(!catalog.Message.Contains("domain=broken", StringComparison.Ordinal), "manifest 损坏的发布包不得形成文档通道");
    True(!catalog.Message.Contains("domain=unsigned", StringComparison.Ordinal), "manifest 未受 SHA256SUMS 保护的发布包不得形成文档通道");
    True(catalog.Message.Contains("History*-v*", StringComparison.Ordinal), "catalog 必须说明版本化候选规则");
    True(!catalog.Message.Contains("current", StringComparison.OrdinalIgnoreCase), "catalog 不得引用 current 层");
    var listed = await bus.ExecuteAsync("diana.docs.read domain=janus", "smoke");
    True(listed.Success && listed.Message.Contains("docs/模块API.md", StringComparison.Ordinal), "文档通道列举");
    var opened = await bus.ExecuteAsync("diana.docs.read domain=janus file=模块API.md heading=命令", "smoke");
    True(opened.Success && opened.Message.Contains("command-body", StringComparison.Ordinal), "文档按节读取");
    True(!opened.Message.Contains("window-body", StringComparison.Ordinal), "按节读取不得越界");
    var outline = await bus.ExecuteAsync("diana.docs.read domain=janus file=docs/变更摘要.md", "smoke");
    True(outline.Success && outline.Message.Contains("版本:", StringComparison.Ordinal), "长文默认返回目录");
    var versionSlice = await bus.ExecuteAsync("diana.docs.read domain=janus file=变更摘要.md heading=9.9.9", "smoke");
    True(versionSlice.Success && versionSlice.Message.Contains("first-item", StringComparison.Ordinal), "按版本读取");
    True(!versionSlice.Message.Contains("other-item", StringComparison.Ordinal), "版本读取不得带入其他版本");
    True(!(await bus.ExecuteAsync("diana.docs.read domain=janus file=../secret.md", "smoke")).Success, "文档路径不得越界");

    var externalDocument = Path.Combine(temporaryRoot, "outside.md");
    var linkedDocument = Path.Combine(channelPackage, "docs", "linked.md");
    File.WriteAllText(externalDocument, "outside");
    try
    {
        File.CreateSymbolicLink(linkedDocument, externalDocument);
        Throws<InvalidOperationException>(
            () => DianaZDocCommands.ResolveInside(channelPackage, "docs/linked.md"),
            "文档路径不得通过重解析点越出发布包");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
    {
        // Some Windows CI accounts cannot create symbolic links; production guard is still compiled.
    }

    Equal(0, assembly.GetTypes().Count(type => type.Name.Contains("ProjectPulse", StringComparison.Ordinal)),
        "程序集不得保留 ProjectPulse 类型");
    Equal(0, assembly.GetTypes().Count(type => type.Name.Equals("IUiModule", StringComparison.Ordinal)),
        "模块不得声明已移除的 UI 生命周期类型");
}
finally
{
    try { Directory.Delete(temporaryRoot, recursive: true); }
    catch (Exception) when (Directory.Exists(temporaryRoot)) { }
}

Console.WriteLine($"HistoryDiana.Smoke: PASS (1 module, {commandCount} commands in {classCount} classes)");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}

static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}: expected={string.Join(',', expected)}, actual={string.Join(',', actual)}");
}

static void Throws<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

sealed class TestModuleContext(CommandBus bus, CommandRegistry registry) : IModuleContext
{
    public CommandBus Bus { get; } = bus;
    public void RegisterCommands(Action<CommandRegistry> configure) => configure(registry);
}

sealed class TestLog : IShellLog
{
    private readonly List<ShellLogEntry> _entries = [];
    public event EventHandler<ShellLogEntry>? EntryAdded;
    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.Now, level, category, message);
        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }
    public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;
}
