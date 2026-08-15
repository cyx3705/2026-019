using System.IO;
using BaseVariable;
using HistoryDiana;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "HistoryDiana.Smoke", Guid.NewGuid().ToString("N"));
// 收尾摘要里的数字必须来自实际注册结果，写死会在增删命令后悄悄失真。
var commandCount = 0;
var classCount = 0;
try
{
    var projectName = "2026-999-HistoryDianaSmoke";
    var worktreeDirectory = Path.Combine(temporaryRoot, projectName);
    Directory.CreateDirectory(Path.Combine(worktreeDirectory, ".git"));
    File.WriteAllText(Path.Combine(worktreeDirectory, "sample.txt"), "HistoryDiana smoke test");

    var channelProject = Path.Combine(temporaryRoot, "2026-020-HistoryJanus");
    var channelPackage = Path.Combine(channelProject, "z-HistoryJanus");
    Directory.CreateDirectory(Path.Combine(channelPackage, "docs"));
    var apiPath = Path.Combine(channelPackage, "docs", "模块API.md");
    File.WriteAllText(apiPath, "# Janus API\nsmoke");
    File.WriteAllText(Path.Combine(channelPackage, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":false}
        """);
    var apiHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(apiPath)));
    File.WriteAllText(Path.Combine(channelPackage, "SHA256SUMS"), $"{apiHash}  docs/模块API.md{Environment.NewLine}");

    var registry = new CommandRegistry();
    var log = new TestLog();
    var bus = new CommandBus(registry, log);
    var settings = new TestSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["proj.libraryroot"] = temporaryRoot,
    });
    var context = new TestModuleContext(bus, settings, log, temporaryRoot, registry);
    var commands = new HistoryDianaCommands();
    commands.Attach(context);

    var assembly = typeof(HistoryDianaCommands).Assembly;
    var moduleInfos = assembly.GetTypes()
        .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
        .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
        .ToList();

    Equal(1, moduleInfos.Count, "程序集只能提供一个模块入口");
    Equal("HistoryDiana", moduleInfos[0].ModuleName, "模块名");
    Equal("1.3.0", moduleInfos[0].Version, "模块版本");
    True(moduleInfos[0].MainClassType is null, "命令必须由宿主上下文显式登记");

    var descriptors = registry.All()
        .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
        .ToList();
    var names = descriptors.Select(descriptor => descriptor.Name).ToArray();
    foreach (var required in new[]
        {
            "diana.kit.base64", "diana.kit.guid", "diana.kit.now", "diana.kit.sha256",
            "diana.project.align", "diana.project.docs", "diana.project.largest",
            "diana.project.manifest", "diana.project.recent", "diana.project.summary",
            "diana.relay.call", "diana.relay.describe", "diana.relay.list",
            "diana.docs.catalog", "diana.docs.janus",
        })
    {
        True(names.Contains(required), $"缺少命令 {required}");
    }
    True(names.All(name => name.StartsWith("diana.", StringComparison.Ordinal)),
        "不得保留 StudioTools 或 ProjectPulse 命令前缀");
    True(descriptors.All(descriptor => descriptor.Domain == "HistoryDiana"), "命令域必须归属 HistoryDiana");
    SequenceEqual(
        new[] { "docs", "kit", "project", "relay", "trial" },
        descriptors.Select(descriptor => descriptor.CommandClass!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray(),
        "Diana 只有 docs / kit / project / relay / trial 五个类");
    commandCount = descriptors.Count;
    classCount = descriptors.Select(descriptor => descriptor.CommandClass!)
        .Distinct(StringComparer.Ordinal)
        .Count();
    // Diana 默认只读。写操作必须逐条列名，不能靠"新命令自然就不只读"混进来：
    // relay.call 真的调用外部工具；trial.load/call/unload 装载并执行候选模块的任意代码。
    var writeCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "diana.relay.call", "diana.trial.load", "diana.trial.call", "diana.trial.unload",
    };
    True(
        descriptors.Where(descriptor => !writeCommands.Contains(descriptor.Name))
            .All(descriptor => descriptor.Readonly),
        "除显式列名的写命令外，所有 Diana 命令必须声明为只读");

    Equal(null, bus.Validate($"diana.project.summary name={projectName}"), "summary 命令校验");
    Equal(null, bus.Validate($"diana.project.recent name={projectName} days=1 limit=10"), "recent 命令校验");
    Equal(null, bus.Validate($"diana.project.largest name={projectName} minMb=0"), "largest 命令校验");

    var summary = await bus.ExecuteAsync($"diana.project.summary name={projectName}", "smoke");
    var recent = await bus.ExecuteAsync($"diana.project.recent name={projectName} days=1", "smoke");
    var largest = await bus.ExecuteAsync($"diana.project.largest name={projectName} minMb=0", "smoke");
    True(summary.Success, "summary 命令执行");
    True(recent.Success, "recent 命令执行");
    True(largest.Success, "largest 命令执行");
    True(summary.Data is not null && recent.Data is not null && largest.Data is not null,
        "命令必须返回结构化结果");

    var manifest = await bus.ExecuteAsync($"diana.project.manifest name={projectName}", "smoke");
    True(!manifest.Success, "没有 manifest 的已登记工作树必须明确失败");
    var alignment = await bus.ExecuteAsync("diana.project.align", "smoke");
    True(alignment.Success && alignment.Data is not null, "四项目对齐命令执行并返回结构化结果");

    var catalog = await bus.ExecuteAsync("diana.docs.catalog", "smoke");
    True(catalog.Success, "docs catalog 必须成功");
    True(catalog.Message.Contains("diana.docs.janus", StringComparison.Ordinal),
        "索引必须把通道命令显式写进对话文本");
    var listed = await bus.ExecuteAsync("diana.docs.janus", "smoke");
    True(listed.Success && listed.Message.Contains("docs/模块API.md", StringComparison.Ordinal),
        "通道省略 file 时只列出本通道文档");
    var opened = await bus.ExecuteAsync("diana.docs.janus file=docs/模块API.md", "smoke");
    True(opened.Success, "通道按 file 读取 z 内 Markdown");
    var escaped = await bus.ExecuteAsync("diana.docs.janus file=../secret.md", "smoke");
    True(!escaped.Success, "通道必须拒绝越出 z 的路径");

    Equal(0, assembly.GetTypes().Count(type => type.Name.Contains("ProjectPulse", StringComparison.Ordinal)),
        "程序集不得保留 ProjectPulse 类型");
    Equal(0, assembly.GetTypes().Count(type => type.IsPublic && !type.IsAbstract
        && typeof(IUiModule).IsAssignableFrom(type)), "模块不得注册 UI 生命周期");
}
finally
{
    if (Directory.Exists(temporaryRoot))
        Directory.Delete(temporaryRoot, recursive: true);
}

Console.WriteLine($"HistoryDiana.Smoke: PASS (1 module, {commandCount} commands in {classCount} classes, explicit HistoryVulcan command registration)");

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
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

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var value) ? value : fallback;

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
