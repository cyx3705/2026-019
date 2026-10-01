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
    Directory.CreateDirectory(Path.Combine(worktreeDirectory, "z-Publish"));
    File.WriteAllText(Path.Combine(worktreeDirectory, "z-Publish", "generated.bin"), new string('x', 4096));

    // 2.7.0：MCP 地址经总线问 portunus.mcp.status（宿主 6.0.0 统一契约），不再读 endpoint.json 或 Cursor 配置。
    var previousEndpoint = Environment.GetEnvironmentVariable("HISTORYVULCAN_ENDPOINT");
    var previousLibraryRoot = Environment.GetEnvironmentVariable("HISTORYCLIO_ROOT");
    Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", null);
    Environment.SetEnvironmentVariable("HISTORYCLIO_ROOT", null);
    try
    {
        string? statusUrl = "http://127.0.0.1:8777/mcp";
        var portunus = new TestRegistrar();
        portunus.Register(new CommandDescriptor
        {
            Name = "portunus.mcp.status",
            Summary = "fake",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("status", new { running = statusUrl != null, url = statusUrl })),
        });
        portunus.Register(new CommandDescriptor
        {
            Name = "vulcan.host.info",
            Summary = "fake",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("info", new { libraryRoot = temporaryRoot })),
        });
        var portunusBus = new TestCommandBus(portunus);

        var endpoint = DianaRuntime.ReadMcpEndpoint(portunusBus);
        Equal("http://127.0.0.1:8777/mcp", endpoint.Uri.ToString(), "portunus.mcp.status 给出的 MCP URI");
        Equal<string?>(null, endpoint.AccessToken, "本机 MCP 不持券");
        Equal(Path.GetFullPath(temporaryRoot), DianaRuntime.ResolveProjectLibraryRoot(portunusBus), "项目库根问 vulcan.host.info");

        statusUrl = "https://example.com/mcp";
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(portunusBus), "MCP 端点必须限制为本机回环");

        statusUrl = null;
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(portunusBus), "MCP 未运行必须明确失败");
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(new TestCommandBus(new TestRegistrar())),
            "Portunus 未装载必须明确失败");

        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", "http://127.0.0.1:59095/mcp");
        Equal("http://127.0.0.1:59095/mcp", DianaRuntime.ReadMcpEndpoint(null).Uri.ToString(), "环境变量覆盖");
        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", Path.Combine(temporaryRoot, "endpoint.json"));
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(null), "不再接受 endpoint.json 路径");
        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", "https://127.0.0.1:8777/mcp");
        Throws<InvalidOperationException>(() => DianaRuntime.ReadMcpEndpoint(null), "显式 MCP URL 也必须拒绝 HTTPS");
    }
    finally
    {
        Environment.SetEnvironmentVariable("HISTORYVULCAN_ENDPOINT", previousEndpoint);
        Environment.SetEnvironmentVariable("HISTORYCLIO_ROOT", previousLibraryRoot);
    }

    using (var rows = JsonDocument.Parse(
               "[{\"commandName\":\"diana.view.windows\",\"source\":\"module\"}," +
               "{\"commandName\":\"vulcan.command.list\",\"source\":\"framework:service\"}]"))
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

    var registry = new TestRegistrar();
    var bus = new TestCommandBus(registry);
    var context = new TestModuleContext(bus, registry, Path.Combine(temporaryRoot, "ModuleData", "HistoryDiana"));
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
                 "diana.log.read",
                 "diana.host.modules", "diana.host.ready", "diana.host.observe",
                 "diana.docs.audit", "diana.docs.catalog", "diana.docs.read",
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
        new[] { "docs", "host", "kit", "log", "project", "relay", "view" },
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
    True(descriptors.Single(item => item.Name == "diana.log.read").Readonly,
        "log.read 必须是只读命令");

    // The text tools must preserve real Unicode and literal escape sequences as distinct inputs.
    foreach (var input in new[] { "hello", "中文", @"\u4E2D\u6587", "line1\nline2\"quoted\"" })
    {
        var base64 = await bus.ExecuteAsync("diana.kit.base64 text=" + CommandParser.QuoteArg(input), "smoke");
        True(base64.Success, base64.Message);
        Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)), base64.Data as string, "总线 Base64 必须保留原始文本");
        var digest = await bus.ExecuteAsync("diana.kit.sha256 text=" + CommandParser.QuoteArg(input), "smoke");
        True(digest.Success, digest.Message);
        Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))), digest.Data as string,
            "总线 SHA256 必须保留原始文本");
    }

    var missingLogProvider = await bus.ExecuteAsync("diana.log.read", "smoke");
    True(!missingLogProvider.Success
         && missingLogProvider.Message.Contains("提供者不可用", StringComparison.Ordinal),
        "Aurora 提供者缺失必须明确失败");
    True(!(await bus.ExecuteAsync("diana.log.read minlevel=verbose", "smoke")).Success,
        "非法日志级别必须失败");
    True(!(await bus.ExecuteAsync("diana.log.read since=not-a-time", "smoke")).Success,
        "非法 since 必须失败");
    True(!(await bus.ExecuteAsync("diana.log.read after=-1", "smoke")).Success,
        "非法 after 必须失败");
    True(!(await bus.ExecuteAsync("diana.log.read limit=501", "smoke")).Success,
        "非法 limit 必须失败");

    string? providerMinimumLevel = null;
    string? providerSource = null;
    string? providerKeyword = null;
    long providerAfter = -1;
    registry.Register(new CommandDescriptor
    {
        Name = "aurora.log.snapshot",
        Domain = "aurora",
        CommandClass = "log",
        Summary = "smoke provider",
        Readonly = true,
        AllowUnspecifiedParameters = true,
        Handler = CommandDescriptor.Sync(ctx =>
        {
            providerMinimumLevel = ctx.GetString("minlevel");
            providerSource = ctx.GetString("source");
            providerKeyword = ctx.GetString("keyword");
            _ = long.TryParse(ctx.GetString("after"), out providerAfter);
            if (providerKeyword == "unknown-schema")
                return CommandResult.Ok(data: JsonSerializer.SerializeToElement(new { schemaVersion = 999 }));
            if (providerKeyword == "oversized")
            {
                return CommandResult.Ok(data: JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    frontendInstanceId = "smoke-front",
                    capturedAt = DateTimeOffset.Now,
                    oldestSequence = 1,
                    newestSequence = 41,
                    matchedCount = 1,
                    returnedCount = 1,
                    truncated = false,
                    nextAfter = 41,
                    entries = new[]
                    {
                        new
                        {
                            sequence = 41,
                            timestamp = DateTimeOffset.Now,
                            level = "Error",
                            source = "shell.chrome",
                            message = new string('x', 300_000),
                        },
                    },
                }));
            }

            return CommandResult.Ok(data: JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                frontendInstanceId = "smoke-front",
                capturedAt = DateTimeOffset.Now,
                oldestSequence = 1,
                newestSequence = 50,
                matchedCount = 1,
                returnedCount = 1,
                truncated = false,
                nextAfter = 42,
                entries = new[]
                {
                    new
                    {
                        sequence = 42,
                        timestamp = DateTimeOffset.Now,
                        level = "Error",
                        source = "shell.chrome",
                        message = "unique marker\nstack line",
                    },
                },
            }));
        }),
    });

    var logRead = await bus.ExecuteAsync(
        "diana.log.read minlevel=warn source=SHELL.CHROME keyword=\"unique marker\" after=40 limit=2",
        "smoke");
    True(logRead.Success && logRead.Data is JsonElement,
        $"原生控制台日志必须返回结构化结果: {logRead.Message}");
    Equal("warn", providerMinimumLevel, "最低级别传给生产端");
    Equal("SHELL.CHROME", providerSource, "来源传给生产端");
    Equal("unique marker", providerKeyword, "关键字传给生产端");
    Equal(40L, providerAfter, "after 传给生产端");
    var logPayload = (JsonElement)logRead.Data!;
    Equal("smoke-front", logPayload.GetProperty("frontendInstanceId").GetString(), "前端实例 ID");
    True(logPayload.GetProperty("entries")[0].GetProperty("message").GetString()!.Contains("\nstack line"),
        "多行异常不得拆分");
    True(!(await bus.ExecuteAsync("diana.log.read keyword=unknown-schema", "smoke")).Success,
        "未知提供者结构版本必须失败");
    True(!(await bus.ExecuteAsync("diana.log.read keyword=oversized", "smoke")).Success,
        "超过 256 KiB 的提供者结果必须失败");

    True(descriptors.Single(item => item.Name == "diana.host.modules").Readonly,
        "host.modules 必须是只读命令");
    True(descriptors.Single(item => item.Name == "diana.host.ready").Readonly,
        "host.ready 必须是只读命令");

    var missingHostProvider = await bus.ExecuteAsync("diana.host.modules", "smoke");
    True(!missingHostProvider.Success
         && missingHostProvider.Message.Contains("没有登记", StringComparison.Ordinal),
        "宿主未登记 vulcan.module.list 时必须明确失败");

    // 宿主在进程内返回强类型对象且属性名为 PascalCase：转发要同时扛住未序列化和大小写差异。
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.list",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "smoke host module catalog",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(data: new[]
        {
            new
            {
                ModuleName = "HistoryDiana",
                Version = "2.5.0",
                InstanceId = "smoke-instance",
                CommandCount = 20,
                Attached = true,
                Ui = false,
                AttachFailures = Array.Empty<string>(),
            },
            new
            {
                ModuleName = "HistoryAurora",
                Version = "1.17.0",
                InstanceId = "smoke-aurora",
                CommandCount = 0,
                Attached = false,
                Ui = true,
                AttachFailures = new[] { "创建 UI 模块 HistoryAurora 失败" },
            },
        })),
    });
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.module.ready",
        Domain = "vulcan",
        CommandClass = "module",
        Summary = "smoke host readiness",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(data: false)),
    });

    var hostModules = await bus.ExecuteAsync("diana.host.modules", "smoke");
    True(hostModules.Success, $"宿主装载目录必须可读: {hostModules.Message}");
    var hostPayload = JsonSerializer.SerializeToElement(hostModules.Data);
    Equal(2, hostPayload.GetProperty("moduleCount").GetInt32(), "装载模块数");
    Equal(1, hostPayload.GetProperty("attachedCount").GetInt32(), "已附着模块数");
    Equal(1, hostPayload.GetProperty("attachFailureCount").GetInt32(), "附着失败条数");
    True(hostPayload.GetProperty("modules")[1].GetProperty("attachFailures")[0].GetString()!
            .Contains("创建 UI 模块", StringComparison.Ordinal),
        "附着失败原文必须原样带回，AI 才不用去翻宿主日志");
    True(!hostModules.Message.Contains("AppData", StringComparison.OrdinalIgnoreCase),
        "装载目录不得回显运行槽绝对路径");

    var filteredHost = await bus.ExecuteAsync("diana.host.modules name=historydiana", "smoke");
    True(filteredHost.Success && filteredHost.Message.Contains("2.5.0", StringComparison.Ordinal),
        "name 过滤必须大小写不敏感并给出版本与实例");
    var missingModule = await bus.ExecuteAsync("diana.host.modules name=HistoryGhost", "smoke");
    True(!missingModule.Success && missingModule.Message.Contains("没有装载", StringComparison.Ordinal),
        "未装载的模块必须返回可修正的失败");

    var hostReady = await bus.ExecuteAsync("diana.host.ready", "smoke");
    True(hostReady.Success && hostReady.Message.Contains("尚不完整", StringComparison.Ordinal),
        "装载未完成时必须说明仍在进行，不得被误读成热重载失败");

    await ObservationTests.Run(bus, registry);
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
    True(!JsonSerializer.Serialize(largest.Data).Contains("generated.bin", StringComparison.Ordinal),
        "默认巡检必须排除 z-Publish 生成目录");
    var largestWithGenerated = await bus.ExecuteAsync(
        $"diana.project.largest name={projectName} minMb=0 includeGenerated=true", "smoke");
    True(largestWithGenerated.Success
         && JsonSerializer.Serialize(largestWithGenerated.Data).Contains("generated.bin", StringComparison.Ordinal),
        "includeGenerated=true 必须显式纳入 z-Publish");
    var invalidProject = await bus.ExecuteAsync("diana.project.summary name=..", "smoke");
    True(!invalidProject.Success && invalidProject.Message.Contains("单一目录名", StringComparison.Ordinal),
        "项目巡检必须返回可修正的参数错误");
    True(!(await bus.ExecuteAsync($"diana.project.manifest name={projectName}", "smoke")).Success,
        "缺 manifest 的项目必须明确失败");
    File.WriteAllText(Path.Combine(worktreeDirectory, "project.manifest.json"), "{}");
    var dynamicAlign = await bus.ExecuteAsync("diana.project.align", "smoke");
    True(dynamicAlign.Success && JsonSerializer.SerializeToElement(dynamicAlign.Data).GetProperty("ExpectedProjects")
        .EnumerateArray().Any(item => item.GetString() == projectName), "新增项目自动进入对齐检查，损坏 manifest 作为失败行返回");
    var selectedAlign = await bus.ExecuteAsync($"diana.project.align name={projectName}", "smoke");
    Equal(1, JsonSerializer.SerializeToElement(selectedAlign.Data).GetProperty("Total").GetInt32(), "name 只检查目标项目");
    var invalidHandle = await bus.ExecuteAsync("diana.view.capture handle=invalid", "smoke");
    True(!invalidHandle.Success && invalidHandle.Message.Contains("handle 必须", StringComparison.Ordinal),
        "图形查看器必须返回可修正的句柄错误");

    // 2.8.0：说明书只来自宿主指令目录（注册即文档）。目录替身按宿主 6.0.0 的驼峰 JSON 形状，
    // 把本测试登记口里的真实指令连同一条故意写坏自描述的假模块指令一起交出去。
    True(!(await bus.ExecuteAsync("diana.docs.catalog", "smoke")).Success, "宿主未登记 vulcan.command.list 时必须明确失败");
    registry.Register(new CommandDescriptor
    {
        Name = "janus.proj.broken",
        Domain = "janus",
        CommandClass = "proj",
        Summary = "坏",
        Example = "janus.proj.broken mode=fast extra=1",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "项目", Required = true, Default = "x" },
            new ParameterSpec { Name = "mode", Description = "运行模式，详见模块API", AllowedValues = ["slow", "safe"], Default = "turbo" },
            new ParameterSpec { Name = "dry", Description = "只演练不落盘", Type = ParamType.Bool },
            new ParameterSpec { Name = "path", Description = "目标路径", Position = 1 },
            new ParameterSpec { Name = "level", Description = "级别：low、mid、high" },
        ],
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
    });
    registry.Register(new CommandDescriptor
    {
        Name = "janus.go",
        Domain = "janus",
        Summary = "两段名指令，用来核对说明书分组",
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
    });
    registry.Register(new CommandDescriptor
    {
        Name = "vulcan.command.list",
        Domain = "vulcan",
        CommandClass = "command",
        Summary = "smoke host command catalog",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(data: JsonSerializer.SerializeToElement(
            registry.All().Select(item =>
            {
                var domain = item.Name.Split('.')[0];
                var module = domain is "diana" or "janus";
                return new Dictionary<string, object?>
                {
                    ["commandName"] = item.Name,
                    ["domain"] = domain,
                    ["commandClass"] = item.CommandClass ?? "",
                    ["summary"] = item.Summary,
                    ["example"] = item.Example,
                    ["source"] = module ? "module" : "framework:service",
                    ["sourceDetail"] = domain == "diana" ? "HistoryDiana" : domain == "janus" ? "HistoryJanus" : null,
                    ["dangerous"] = item.Level == CommandLevel.Ask,
                    ["requiresUiThread"] = item.RequiresUiThread,
                    ["readonly"] = item.Readonly,
                    ["hiddenReason"] = item.HiddenReason,
                    ["allowUnspecifiedParameters"] = item.AllowUnspecifiedParameters,
                    ["parameters"] = item.Parameters.Select(parameter => new Dictionary<string, object?>
                    {
                        ["name"] = parameter.Name,
                        ["type"] = parameter.Type.ToString().ToLowerInvariant(),
                        ["required"] = parameter.Required,
                        ["default"] = parameter.Default,
                        ["position"] = parameter.Position,
                        ["allowedValues"] = parameter.AllowedValues ?? [],
                        ["description"] = parameter.Description,
                    }).ToArray(),
                };
            }).ToArray()))),
    });

    var catalog = await bus.ExecuteAsync("diana.docs.catalog", "smoke");
    True(catalog.Success && catalog.Message.Contains("HistoryDiana", StringComparison.Ordinal)
         && catalog.Message.Contains("HistoryJanus", StringComparison.Ordinal)
         && catalog.Message.Contains("docs(3)", StringComparison.Ordinal),
        $"catalog 必须按域列出所属模块与命令类计数: {catalog.Message}");
    True(!catalog.Message.Contains("z-Publish", StringComparison.Ordinal), "catalog 不得再指向 z 文档");

    var domainManual = await bus.ExecuteAsync("diana.docs.read domain=diana class=host", "smoke");
    True(domainManual.Success, domainManual.Message);
    var manualText = JsonSerializer.SerializeToElement(domainManual.Data).GetProperty("manual").GetString()!;
    True(manualText.Contains("### diana.host.modules", StringComparison.Ordinal)
         && manualText.Contains("示例：diana.host.modules name=HistoryDiana", StringComparison.Ordinal)
         && manualText.Contains("- name（string，可省略）", StringComparison.Ordinal),
        $"按类读取必须渲染用途、参数与示例: {manualText}");
    True(!manualText.Contains("diana.kit.", StringComparison.Ordinal), "按类读取不得越出该类");
    True(!domainManual.Message.Contains("###", StringComparison.Ordinal), "正文只保留在 Data 中");

    var single = await bus.ExecuteAsync("diana.docs.read command=diana.log.read", "smoke");
    var singleText = JsonSerializer.SerializeToElement(single.Data).GetProperty("manual").GetString()!;
    True(single.Success && singleText.Contains("默认 error", StringComparison.Ordinal)
         && singleText.Contains("取值 trace|debug|info|warn|error|fatal", StringComparison.Ordinal),
        $"单条读取必须带默认值与取值: {singleText}");
    var writeManual = JsonSerializer.SerializeToElement(
        (await bus.ExecuteAsync("diana.docs.read command=diana.relay.call", "smoke")).Data).GetProperty("manual").GetString()!;
    True(writeManual.Contains("· 写", StringComparison.Ordinal), "写命令必须标出");
    True(!(await bus.ExecuteAsync("diana.docs.read", "smoke")).Success, "domain 与 command 都缺时必须失败");
    var unknownDomain = await bus.ExecuteAsync("diana.docs.read domain=ghost", "smoke");
    True(!unknownDomain.Success && unknownDomain.Message.Contains("diana", StringComparison.Ordinal), "未知域必须列出可用域");
    var unknownClass = await bus.ExecuteAsync("diana.docs.read domain=diana class=ghost", "smoke");
    True(!unknownClass.Success && unknownClass.Message.Contains("docs", StringComparison.Ordinal), "未知类必须列出可用类");
    True(!(await bus.ExecuteAsync("diana.docs.read command=diana.ghost.read", "smoke")).Success, "未知指令必须失败");

    var ownAudit = await bus.ExecuteAsync("diana.docs.audit domain=diana", "smoke");
    True(ownAudit.Success && JsonSerializer.SerializeToElement(ownAudit.Data).GetProperty("findingCount").GetInt32() == 0,
        $"Diana 自己的自描述必须零缺陷: {ownAudit.Message}");
    var brokenAudit = await bus.ExecuteAsync("diana.docs.audit domain=janus", "smoke");
    True(brokenAudit.Success, brokenAudit.Message);
    var rules = JsonSerializer.SerializeToElement(brokenAudit.Data).GetProperty("findings").EnumerateArray()
        .Select(item => item.GetProperty("rule").GetString()!).ToHashSet(StringComparer.Ordinal);
    foreach (var rule in new[]
             {
                 "summary-missing", "parameter-undescribed", "required-with-default", "default-not-allowed",
                 "bool-without-default", "position-gap", "retired-doc-reference", "example-bad-parameter",
                 "example-missing-required", "allowed-values-in-text", "name-two-segment",
             })
    {
        True(rules.Contains(rule), $"audit 必须查出 {rule}: {brokenAudit.Message}");
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

sealed class TestModuleContext(TestCommandBus bus, TestRegistrar registry, string dataDirectory) : IModuleContext
{
    public ICommandBus Bus { get; } = bus;
    public IModuleLog Log { get; } = new TestLog();
    public IModuleEnvironment Environment { get; } = new TestEnvironment(dataDirectory);
    public void RegisterCommands(Action<ICommandRegistrar> configure) => configure(registry);
}

sealed class TestEnvironment(string dataDirectory) : IModuleEnvironment
{
    public string ModuleName => "HistoryDiana";
    public string DataDirectory => dataDirectory;
    public string PackageDirectory => AppContext.BaseDirectory;
    public HostRunMode RunMode => HostRunMode.Probe;
    public string HostVersion => "6.0.0";
}
