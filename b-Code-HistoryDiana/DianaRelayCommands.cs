using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// MCP 工具中继：按 OneHistory 当前的 MCP 策略实时列举、查看和调用工具。
/// </summary>
/// <remarks>
/// 存在的理由是快照过期问题——AI 会话持有的工具目录是任务开始时的旧快照，
/// 模块热重载后就与实际可见工具不一致。这里每次都现查现调，绕开那份旧快照。
/// </remarks>
internal static class DianaRelayCommands
{
    private const int MaximumArgumentsBytes = 64 * 1024;
    private const int CycleCallTimeoutSeconds = 21 * 60;

    public static void Register(CommandRegistry registry, Func<OhsmcpClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clientFactory);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.relay.list",
            Domain = "HistoryDiana",
            CommandClass = "relay",
            Summary = "实时列出当前 MCP 策略下可见的工具",
            Example = "diana.relay.list filter=diana modulesonly=true",
            Readonly = true,
            Parameters =
            [
                Text("filter", "按工具名或描述包含匹配；留空不过滤", position: 0),
                Bool("modulesonly", "true 只返回模块工具，false 返回全部可见工具", "true"),
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var filter = (context.GetString("filter") ?? "").Trim();
                var modulesOnly = context.GetBool("modulesonly", true);

                using var client = clientFactory();
                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                if (modulesOnly)
                {
                    var moduleNames = await client.ListVisibleModuleToolNamesAsync().ConfigureAwait(false);
                    tools = tools.Where(tool => moduleNames.Contains(tool.Name)).ToList();
                }

                if (filter.Length > 0)
                {
                    tools = tools.Where(tool =>
                            tool.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || tool.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                var ordered = tools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(tool => new { tool.Name, tool.Description })
                    .ToList();
                var text = new StringBuilder(
                    "AI 会话里的工具表可能过期。以本列表为准；没有的工具用 diana.relay.call / describe。\n");
                text.Append($"可见工具 {ordered.Count} 个");
                foreach (var tool in ordered)
                    text.Append($"\n  {tool.Name}");
                return CommandResult.Ok(text.ToString(), new { Count = ordered.Count, Tools = ordered });
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.relay.describe",
            Domain = "HistoryDiana",
            CommandClass = "relay",
            Summary = "查看一个当前可见 MCP 工具的描述与 JSON Schema",
            Example = "diana.relay.describe name=diana_host_modules",
            Readonly = true,
            Parameters = [Text("name", "MCP 工具名", required: true, position: 0)],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var name = RequireToolName(context.RequireString("name"));
                using var client = clientFactory();
                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                var tool = tools.FirstOrDefault(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return tool == null
                    ? CommandResult.Fail($"当前 MCP 策略下没有可见工具: {name}")
                    : CommandResult.Ok(tool.Name, tool);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.relay.call",
            Domain = "HistoryDiana",
            CommandClass = "relay",
            Summary = "按当前 MCP 工具目录调用一个工具，不依赖会话里的旧快照",
            Example = "diana.relay.call name=diana_host_modules argumentsjson={\"name\":\"HistoryDiana\"}",
            Parameters =
            [
                Text("name", "MCP 工具名", required: true, position: 0),
                Text("argumentsjson", "JSON 对象字符串，默认空对象，最大 64 KiB", position: 1),
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var name = RequireToolName(context.RequireString("name"));
                // 自调用会形成中继环，直接拒绝。
                if (name.StartsWith("diana_relay_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DianaRelay_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("ToolRelay_", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Fail("禁止中继自调用");
                }

                var argumentsJson = context.GetString("argumentsjson") is { Length: > 0 } raw ? raw : "{}";
                if (Encoding.UTF8.GetByteCount(argumentsJson) > MaximumArgumentsBytes)
                    return CommandResult.Fail("argumentsjson 超过 64 KiB 上限");

                JsonObject arguments;
                try
                {
                    arguments = JsonNode.Parse(argumentsJson) as JsonObject
                                ?? throw new JsonException("必须是单个 JSON 对象");
                }
                catch (JsonException ex)
                {
                    return CommandResult.Fail($"argumentsjson 不是有效 JSON: {ex.Message}");
                }

                using var client = name.Equals("diana_release_cycle", StringComparison.OrdinalIgnoreCase)
                    ? OhsmcpClient.FromEndpoint(DianaRuntime.ReadMcpEndpoint(), CycleCallTimeoutSeconds)
                    : clientFactory();
                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                var target = tools.FirstOrDefault(tool =>
                    tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return CommandResult.Fail($"当前 MCP 策略下没有可见工具: {name}");

                var result = await client.CallToolAsync(target.Name, arguments).ConfigureAwait(false);
                if (result.TryGetProperty("isError", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.True)
                {
                    return CommandResult.Fail(
                        $"目标工具 {target.Name} 返回失败: {FirstText(result) ?? "未提供错误文本"}");
                }

                return CommandResult.Ok(target.Name, new { Tool = target.Name, Result = result });
            }),
        });
    }

    private static string? FirstText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement)
                && textElement.GetString() is { Length: > 0 } text)
            {
                const int maximumLength = 2048;
                return text.Length <= maximumLength ? text : text[..maximumLength] + "...";
            }
        }

        return null;
    }

    private static string RequireToolName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 64)
            throw new ArgumentException("name 必须是 1~64 字符的 MCP 工具名", nameof(name));
        if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("name 只能包含 ASCII 字母、数字、下划线和连字符", nameof(name));
        return name;
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Bool,
            Default = defaultValue,
        };
}
