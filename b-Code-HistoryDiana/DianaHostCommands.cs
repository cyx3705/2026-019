using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// 运行宿主观察：把宿主自己的只读装载状态整理成 AI 可核对的结构化结果。
/// </summary>
/// <remarks>
/// 宿主的 <c>vulcan.module.*</c> 整类都不投影到 MCP（写操作按策略隐藏，只读的 list/ready
/// 被一起挡在外面），而《模块开发手册》第四节的热重载判据恰好要读这两条。缺了这个通道，
/// AI 每一轮都得跳出 MCP 去跑 Console CLI。这一类只做进程内转发和形状校验：不复制宿主的
/// 装载实现，不碰运行包，也不参与已冻结的 <c>vulcan.dev.*</c> 管线。
/// </remarks>
internal static class DianaHostCommands
{
    public static void Register(CommandRegistry registry, CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.host.modules",
            Domain = "HistoryDiana",
            CommandClass = "host",
            Summary = "读取活宿主当前装载的模块、版本、实例 ID、指令数与附着失败",
            Example = "diana.host.modules name=HistoryDiana",
            Readonly = true,
            Parameters =
            [
                Text("name", "只返回该模块，大小写不敏感精确匹配；省略返回全部"),
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var filter = Normalize(context.GetString("name"));
                var (payload, failure) = await TryReadAsync(bus, "vulcan.module.list", "diana.host.modules")
                    .ConfigureAwait(false);
                if (failure != null)
                    return CommandResult.Fail(failure);

                if (payload.ValueKind != JsonValueKind.Array)
                    return CommandResult.Fail("宿主模块目录返回了不受支持的响应结构：根节点必须是数组");

                var modules = new List<ModuleSnapshot>();
                foreach (var element in payload.EnumerateArray())
                {
                    if (!TryReadModule(element, out var module, out var error))
                        return CommandResult.Fail($"宿主模块目录响应无效: {error}");
                    if (filter != null
                        && !string.Equals(module.Name, filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    modules.Add(module);
                }

                if (filter != null && modules.Count == 0)
                {
                    return CommandResult.Fail(
                        $"活宿主当前没有装载 {filter}；先确认 vulcan.dev.submit 的回执含「已热重载到活宿主」，"
                        + "再用不带 name 的 diana.host.modules 看完整目录");
                }

                var attached = modules.Count(item => item.Attached);
                var failures = modules.Sum(item => item.AttachFailures.Count);
                var message = filter != null
                    ? $"{modules[0].Name} {modules[0].Version} 实例 {modules[0].InstanceId}，"
                      + $"{modules[0].CommandCount} 条指令，{(modules[0].Attached ? "已附着" : "未附着")}"
                    : $"活宿主装载 {modules.Count} 个模块，{attached} 个已附着"
                      + (failures > 0 ? $"，{failures} 条附着失败" : "");

                return CommandResult.Ok(message, new
                {
                    capturedAt = DateTimeOffset.Now,
                    moduleCount = modules.Count,
                    attachedCount = attached,
                    attachFailureCount = failures,
                    modules = modules.Select(item => new
                    {
                        name = item.Name,
                        version = item.Version,
                        instanceId = item.InstanceId,
                        commandCount = item.CommandCount,
                        attached = item.Attached,
                        ui = item.Ui,
                        attachFailures = item.AttachFailures,
                    }).ToArray(),
                });
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.host.ready",
            Domain = "HistoryDiana",
            CommandClass = "host",
            Summary = "查询本轮模块装载是否已经全部接上活宿主",
            Example = "diana.host.ready",
            Readonly = true,
            Handler = _ => DianaCommandGuard.RunAsync(async () =>
            {
                var (payload, failure) = await TryReadAsync(bus, "vulcan.module.ready", "diana.host.ready")
                    .ConfigureAwait(false);
                if (failure != null)
                    return CommandResult.Fail(failure);

                if (payload.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return CommandResult.Fail("宿主装载就绪查询返回了不受支持的响应结构：必须是布尔值");

                var ready = payload.ValueKind == JsonValueKind.True;
                return CommandResult.Ok(
                    ready
                        ? "本轮模块装载已全部接上活宿主"
                        : "模块目录尚不完整；装载仍在进行，稍后重查，不要据此判定热重载失败",
                    new { ready });
            }),
        });
    }

    /// <summary>转发一条宿主只读指令，并把结果统一成 <see cref="JsonElement"/>。</summary>
    private static async Task<(JsonElement Payload, string? Failure)> TryReadAsync(
        CommandBus bus,
        string command,
        string source)
    {
        var result = await bus.ExecuteAsync(command, source).ConfigureAwait(false);
        if (!result.Success)
        {
            var unavailable = result.Message.Contains("未知指令", StringComparison.OrdinalIgnoreCase)
                              || result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
            return (default, unavailable
                ? $"当前宿主没有登记 {command}；请确认运行的是 HistoryVulcan 5.1.2 或更高版本"
                : $"读取宿主装载状态失败: {result.Message}");
        }

        if (result.Data is null)
            return (default, $"{command} 没有返回结构化结果");

        // 宿主在进程内返回的是强类型对象，只有经 CLI/MCP 才被序列化；两种形态都要能读。
        var payload = result.Data is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(result.Data);
        return (payload, null);
    }

    private static bool TryReadModule(JsonElement element, out ModuleSnapshot module, out string error)
    {
        module = default!;
        error = "";
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "模块条目必须是 JSON 对象";
            return false;
        }

        if (!TryText(element, "moduleName", out var name) || name.Length == 0)
        {
            error = "模块条目缺少 moduleName";
            return false;
        }

        if (!TryText(element, "version", out var version))
            version = "";
        if (!TryText(element, "instanceId", out var instanceId))
            instanceId = "";
        if (!TryProperty(element, "commandCount", out var commandCountElement)
            || commandCountElement.ValueKind != JsonValueKind.Number
            || !commandCountElement.TryGetInt32(out var commandCount)
            || commandCount < 0)
        {
            error = $"模块 {name} 的 commandCount 无效";
            return false;
        }

        var attached = TryProperty(element, "attached", out var attachedElement)
                       && attachedElement.ValueKind == JsonValueKind.True;
        var ui = TryProperty(element, "ui", out var uiElement)
                 && uiElement.ValueKind == JsonValueKind.True;

        var failures = new List<string>();
        if (TryProperty(element, "attachFailures", out var failureElement)
            && failureElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failureElement.EnumerateArray())
            {
                var text = failure.ValueKind == JsonValueKind.String
                    ? failure.GetString()
                    : failure.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    failures.Add(text!);
            }
        }

        module = new ModuleSnapshot(name, version, instanceId, commandCount, attached, ui, failures);
        return true;
    }

    /// <summary>宿主两侧的序列化命名策略不一定一致，属性名按大小写不敏感解析。</summary>
    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryText(JsonElement element, string name, out string text)
    {
        text = "";
        if (!TryProperty(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString() ?? "";
        return true;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ParameterSpec Text(string name, string description)
        => new()
        {
            Name = name,
            Description = description,
        };

    private sealed record ModuleSnapshot(
        string Name,
        string Version,
        string InstanceId,
        int CommandCount,
        bool Attached,
        bool Ui,
        IReadOnlyList<string> AttachFailures);
}
