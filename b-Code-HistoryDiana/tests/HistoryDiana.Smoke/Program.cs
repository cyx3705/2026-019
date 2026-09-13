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
    var manyHeadingsPath = Path.Combine(channelPackage, "docs", "章节很多.md");
    File.WriteAllText(manyHeadingsPath, string.Join('\n',
        Enumerable.Range(1, 60).Select(index => $"## 章节 {index}\nbody")));
    var channelManifestPath = Path.Combine(channelPackage, "module.manifest.json");
    File.WriteAllText(channelManifestPath,
        """
        {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistoryJanus","version":"9.9.9","artifact":"HistoryJanus.dll","ui":false}
        """);
    File.WriteAllText(Path.Combine(channelPackage, "SHA256SUMS"), string.Join(Environment.NewLine,
        $"{Hash(apiPath)}  docs/模块API.md",
        $"{Hash(changelogPath)}  docs/变更摘要.md",
        $"{Hash(manyHeadingsPath)}  docs/章节很多.md",
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
                 "diana.log.read",
                 "diana.host.modules", "diana.host.ready", "diana.host.observe",
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
    True(opened.Success && JsonSerializer.SerializeToElement(opened.Data).GetProperty("Content").GetString()!.Contains("command-body", StringComparison.Ordinal), "文档按节读取");
    True(!JsonSerializer.SerializeToElement(opened.Data).GetProperty("Content").GetString()!.Contains("window-body", StringComparison.Ordinal), "按节读取不得越界");
    True(!opened.Message.Contains("command-body", StringComparison.Ordinal), "正文只保留在 Data 中");
    var outline = await bus.ExecuteAsync("diana.docs.read domain=janus file=docs/变更摘要.md", "smoke");
    True(outline.Success && outline.Message.Contains("版本:", StringComparison.Ordinal), "长文默认返回目录");
    var versionSlice = await bus.ExecuteAsync("diana.docs.read domain=janus file=变更摘要.md heading=9.9.9", "smoke");
    True(versionSlice.Success && JsonSerializer.SerializeToElement(versionSlice.Data).GetProperty("Content").GetString()!.Contains("first-item", StringComparison.Ordinal), "按版本读取");
    True(!JsonSerializer.SerializeToElement(versionSlice.Data).GetProperty("Content").GetString()!.Contains("other-item", StringComparison.Ordinal), "版本读取不得带入其他版本");
    True(!(await bus.ExecuteAsync("diana.docs.read domain=janus file=../secret.md", "smoke")).Success, "文档路径不得越界");
    var missingHeading = await bus.ExecuteAsync(
        "diana.docs.read domain=janus file=docs/章节很多.md heading=不存在", "smoke");
    True(!missingHeading.Success
         && missingHeading.Message.Contains("前 20/60 项", StringComparison.Ordinal)
         && missingHeading.Message.Length < 2048,
        "无效 heading 的建议必须限长并指向完整目录");

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
