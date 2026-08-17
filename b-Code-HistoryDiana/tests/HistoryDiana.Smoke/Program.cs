using System.IO;
using System.Security.Cryptography;
using System.Text;
using BaseVariable;
using HistoryDiana;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "HistoryDiana.Smoke", Guid.NewGuid().ToString("N"));
var commandCount = 0;
var classCount = 0;
try
{
    var projectName = "2026-999-HistoryDianaSmoke";
    var worktreeDirectory = Path.Combine(temporaryRoot, projectName);
    Directory.CreateDirectory(Path.Combine(worktreeDirectory, ".git"));
    File.WriteAllText(Path.Combine(worktreeDirectory, "sample.txt"), "HistoryDiana smoke test");

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
    File.WriteAllText(Path.Combine(channelPackage, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":false}
        """);
    File.WriteAllText(Path.Combine(channelPackage, "SHA256SUMS"), string.Join(Environment.NewLine,
        $"{Hash(apiPath)}  docs/模块API.md",
        $"{Hash(changelogPath)}  docs/变更摘要.md") + Environment.NewLine);

    var registry = new CommandRegistry();
    var log = new TestLog();
    var bus = new CommandBus(registry, log);
    var settings = new TestSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["proj.libraryroot"] = temporaryRoot,
    });
    var context = new TestModuleContext(bus, settings, log, temporaryRoot, registry);
    new HistoryDianaCommands().Attach(context);

    var assembly = typeof(HistoryDianaCommands).Assembly;
    var moduleInfos = assembly.GetTypes()
        .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
        .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
        .ToList();
    Equal(1, moduleInfos.Count, "程序集只能提供一个模块入口");
    Equal("HistoryDiana", moduleInfos[0].ModuleName, "模块名");
    Equal("1.10.16", moduleInfos[0].Version, "模块版本");
    True(moduleInfos[0].MainClassType is null, "命令必须由宿主上下文显式登记");

    var descriptors = registry.All().OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
    var names = descriptors.Select(item => item.Name).ToArray();
    foreach (var required in new[]
             {
                 "diana.kit.base64", "diana.kit.guid", "diana.kit.now", "diana.kit.sha256",
                 "diana.project.align", "diana.project.docs", "diana.project.largest",
                 "diana.project.manifest", "diana.project.recent", "diana.project.summary",
                 "diana.relay.call", "diana.relay.describe", "diana.relay.list",
                 "diana.release.cycle", "diana.trial.load", "diana.worktree.merge",
                 "diana.docs.catalog", "diana.docs.janus",
             })
    {
        True(names.Contains(required), $"缺少命令 {required}");
    }

    True(!names.Contains("diana.trial.list"), "私有试用列表命令必须退役");
    True(!names.Contains("diana.trial.call"), "私有试用调用命令必须退役");
    True(!names.Contains("diana.trial.unload"), "私有试用卸载命令必须退役");
    True(!names.Contains("diana.release.start"), "不得登记已退役的 release.start");
    True(!names.Contains("diana.worktree.remove"), "不得登记已退役的 worktree.remove");
    True(names.All(name => name.StartsWith("diana.", StringComparison.Ordinal)), "命令前缀必须是 diana");
    True(descriptors.All(item => item.Domain == "HistoryDiana"), "命令域必须归属 HistoryDiana");
    SequenceEqual(
        new[] { "docs", "kit", "project", "relay", "release", "trial", "worktree" },
        descriptors.Select(item => item.CommandClass!).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        "Diana 命令类集合");

    commandCount = descriptors.Count;
    classCount = descriptors.Select(item => item.CommandClass!).Distinct(StringComparer.Ordinal).Count();
    Equal(24, commandCount, "固定命令数加夹具文档通道");
    var writeCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "diana.relay.call", "diana.trial.load", "diana.release.cycle",
        "diana.worktree.root", "diana.worktree.create", "diana.worktree.merge",
    };
    Equal(6, writeCommands.Count, "写命令白名单条数");
    True(descriptors.Where(item => !writeCommands.Contains(item.Name)).All(item => item.Readonly),
        "白名单以外命令必须只读");

    Equal("缺少必填参数: path=", bus.Validate("diana.trial.load"), "trial.load 必须要求 path");
    var loadDescriptor = descriptors.Single(item => item.Name == "diana.trial.load");
    SequenceEqual(new[] { "path" }, loadDescriptor.Parameters.Select(item => item.Name).ToArray(),
        "trial.load 只能接受发布包路径");
    var cycleDescriptor = descriptors.Single(item => item.Name == "diana.release.cycle");
    True(cycleDescriptor.Parameters.All(item => item.Name != "ui"), "release.cycle 不得保留 ui 参数");

    var summary = await bus.ExecuteAsync($"diana.project.summary name={projectName}", "smoke");
    var recent = await bus.ExecuteAsync($"diana.project.recent name={projectName} days=1", "smoke");
    var largest = await bus.ExecuteAsync($"diana.project.largest name={projectName} minMb=0", "smoke");
    True(summary.Success && recent.Success && largest.Success, "项目巡检命令执行");
    True(summary.Data is not null && recent.Data is not null && largest.Data is not null, "项目巡检返回结构化结果");
    True(!(await bus.ExecuteAsync($"diana.project.manifest name={projectName}", "smoke")).Success,
        "缺 manifest 的项目必须明确失败");

    var catalog = await bus.ExecuteAsync("diana.docs.catalog", "smoke");
    True(catalog.Success && catalog.Message.Contains("diana.docs.janus", StringComparison.Ordinal),
        "catalog 必须列出版本化候选通道");
    True(catalog.Message.Contains("History*-v*", StringComparison.Ordinal), "catalog 必须说明版本化候选规则");
    True(!catalog.Message.Contains("current", StringComparison.OrdinalIgnoreCase), "catalog 不得引用 current 层");
    var listed = await bus.ExecuteAsync("diana.docs.janus", "smoke");
    True(listed.Success && listed.Message.Contains("docs/模块API.md", StringComparison.Ordinal), "文档通道列举");
    var opened = await bus.ExecuteAsync("diana.docs.janus file=模块API.md heading=命令", "smoke");
    True(opened.Success && opened.Message.Contains("command-body", StringComparison.Ordinal), "文档按节读取");
    True(!opened.Message.Contains("window-body", StringComparison.Ordinal), "按节读取不得越界");
    var outline = await bus.ExecuteAsync("diana.docs.janus file=docs/变更摘要.md", "smoke");
    True(outline.Success && outline.Message.Contains("版本:", StringComparison.Ordinal), "长文默认返回目录");
    var versionSlice = await bus.ExecuteAsync("diana.docs.janus file=变更摘要.md heading=9.9.9", "smoke");
    True(versionSlice.Success && versionSlice.Message.Contains("first-item", StringComparison.Ordinal), "按版本读取");
    True(!versionSlice.Message.Contains("other-item", StringComparison.Ordinal), "版本读取不得带入其他版本");
    True(!(await bus.ExecuteAsync("diana.docs.janus file=../secret.md", "smoke")).Success, "文档路径不得越界");

    var installCalls = new List<(string Source, string Path)>();
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.install",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "严格安装发布包",
        Parameters = [new ParameterSpec { Name = "path", Description = "发布包路径", Required = true, Position = 0 }],
        Handler = CommandDescriptor.Sync(commandContext =>
        {
            installCalls.Add((commandContext.Source, commandContext.RequireString("path")));
            return CommandResult.Ok("installed");
        }),
    });
    var candidateRoot = Path.Combine(temporaryRoot, "z-Publish", "HistoryJanus-v9.9.9");
    var historyRoot = Path.Combine(temporaryRoot, "z-Publish", "history", "HistoryJanus-v9.9.8");
    Directory.CreateDirectory(candidateRoot);
    Directory.CreateDirectory(historyRoot);
    var candidateLoad = await bus.ExecuteAsync(
        $"diana.trial.load path={CommandParser.QuoteArg(candidateRoot)}", "smoke");
    var historyLoad = await bus.ExecuteAsync(
        $"diana.trial.load path={CommandParser.QuoteArg(historyRoot)}", "smoke");
    True(candidateLoad.Success && historyLoad.Success, "候选与历史包必须复用 Vulcan 热重载");
    True(candidateLoad.Message.Contains("热重载", StringComparison.Ordinal), "成功消息说明热重载语义");
    Equal(2, installCalls.Count, "候选与历史包各安装一次");
    True(installCalls.All(call => call.Source == "host:diana.trial.load"), "安装必须使用受信任宿主来源");
    Equal(Path.GetFullPath(candidateRoot), installCalls[0].Path, "候选路径完整转发");
    Equal(Path.GetFullPath(historyRoot), installCalls[1].Path, "历史路径完整转发");

    var isolatedRegistry = new CommandRegistry();
    var isolatedLog = new TestLog();
    var isolatedBus = new CommandBus(isolatedRegistry, isolatedLog);
    new HistoryDianaCommands().Attach(
        new TestModuleContext(isolatedBus, settings, isolatedLog, temporaryRoot, isolatedRegistry));
    var missingInstall = await isolatedBus.ExecuteAsync(
        $"diana.trial.load path={CommandParser.QuoteArg(candidateRoot)}", "smoke");
    True(!missingInstall.Success && missingInstall.Message.Contains("vulcan.module.install", StringComparison.Ordinal),
        "缺少宿主安装接口时必须明确失败");

    Equal(0, assembly.GetTypes().Count(type => type.Name.Contains("ProjectPulse", StringComparison.Ordinal)),
        "程序集不得保留 ProjectPulse 类型");
    Equal(0, assembly.GetTypes().Count(type => type.IsPublic && !type.IsAbstract
        && typeof(IUiModule).IsAssignableFrom(type)), "模块不得注册 UI 生命周期");
}
finally
{
    try { Directory.Delete(temporaryRoot, recursive: true); }
    catch (Exception) when (Directory.Exists(temporaryRoot)) { }
}

Console.WriteLine($"HistoryDiana.Smoke: PASS (1 module, {commandCount} commands in {classCount} classes, strict runtime replacement)");

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

sealed class TestModuleContext(
    CommandBus bus,
    ISettingsService settings,
    IShellLog log,
    string dataDirectory,
    CommandRegistry registry) : IModuleContext
{
    public CommandBus Bus { get; } = bus;
    public ISettingsService Settings { get; } = settings;
    public IShellLog Log { get; } = log;
    public string DataDirectory { get; } = dataDirectory;
    public void RegisterCommands(Action<CommandRegistry> configure) => configure(registry);
}

sealed class TestSettings(IReadOnlyDictionary<string, string> values) : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
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
