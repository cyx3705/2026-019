using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>Combines bounded read-only observations without owning the development pipeline.</summary>
internal static class DianaObservationCommands
{
    private const int MaximumBaselineLength = 64 * 1024;

    public static void Register(CommandRegistry registry, CommandBus bus)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "diana.host.observe",
            Domain = "HistoryDiana",
            CommandClass = "host",
            Summary = "合并模块、就绪与前端错误快照；带 baseline 可比较实例和指令数变化，不代表开发管线完成",
            Example = "diana.host.observe name=HistoryDiana",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "目标模块完整名称；大小写不敏感", Required = true },
                new ParameterSpec { Name = "baseline", Description = "上次返回的 baselineToken；省略则取基线，最多 64 KiB" },
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var name = context.RequireString("name").Trim();
                if (name.Length == 0)
                    return CommandResult.Fail("name 不得为空");
                var baseline = Decode(context.GetString("baseline"), name);
                // Read the cursor first, so logs emitted while collecting module state are not skipped.
                var log = await Read(bus, "diana.log.read minlevel=error limit=1");
                var modules = await Read(bus, "diana.host.modules");
                var ready = await Read(bus, "diana.host.ready");
                var issues = new List<string>();
                if (modules.Error != null) issues.Add(modules.Error);
                if (ready.Error != null) issues.Add(ready.Error);
                if (log.Error != null) issues.Add(log.Error);
                var current = modules.Data is { } moduleData
                    ? moduleData.GetProperty("modules").EnumerateArray().Select(item => new ModuleState(
                        item.GetProperty("name").GetString()!, item.GetProperty("instanceId").GetString()!,
                        item.GetProperty("commandCount").GetInt32(), item.GetProperty("attached").GetBoolean())).ToArray()
                    : [];
                var target = current.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (modules.Error == null && target == null) issues.Add($"目标模块 {name} 未装载");
                var frontendChanged = false;
                var logsComplete = log.Data is { } initialLog && !initialLog.GetProperty("truncated").GetBoolean();
                JsonElement? errors = null;
                if (baseline != null && log.Data is { } cursor)
                {
                    frontendChanged = cursor.GetProperty("frontendInstanceId").GetString() != baseline.FrontendInstanceId;
                    var query = frontendChanged
                        ? "since=" + CommandParser.QuoteArg(baseline.CapturedAt.ToString("O"))
                        : "after=" + baseline.After.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var delta = await Read(bus, "diana.log.read minlevel=error limit=100 " + query);
                    errors = delta.Data;
                    if (delta.Error != null) issues.Add(delta.Error);
                    logsComplete = delta.Error == null;
                    if (errors is { } data)
                    {
                        if (data.GetProperty("frontendInstanceId").GetString() != cursor.GetProperty("frontendInstanceId").GetString())
                        {
                            issues.Add("观察过程中前端再次变化，请重新取基线；本次日志不完整");
                            logsComplete = false;
                        }
                        if (data.GetProperty("truncated").GetBoolean())
                        {
                            issues.Add("错误结果被截断；用 diana.log.read 的 nextAfter 继续读取");
                            logsComplete = false;
                        }
                        if (!frontendChanged && (data.GetProperty("newestSequence").GetInt64() < baseline.After
                            || data.GetProperty("oldestSequence").GetInt64() > baseline.After + 1))
                        {
                            issues.Add("日志缓冲存在缺口或序号已重置，不能证明期间无错误");
                            logsComplete = false;
                        }
                    }
                    if (frontendChanged)
                    {
                        issues.Add("前端实例已变化，已按基线时间读取；旧前端内存日志无法恢复");
                        logsComplete = false;
                    }
                }
                string? token = null;
                if (modules.Error == null && log.Data is { } checkpoint)
                {
                    var next = new Baseline(1, name, checkpoint.GetProperty("capturedAt").GetDateTimeOffset(),
                        checkpoint.GetProperty("frontendInstanceId").GetString()!,
                        checkpoint.GetProperty("newestSequence").GetInt64(), current);
                    token = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next));
                    if (token.Length > MaximumBaselineLength)
                    {
                        token = null;
                        issues.Add("模块基线超过 64 KiB；请使用单项观察命令");
                    }
                }
                var changes = baseline == null || modules.Error != null ? null : Compare(baseline.Modules, current);
                var oldTarget = baseline?.Modules.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                bool? instanceChanged = oldTarget != null && target != null
                    && oldTarget.InstanceId.Length > 0 && target.InstanceId.Length > 0
                    ? oldTarget.InstanceId != target.InstanceId : null;
                return CommandResult.Ok($"{name} 观察{(baseline == null ? "基线" : "对比")}；{issues.Count} 项提示，管线完成仍须核对 CLI 回执", new
                {
                    schemaVersion = 1,
                    capturedAt = DateTimeOffset.Now,
                    atomic = false,
                    target = name,
                    ready = ready.Data,
                    modules = modules.Data,
                    baselineToken = token,
                    comparison = baseline == null ? null : new { instanceChanged, changes, frontendChanged },
                    logs = baseline == null ? log.Data : errors,
                    logsComplete,
                    issues,
                    pipeline = new { status = "unavailable", reason = "当前只读接口不提供 submit/finish 阶段、等待原因或完成状态；核对 Console CLI 回执，卡住时按宿主手册查看宿主日志。不要停宿主。" },
                });
            }),
        });
    }

    internal static Baseline? Decode(string? token, string target)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token.Length > MaximumBaselineLength) throw new ArgumentException("baseline 超过 64 KiB");
        Baseline? value;
        try { value = JsonSerializer.Deserialize<Baseline>(Convert.FromBase64String(token)); }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new ArgumentException("baseline 必须是上次 observe 返回的 baselineToken", ex); }
        if (value == null || value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.Target)
            || !value.Target.Equals(target, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(value.FrontendInstanceId) || value.After < 0 || value.After == long.MaxValue
            || value.CapturedAt == default || value.CapturedAt > DateTimeOffset.Now
            || value.Modules == null || value.Modules.Any(item => item == null || string.IsNullOrWhiteSpace(item.Name)
                || item.InstanceId == null || item.CommandCount < 0)
            || value.Modules.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Modules.Length)
            throw new ArgumentException("baseline 结构、目标或版本无效；请重新取基线");
        return value;
    }

    internal static Change[] Compare(ModuleState[] before, ModuleState[] after)
    {
        var old = before.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var current = after.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        return old.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            old.TryGetValue(name, out var a);
            current.TryGetValue(name, out var b);
            return new Change(name, a, b, a != null && (b == null || b.CommandCount < a.CommandCount || (a.Attached && !b.Attached)));
        }).Where(item => item.Before != item.After).ToArray();
    }

    private static async Task<(JsonElement? Data, string? Error)> Read(CommandBus bus, string command)
    {
        var result = await bus.ExecuteAsync(command, "diana.host.observe").ConfigureAwait(false);
        if (!result.Success || result.Data == null) return (null, $"{command}: {result.Message}");
        return (JsonSerializer.SerializeToElement(result.Data), null);
    }

    internal sealed record Baseline(int SchemaVersion, string Target, DateTimeOffset CapturedAt, string FrontendInstanceId, long After, ModuleState[] Modules);
    internal sealed record ModuleState(string Name, string InstanceId, int CommandCount, bool Attached);
    internal sealed record Change(string Name, ModuleState? Before, ModuleState? After, bool Regression);
}
