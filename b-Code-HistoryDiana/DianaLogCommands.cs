using System.Globalization;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

internal static class DianaLogCommands
{
    private const int MaximumResultBytes = 256 * 1024;
    private static readonly HashSet<string> Levels =
        new(["trace", "debug", "info", "warn", "error", "fatal"], StringComparer.OrdinalIgnoreCase);

    public static void Register(CommandRegistry registry, CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.log.read",
            Domain = "HistoryDiana",
            CommandClass = "log",
            Summary = "读取当前前端控制台的结构化内存日志",
            Example = "diana.log.read minlevel=error source=shell.chrome limit=100",
            Readonly = true,
            Parameters =
            [
                Text("minlevel", "最低级别：trace/debug/info/warn/error/fatal", defaultValue: "error",
                    allowedValues: ["trace", "debug", "info", "warn", "error", "fatal"]),
                Text("source", "规范化来源的大小写不敏感精确匹配"),
                Text("keyword", "对来源和完整消息做大小写不敏感包含匹配"),
                Text("since", "ISO 8601 时间，只返回该时刻及之后的记录"),
                Text("after", "只返回该单调序号之后的记录"),
                Int("limit", "返回条数，范围 1~500", "100"),
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var minimumLevel = (context.GetString("minlevel") ?? "error").Trim();
                if (!Levels.Contains(minimumLevel))
                    return CommandResult.Fail($"未知日志级别: {minimumLevel}");

                var limit = context.GetInt("limit", 100);
                if (limit is < 1 or > 500)
                    return CommandResult.Fail("limit 必须在 1~500 之间");

                DateTimeOffset? since = null;
                if (Normalize(context.GetString("since")) is { } sinceText)
                {
                    if (!DateTimeOffset.TryParse(
                            sinceText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                            out var parsedSince))
                    {
                        return CommandResult.Fail("since 必须是有效的 ISO 8601 时间");
                    }
                    since = parsedSince;
                }

                long? after = null;
                if (Normalize(context.GetString("after")) is { } afterText)
                {
                    if (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAfter)
                        || parsedAfter < 0)
                    {
                        return CommandResult.Fail("after 必须是非负整数");
                    }
                    after = parsedAfter;
                }

                var providerCommand = BuildProviderCommand(
                    minimumLevel,
                    Normalize(context.GetString("source")),
                    Normalize(context.GetString("keyword")),
                    since,
                    after,
                    limit);
                var provider = await bus.ExecuteAsync(providerCommand, "diana.log.read").ConfigureAwait(false);
                if (!provider.Success)
                {
                    var unavailable = provider.Message.Contains("未知指令", StringComparison.OrdinalIgnoreCase)
                                      || provider.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
                    return CommandResult.Fail(unavailable
                        ? "当前 Aurora 控制台日志提供者不可用；请确认前端已附着且不是正在热重载"
                        : $"读取 Aurora 控制台日志失败: {provider.Message}");
                }

                if (provider.Data is not JsonElement payload)
                    return CommandResult.Fail("Aurora 控制台日志提供者返回了不受支持的响应结构");

                if (!TryValidate(payload, after.HasValue, out var validated, out var error))
                    return CommandResult.Fail($"Aurora 控制台日志提供者响应无效: {error}");

                var returnedCount = validated.GetProperty("returnedCount").GetInt32();
                var matchedCount = validated.GetProperty("matchedCount").GetInt32();
                var instanceId = validated.GetProperty("frontendInstanceId").GetString();
                return CommandResult.Ok(
                    $"前端 {instanceId} 的控制台日志匹配 {matchedCount} 条，返回 {returnedCount} 条",
                    validated);
            }),
        });
    }

    private static string BuildProviderCommand(
        string minimumLevel,
        string? source,
        string? keyword,
        DateTimeOffset? since,
        long? after,
        int limit)
    {
        var command = new StringBuilder("aurora.log.snapshot");
        Append(command, "minlevel", minimumLevel);
        if (source != null)
            Append(command, "source", source);
        if (keyword != null)
            Append(command, "keyword", keyword);
        if (since.HasValue)
            Append(command, "since", since.Value.ToString("O", CultureInfo.InvariantCulture));
        if (after.HasValue)
            Append(command, "after", after.Value.ToString(CultureInfo.InvariantCulture));
        Append(command, "limit", limit.ToString(CultureInfo.InvariantCulture));
        return command.ToString();
    }

    private static void Append(StringBuilder command, string name, string value)
        => command.Append(' ').Append(name).Append('=').Append(CommandParser.QuoteArg(value));

    private static bool TryValidate(
        JsonElement payload,
        bool incremental,
        out JsonElement validated,
        out string error)
    {
        validated = default;
        error = "";
        if (payload.ValueKind != JsonValueKind.Object)
            return Fail("根节点必须是 JSON 对象", out error);
        if (!TryInt32(payload, "schemaVersion", out var schemaVersion) || schemaVersion != 1)
            return Fail("schemaVersion 必须为 1", out error);
        if (!TryText(payload, "frontendInstanceId", out var instanceId) || instanceId.Length == 0)
            return Fail("frontendInstanceId 缺失", out error);
        if (!TryText(payload, "capturedAt", out var capturedAt)
            || !DateTimeOffset.TryParse(capturedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return Fail("capturedAt 不是 ISO 8601 时间", out error);
        }
        if (!TryNonNegativeInt64(payload, "oldestSequence", out var oldest)
            || !TryNonNegativeInt64(payload, "newestSequence", out var newest)
            || !TryNonNegativeInt64(payload, "nextAfter", out var nextAfter)
            || !TryNonNegativeInt32(payload, "matchedCount", out var matchedCount)
            || !TryNonNegativeInt32(payload, "returnedCount", out var returnedCount))
        {
            return Fail("序号和计数字段必须是非负整数", out error);
        }
        if ((oldest == 0) != (newest == 0) || (oldest > 0 && oldest > newest))
            return Fail("oldestSequence/newestSequence 范围无效", out error);
        if (!payload.TryGetProperty("truncated", out var truncated)
            || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Fail("truncated 必须是布尔值", out error);
        }
        if (!payload.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return Fail("entries 必须是数组", out error);
        if (entries.GetArrayLength() != returnedCount || returnedCount > matchedCount)
            return Fail("returnedCount/matchedCount 与 entries 不一致", out error);

        long? previous = null;
        var maximumReturnedSequence = 0L;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryNonNegativeInt64(entry, "sequence", out var sequence)
                || sequence == 0
                || !TryText(entry, "timestamp", out var timestamp)
                || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || !TryText(entry, "level", out var level)
                || !Levels.Contains(level)
                || !TryText(entry, "source", out _)
                || !TryText(entry, "message", out _))
            {
                return Fail("entries 含无效记录", out error);
            }

            if (previous.HasValue
                && (incremental ? sequence <= previous.Value : sequence >= previous.Value))
            {
                return Fail(incremental ? "增量结果必须按序号正序" : "最近结果必须按序号倒序", out error);
            }
            previous = sequence;
            maximumReturnedSequence = Math.Max(maximumReturnedSequence, sequence);
        }
        if (maximumReturnedSequence > 0 && nextAfter != maximumReturnedSequence)
            return Fail("nextAfter 必须等于本次返回的最大序号", out error);
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaximumResultBytes)
            return Fail("结构化结果超过 256 KiB", out error);

        validated = payload.Clone();
        return true;
    }

    private static bool TryText(JsonElement value, string name, out string text)
    {
        text = "";
        return value.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.String
               && (text = property.GetString() ?? "") != null;
    }

    private static bool TryInt32(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.TryGetInt32(out number);
    }

    private static bool TryNonNegativeInt32(JsonElement value, string name, out int number)
        => TryInt32(value, name, out number) && number >= 0;

    private static bool TryNonNegativeInt64(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.TryGetInt64(out number) && number >= 0;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ParameterSpec Text(
        string name,
        string description,
        string? defaultValue = null,
        string[]? allowedValues = null)
        => new()
        {
            Name = name,
            Description = description,
            Default = defaultValue,
            AllowedValues = allowedValues,
        };

    private static ParameterSpec Int(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Int,
            Default = defaultValue,
        };
}
