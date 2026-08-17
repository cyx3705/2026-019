using System.IO;
using System.Text;
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
    var channelPackage = Path.Combine(channelProject, "z-Publish");
    Directory.CreateDirectory(Path.Combine(channelPackage, "docs"));
    var apiPath = Path.Combine(channelPackage, "docs", "模块API.md");
    File.WriteAllText(apiPath, "# Janus API\nintro\n## 命令\ncommand-body\n## 窗口\nwindow-body\n");
    var changelogPath = Path.Combine(channelPackage, "docs", "变更摘要.md");
    var changelog = new StringBuilder();
    changelog.Append("# 变更\n## 主要变化\n- 9.9.9 first-item\n  continued-line\n- 9.9.8 other-item\n## 附录\n");
    while (Encoding.UTF8.GetByteCount(changelog.ToString()) <= 12 * 1024)
        changelog.Append("padding-line-to-force-outline\n");
    File.WriteAllText(changelogPath, changelog.ToString());
    File.WriteAllText(Path.Combine(channelPackage, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":false}
        """);
    var apiHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(apiPath)));
    var changelogHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(changelogPath)));
    File.WriteAllText(Path.Combine(channelPackage, "SHA256SUMS"),
        $"{apiHash}  docs/模块API.md{Environment.NewLine}{changelogHash}  docs/变更摘要.md{Environment.NewLine}");

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
    Equal("1.10.13", moduleInfos[0].Version, "模块版本");
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
    True(
        descriptors.All(descriptor =>
            descriptor.Example is null
            || !descriptor.Example.Contains("ProjectPulse", StringComparison.Ordinal)),
        "示例不得再引用已退役的 ProjectPulse");
    True(descriptors.All(descriptor => descriptor.Domain == "HistoryDiana"), "命令域必须归属 HistoryDiana");
    SequenceEqual(
        new[] { "docs", "kit", "project", "relay", "release", "trial", "worktree" },
        descriptors.Select(descriptor => descriptor.CommandClass!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray(),
        "Diana 只有 docs / kit / project / relay / release / trial / worktree 七个类");
    commandCount = descriptors.Count;
    classCount = descriptors.Select(descriptor => descriptor.CommandClass!)
        .Distinct(StringComparer.Ordinal)
        .Count();
    // Diana 默认只读。写操作必须逐条列名，不能靠"新命令自然就不只读"混进来。
    var writeCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "diana.relay.call", "diana.trial.load", "diana.trial.call", "diana.trial.unload",
        "diana.release.cycle",
        "diana.worktree.root", "diana.worktree.create", "diana.worktree.merge",
    };
    True(
        descriptors.Where(descriptor => !writeCommands.Contains(descriptor.Name))
            .All(descriptor => descriptor.Readonly),
        "除显式列名的写命令外，所有 Diana 命令必须声明为只读");
    Equal(8, writeCommands.Count, "写命令白名单条数");
    True(names.Contains("diana.release.cycle"), "缺少 diana.release.cycle");
    True(names.Contains("diana.worktree.merge"), "缺少 diana.worktree.merge");
    True(names.Contains("diana.trial.load"), "缺少 diana.trial.load");
    True(!names.Contains("diana.release.start"), "不得再登记 diana.release.start，改走 cycle");
    True(!names.Contains("diana.worktree.remove"), "不得再登记 diana.worktree.remove，改走 merge");
    Equal("缺少必填参数: name=", bus.Validate("diana.release.cycle"), "cycle 必须要求 name");
    Equal("缺少必填参数: msg=", bus.Validate("diana.release.cycle name=HistoryJanus"), "cycle 必须要求 msg");
    Equal("缺少必填参数: project=", bus.Validate("diana.worktree.merge"), "merge 必须要求 project");
    Equal("缺少必填参数: name=", bus.Validate("diana.worktree.merge project=2026-020-HistoryJanus"), "merge 必须要求 name");
    Equal("缺少必填参数: path=", bus.Validate("diana.trial.load"), "load 必须要求 path");

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
    True(catalog.Message.Contains("z-Publish", StringComparison.Ordinal),
        "索引必须显示根部 z-Publish 文档区");
    True(!catalog.Message.Contains("current", StringComparison.OrdinalIgnoreCase),
        "索引不得再显示废弃的 current 层");
    True(catalog.Message.Contains("heading=", StringComparison.Ordinal),
        "索引必须提示长文用 heading= 按节读取");
    var listed = await bus.ExecuteAsync("diana.docs.janus", "smoke");
    True(listed.Success && listed.Message.Contains("docs/模块API.md", StringComparison.Ordinal),
        "通道省略 file 时只列出本通道文档");
    var opened = await bus.ExecuteAsync("diana.docs.janus file=docs/模块API.md", "smoke");
    True(opened.Success && opened.Message.Contains("intro", StringComparison.Ordinal),
        "通道按 file 读取 z 内 Markdown");
    var shortName = await bus.ExecuteAsync("diana.docs.janus file=模块API.md", "smoke");
    True(shortName.Success && shortName.Message.Contains("intro", StringComparison.Ordinal),
        "唯一文件名应解析到 catalog 路径");
    var section = await bus.ExecuteAsync("diana.docs.janus file=docs/模块API.md heading=命令", "smoke");
    True(section.Success && section.Message.Contains("command-body", StringComparison.Ordinal),
        "heading 只返回指定一节");
    True(!section.Message.Contains("window-body", StringComparison.Ordinal),
        "heading 不得带回后续章节");
    var outline = await bus.ExecuteAsync("diana.docs.janus file=docs/变更摘要.md", "smoke");
    True(outline.Success && outline.Message.Contains("章节:", StringComparison.Ordinal),
        "超过 12 KiB 未带 heading 只返回目录");
    True(outline.Message.Contains("版本:", StringComparison.Ordinal) &&
        outline.Message.Contains("9.9.9", StringComparison.Ordinal),
        "长文目录必须列出版本号");
    True(!outline.Message.Contains("first-item", StringComparison.Ordinal),
        "目录不得带回版本正文");
    var versionSlice = await bus.ExecuteAsync(
        "diana.docs.janus file=docs/变更摘要.md heading=9.9.9", "smoke");
    True(versionSlice.Success && versionSlice.Message.Contains("first-item", StringComparison.Ordinal),
        "heading=版本号应切出该版本条目");
    True(versionSlice.Message.Contains("continued-line", StringComparison.Ordinal),
        "版本条目应包含续行");
    True(!versionSlice.Message.Contains("other-item", StringComparison.Ordinal),
        "heading=版本号不得带回其他版本");
    var missingHeading = await bus.ExecuteAsync(
        "diana.docs.janus file=docs/变更摘要.md heading=不存在", "smoke");
    True(!missingHeading.Success && missingHeading.Message.Contains("9.9.9", StringComparison.Ordinal),
        "找不到标题时应列出可用版本");
    var escaped = await bus.ExecuteAsync("diana.docs.janus file=../secret.md", "smoke");
    True(!escaped.Success, "通道必须拒绝越出 z 的路径");

    // ui=true 的界面由 Vulcan 前端创建，Diana 这个无窗进程只做中继。没有前端中继时必须
    // 当场失败——静默退化成无界面试用，等于让人对着一份"验收过界面"的结论做决定。
    var cycle = descriptors.Single(descriptor => descriptor.Name == "diana.release.cycle");
    var cycleUi = cycle.Parameters.SingleOrDefault(parameter => parameter.Name == "ui");
    True(cycleUi is not null, "diana.release.cycle 必须提供 ui 参数");
    SequenceEqual(new[] { "true", "false" }, cycleUi!.AllowedValues!, "cycle ui 参数只接受 true/false");

    var load = descriptors.Single(descriptor => descriptor.Name == "diana.trial.load");
    var loadUi = load.Parameters.SingleOrDefault(parameter => parameter.Name == "ui");
    True(loadUi is not null, "diana.trial.load 必须提供 ui 参数");
    SequenceEqual(new[] { "true", "false" }, loadUi!.AllowedValues!, "load ui 参数只接受 true/false");
    Equal("true", loadUi.Default, "trial.load 默认建验收界面");

    var candidateRoot = Path.Combine(temporaryRoot, "candidate", "z-Publish");
    Directory.CreateDirectory(candidateRoot);
    File.Copy(assembly.Location, Path.Combine(candidateRoot, "HistoryDiana.dll"));
    File.WriteAllText(Path.Combine(candidateRoot, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryDiana","version":"9.9.9","artifact":"HistoryDiana.dll","ui":true}
        """);

    True(bus.FrontendExecutor is null, "Smoke 的总线不接前端，才能验中继缺失时的行为");
    var uiTrial = await bus.ExecuteAsync(
        $"diana.trial.load path=\"{candidateRoot}\" alias=smoke-ui", "smoke");
    True(!uiTrial.Success, "没有前端中继时 ui=true 必须失败");
    True(uiTrial.Message.Contains("前端", StringComparison.Ordinal), "失败说明必须点名前端中继");
    var afterFailedUi = await bus.ExecuteAsync("diana.trial.list", "smoke");
    True(afterFailedUi.Message.Contains("当前没有试用中的候选模块", StringComparison.Ordinal),
        "ui=true 失败后不得残留试用条目");

    var janusRoot = Path.Combine(temporaryRoot, "candidate", "janus", "z-Publish");
    Directory.CreateDirectory(janusRoot);
    File.Copy(assembly.Location, Path.Combine(janusRoot, "HistoryJanus.dll"));
    File.WriteAllText(Path.Combine(janusRoot, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":true}
        """);

    var hostSnap = Path.Combine(temporaryRoot, "candidate", "host", "z-Publish");
    Directory.CreateDirectory(hostSnap);
    File.WriteAllText(Path.Combine(hostSnap, "module.manifest.json"),
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Host","name":"HistoryVulcan","version":"9.9.9"}
        """);
    var hostTrial = await DianaTrialCommands.LoadFromPathAsync(
        context, hostSnap, "smoke-host", createUi: false, CancellationToken.None);
    True(!hostTrial.Success, "宿主快照不得 trial.load");
    True(hostTrial.Message.Contains("宿主", StringComparison.Ordinal), "失败必须说明这是宿主不是模块");

    var loadedFormal = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HistoryJanus", "HistoryDiana" };
    var unloadCalls = new List<string>();
    var reloadCount = 0;
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.list",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "列出已加载模块",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ =>
        {
            if (loadedFormal.Count == 0)
                return CommandResult.Ok("当前无已加载模块。");
            return CommandResult.Ok(
                string.Join('\n', loadedFormal.OrderBy(name => name, StringComparer.Ordinal)
                    .Select(name => $"{name} 9.9.9 (1 条指令)")),
                loadedFormal.Select(name => new { ModuleName = name }).ToArray());
        }),
    });
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.unload",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "卸载一个已装载模块",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "模块名", Required = true, Position = 0 },
        ],
        Handler = CommandDescriptor.Sync(context =>
        {
            var name = context.RequireString("name");
            unloadCalls.Add(name);
            loadedFormal.Remove(name);
            return CommandResult.Ok($"已卸载 {name}");
        }),
    });
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.reload",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "重载全部后台模块",
        Handler = CommandDescriptor.Sync(_ =>
        {
            reloadCount++;
            loadedFormal.Add("HistoryJanus");
            loadedFormal.Add("HistoryDiana");
            return CommandResult.Ok("重载完成: 2 个模块");
        }),
    });

    var frontendCalls = new List<string>();
    bus.FrontendExecutor = (text, _, _) =>
    {
        frontendCalls.Add(text);
        return Task.FromResult(CommandResult.Ok("frontend-ok"));
    };

    var dianaUi = await DianaTrialCommands.LoadFromPathAsync(
        context, candidateRoot, "smoke-ui", createUi: true, CancellationToken.None);
    True(dianaUi.Success, "HistoryDiana 候选 ui=true 在有前端时应成功");
    Equal(0, unloadCalls.Count, "不得卸载 HistoryDiana 自己");
    True(frontendCalls.Exists(call => call.StartsWith("vulcan.module.trialui.load", StringComparison.Ordinal)),
        "HistoryDiana ui=true 仍应中继 trialui.load");
    True(!dianaUi.Message.Contains("已先卸载正式模块", StringComparison.Ordinal),
        "跳过卸载时不得声称已卸正式模块");

    var afterDianaUnload = await bus.ExecuteAsync("diana.trial.unload alias=smoke-ui", "smoke");
    True(afterDianaUnload.Success, "卸 HistoryDiana 试用");
    Equal(0, reloadCount, "未腾出正式模块时不得 reload");

    frontendCalls.Clear();
    var janusUi = await bus.ExecuteAsync(
        $"diana.trial.load path=\"{janusRoot}\" alias=smoke-janus", "smoke");
    True(janusUi.Success, "同名正式模块已装载时 ui=true 应先卸再装试用界面");
    SequenceEqual(new[] { "HistoryJanus" }, unloadCalls, "应卸载正式 HistoryJanus");
    True(janusUi.Message.Contains("已先卸载正式模块 HistoryJanus", StringComparison.Ordinal),
        "成功结果应说明已卸正式模块");
    True(frontendCalls.Exists(call => call.StartsWith("vulcan.module.trialui.load", StringComparison.Ordinal)),
        "卸正式模块后仍应中继 trialui.load");
    True(!frontendCalls.Exists(call => call.Contains("vulcan.module.unload", StringComparison.Ordinal)),
        "unload 必须走总线，不得误投前端中继");

    var afterJanusUnload = await bus.ExecuteAsync("diana.trial.unload alias=smoke-janus", "smoke");
    True(afterJanusUnload.Success, "卸 HistoryJanus 试用");
    Equal(1, reloadCount, "腾出位置的试用卸完后应 reload");
    True(afterJanusUnload.Message.Contains("vulcan.module.reload", StringComparison.Ordinal),
        "卸载结果应说明已装回正式模块");

    unloadCalls.Clear();
    loadedFormal.Remove("HistoryJanus");
    frontendCalls.Clear();
    var janusSkip = await DianaTrialCommands.LoadFromPathAsync(
        context, janusRoot, "smoke-janus-skip", createUi: true, CancellationToken.None);
    True(janusSkip.Success, "正式模块未装载时 ui=true 仍应建界面");
    Equal(0, unloadCalls.Count, "正式模块未装载时不得调用 unload");
    True(frontendCalls.Exists(call => call.StartsWith("vulcan.module.trialui.load", StringComparison.Ordinal)),
        "未装载正式模块时仍应中继 trialui.load");
    var afterSkipUnload = await bus.ExecuteAsync("diana.trial.unload alias=smoke-janus-skip", "smoke");
    True(afterSkipUnload.Success, "卸未腾位的试用");
    Equal(1, reloadCount, "未腾位的试用卸完不得再 reload");

    frontendCalls.Clear();
    var orphanRelease = await DianaTrialCommands.ReleaseForWorktreeAsync(
        context, "HistoryJanus", janusRoot, CancellationToken.None);
    True(orphanRelease.Success, "回收工作区前释放试用必须成功");
    True(
        frontendCalls.Exists(call =>
            call.StartsWith("vulcan.module.trialui.unload", StringComparison.Ordinal)
            && call.Contains("HistoryJanus", StringComparison.Ordinal)),
        "Trials 已空时仍须拆除前端试用界面，否则工作区 DLL 删不掉");

    var leakLoaded = await DianaTrialCommands.LoadFromPathAsync(
        context, janusRoot, "smoke-leak", createUi: false, CancellationToken.None);
    True(leakLoaded.Success, "无界面试用应成功");
    True(DianaTrialCommands.HasTrialLoadContext("smoke-leak"), "试用 ALC 应存在");
    True(DianaTrialCommands.DropWithoutUnloadForTests("smoke-leak"), "模拟 Diana 热重载丢掉试用表");
    True(DianaTrialCommands.HasTrialLoadContext("smoke-leak"), "丢掉表之后 ALC 仍应占着 DLL");
    True(DianaTrialCommands.SweepOrphanTrialContexts() >= 1, "应回收残留试用 ALC");
    var leakDll = Path.Combine(janusRoot, "HistoryJanus.dll");
    try
    {
        using var released = File.Open(leakDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException ex)
    {
        throw new InvalidOperationException($"清扫残留 ALC 后必须释放候选 DLL：{ex.Message}");
    }

    var isolatedRegistry = new CommandRegistry();
    var isolatedLog = new TestLog();
    var isolatedBus = new CommandBus(isolatedRegistry, isolatedLog);
    isolatedBus.FrontendExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("frontend-ok"));
    var isolatedSettings = new TestSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["proj.libraryroot"] = temporaryRoot,
    });
    var isolatedContext = new TestModuleContext(isolatedBus, isolatedSettings, isolatedLog, temporaryRoot, isolatedRegistry);
    new HistoryDianaCommands().Attach(isolatedContext);
    isolatedRegistry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.list",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "列出已加载模块",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
            "HistoryJanus 4.0.0 (40 条指令)",
            new[] { new { ModuleName = "HistoryJanus" } })),
    });
    var missingUnload = await DianaTrialCommands.LoadFromPathAsync(
        isolatedContext, janusRoot, "need-unload", createUi: true, CancellationToken.None);
    True(!missingUnload.Success, "宿主没有 vulcan.module.unload 时 ui=true 必须失败");
    True(missingUnload.Message.Contains("3.12.0", StringComparison.Ordinal),
        "失败说明必须点名宿主 3.12.0");
    var afterMissing = await isolatedBus.ExecuteAsync("diana.trial.list", "smoke");
    True(afterMissing.Message.Contains("当前没有试用中的候选模块", StringComparison.Ordinal),
        "缺少 unload 失败后不得残留试用条目");

    Equal(0, assembly.GetTypes().Count(type => type.Name.Contains("ProjectPulse", StringComparison.Ordinal)),
        "程序集不得保留 ProjectPulse 类型");
    Equal(0, assembly.GetTypes().Count(type => type.IsPublic && !type.IsAbstract
        && typeof(IUiModule).IsAssignableFrom(type)), "模块不得注册 UI 生命周期");
}
finally
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    if (Directory.Exists(temporaryRoot))
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 可回收 ALC 可能仍短暂锁着候选 DLL；夹具目录在 %TEMP%，不阻塞断言。
        }
    }
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
