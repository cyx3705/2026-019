using System.Text.Json;
using HistoryDiana;
using HistoryVulcan.Core.Commands;

internal static class ObservationTests
{
    public static async Task Run(CommandBus bus, CommandRegistry registry)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        var originalArguments = JsonSerializer.Serialize(new { name = "HistoryDiana", text = "中文\nC:\\folder\\file", literal = "\\u0022" });
        var transportArguments = JsonSerializer.Serialize(originalArguments)[1..^1];
        Check(DianaRelayCommands.ParseArguments(originalArguments).ToJsonString()
            == DianaRelayCommands.ParseArguments(transportArguments).ToJsonString(), "兼容一层传输转义且不破坏 Unicode、换行与字面反斜杠");
        var slashQuotes = "{\\\"name\\\":\\\"HistoryDiana\\\"}";
        Check(DianaRelayCommands.ParseArguments(slashQuotes)["name"]!.GetValue<string>() == "HistoryDiana", "兼容反斜杠引号");
        foreach (var invalidJson in new[] { "[]", "null", "{broken}", new string('x', 65537) })
        {
            var rejected = false;
            try { DianaRelayCommands.ParseArguments(invalidJson); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "非对象、损坏或超长 JSON 仍须拒绝");
        }
        var missingRegistry = new CommandRegistry();
        var missingBus = new CommandBus(missingRegistry, new TestLog());
        DianaObservationCommands.Register(missingRegistry, missingBus);
        var missing = await missingBus.ExecuteAsync("diana.host.observe name=HistoryDiana", "smoke");
        var missingData = JsonSerializer.SerializeToElement(missing.Data);
        Check(missing.Success && missingData.GetProperty("issues").GetArrayLength() == 3
            && missingData.GetProperty("baselineToken").ValueKind == JsonValueKind.Null
            && !missingData.GetProperty("logsComplete").GetBoolean(), "缺失提供者必须显式报告，不发基线令牌");
        var first = await bus.ExecuteAsync("diana.host.observe name=HistoryDiana", "smoke");
        Check(first.Success, first.Message);
        var data = JsonSerializer.SerializeToElement(first.Data);
        var token = data.GetProperty("baselineToken").GetString()!;
        var decoded = DianaObservationCommands.Decode(token, "historydiana")!;
        Check(decoded.After == 50, "基线必须使用 newestSequence，不能使用筛选后的 nextAfter");
        var second = await bus.ExecuteAsync("diana.host.observe name=HistoryDiana baseline=" + token, "smoke");
        Check(second.Success, second.Message);
        var comparison = JsonSerializer.SerializeToElement(second.Data);
        Check(second.Message.Contains("新增错误 1 条", StringComparison.Ordinal) && comparison.GetProperty("errorCount").GetInt32() == 1,
            "错误必须出现在摘要，不能被 issues 为空掩盖");
        Check(!comparison.GetProperty("comparison").GetProperty("instanceChanged").GetBoolean(), "相同实例不能报重载");
        Check(comparison.GetProperty("pipeline").GetProperty("status").GetString() == "unavailable", "不可把 ready 当作管线进度");
        Check(!(await bus.ExecuteAsync("diana.host.observe name=HistoryDiana baseline=invalid", "smoke")).Success, "坏基线拒绝");
        Check(!(await bus.ExecuteAsync("diana.host.observe name=HistoryGhost baseline=" + token, "smoke")).Success, "跨目标基线拒绝");
        Check(!(await bus.ExecuteAsync("diana.host.observe name=HistoryDiana baseline=" + new string('A', 65537), "smoke")).Success, "超长基线拒绝");
        var changed = DianaObservationCommands.Compare(
            [new("HistoryDiana", "old", 20, true), new("HistoryAurora", "a", 56, true), new("HistoryGone", "g", 1, true)],
            [new("HistoryDiana", "new", 21, true), new("HistoryAurora", "a", 0, false)]);
        Check(changed.Length == 3 && !changed[0].Regression && changed[1].Regression && changed[2].Regression,
            "同版本实例变化、其他模块指令退化和模块消失必须可区分");

        // Force a frontend restart using a valid caller-carried baseline. No stale after may be reused.
        var older = decoded with { FrontendInstanceId = "old-frontend" };
        var olderToken = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(older));
        var restarted = await bus.ExecuteAsync("diana.host.observe name=HistoryDiana baseline=" + olderToken, "smoke");
        var restartData = JsonSerializer.SerializeToElement(restarted.Data);
        Check(restarted.Success && restartData.GetProperty("comparison").GetProperty("frontendChanged").GetBoolean()
            && !restartData.GetProperty("logsComplete").GetBoolean(), "前端变化要显式标记日志不完整");

        var invalid = decoded with { Modules = null! };
        var invalidToken = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invalid));
        Check(!(await bus.ExecuteAsync("diana.host.observe name=HistoryDiana baseline=" + invalidToken, "smoke")).Success,
            "结构不完整基线拒绝");
    }
}
