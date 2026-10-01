using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// 指令说明书：注册即文档。每次现查宿主 <c>vulcan.command.list</c>，把各模块登记时写的
/// Summary / Parameters / Example / 只读 / 确认 / 隐藏原因渲染成可读的说明书。
/// </summary>
/// <remarks>
/// 2.8.0 起 OneHistory 不再维护消费文档（各仓 <c>b-Office/package/模块API.md</c> 与
/// <c>z-Publish/*/docs/</c> 全部退役）。说明书的唯一来源是指令注册时的自描述，
/// 所以自描述写得不够就是说明书不够——<c>diana.docs.audit</c> 专门指出这类缺陷。
/// 不扫描 z-Publish，不读任何 Markdown，新模块装上即可查，无需新增或重载 Diana 命令。
/// </remarks>
internal static class DianaDocsCommands
{
    private const string CatalogCommandName = "vulcan.command.list";
    private const string HostOwner = "HistoryVulcan";
    private const int MinimumSummaryLength = 6;
    private const int MaximumSummaryLength = 120;
    private const int MinimumParameterDescriptionLength = 4;
    private const int MaximumAuditLines = 40;
    // 两段式指令（<域>.<方法>）在宿主目录里没有命令类，说明书里单独归一组。
    private const string TwoSegmentClass = "(两段名)";

    // 指向已退役文档的说法：说明书只剩自描述，再让调用者去「看文档」等于断头路。
    private static readonly Regex RetiredDocumentReference = new(
        @"模块API|消费文档|docs/|z-Publish/.*docs|diana\.docs\.read\s+domain=\S+\s+file=",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 「模式：fast、safe、slow」这类写在说明里的枚举：三个以上英文取值紧跟冒号，用顿号、斜杠、竖线或逗号隔开。
    private static readonly Regex EnumeratedInText = new(
        @"[:：]\s*[A-Za-z][\w-]*(?:\s*[、/|,，]\s*[A-Za-z][\w-]*){2,}",
        RegexOptions.CultureInvariant);

    public static void Register(ICommandRegistrar registry, ICommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.docs.catalog",
            Domain = "HistoryDiana",
            CommandClass = "docs",
            Summary = "列出活宿主全部指令域、所属模块与各类指令数；查任何模块怎么调用都从这里开始",
            Example = "diana.docs.catalog",
            Readonly = true,
            Handler = _ => DianaCommandGuard.RunAsync(async () =>
            {
                var commands = await ReadCatalogAsync(bus).ConfigureAwait(false);
                var domains = commands
                    .GroupBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new DomainEntry(
                        group.Key,
                        group.Select(item => item.Owner).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                        group.Count(),
                        group.Count(item => item.HiddenReason != null),
                        group.GroupBy(item => item.CommandClass, StringComparer.OrdinalIgnoreCase)
                            .Select(classGroup => new ClassEntry(classGroup.Key, classGroup.Count()))
                            .OrderBy(item => item.Name, StringComparer.Ordinal)
                            .ToArray()))
                    .OrderBy(item => item.Domain, StringComparer.Ordinal)
                    .ToList();

                return CommandResult.Ok(RenderCatalog(domains, commands.Count), new
                {
                    commandCount = commands.Count,
                    domains,
                });
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.docs.read",
            Domain = "HistoryDiana",
            CommandClass = "docs",
            Summary = "按域、类或单条指令读取说明书：用途、参数（类型/必填/默认/取值）、示例、只读与确认；内容来自指令注册时的自描述",
            Example = "diana.docs.read domain=janus class=proj",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "domain",
                    Description = "指令域，例如 janus、mercury、vulcan（见 diana.docs.catalog）；给了 command 可省略",
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "class",
                    Description = "只看该域内的一个命令类，例如 proj、log",
                    Position = 1,
                },
                new ParameterSpec
                {
                    Name = "command",
                    Description = "只看一条完整指令名，例如 janus.proj.list",
                },
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var domain = Normalize(context.GetString("domain"));
                var commandClass = Normalize(context.GetString("class"));
                var commandName = Normalize(context.GetString("command"));
                if (domain == null && commandName == null)
                    return CommandResult.Fail("domain 与 command 至少给一个；先执行 diana.docs.catalog 查看全部指令域");

                var commands = await ReadCatalogAsync(bus).ConfigureAwait(false);
                IEnumerable<CatalogCommand> selected = commands;
                if (commandName != null)
                {
                    var match = commands.FirstOrDefault(item =>
                        item.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        var near = commands
                            .Where(item => item.Name.Contains(commandName, StringComparison.OrdinalIgnoreCase)
                                           || commandName.StartsWith(item.Domain + ".", StringComparison.OrdinalIgnoreCase))
                            .Select(item => item.Name)
                            .Take(10)
                            .ToList();
                        return CommandResult.Fail($"活宿主没有登记指令 {commandName}。"
                            + (near.Count > 0 ? $"同域指令: {string.Join("、", near)}" : "先执行 diana.docs.catalog。"));
                    }

                    selected = [match];
                }
                else
                {
                    selected = selected.Where(item => item.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
                    if (!selected.Any())
                    {
                        var known = commands.Select(item => item.Domain)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(name => name, StringComparer.Ordinal);
                        return CommandResult.Fail($"活宿主没有指令域 {domain}。可用域: {string.Join("、", known)}");
                    }

                    if (commandClass != null)
                    {
                        var inDomain = selected.ToList();
                        selected = inDomain.Where(item => item.CommandClass.Equals(commandClass, StringComparison.OrdinalIgnoreCase));
                        if (!selected.Any())
                        {
                            var classes = inDomain.Select(item => item.CommandClass)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(name => name, StringComparer.Ordinal);
                            return CommandResult.Fail($"{domain} 域没有命令类 {commandClass}。可用类: {string.Join("、", classes)}");
                        }
                    }
                }

                var list = selected.OrderBy(item => item.CommandClass, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ToList();
                var scope = commandName ?? (commandClass != null ? $"{domain}.{commandClass}" : domain!);
                var manual = RenderManual(list);
                // 正文只放 Data：MCP 客户端读结构化结果，消息里再放一份会让同一份说明书进对话两次。
                return CommandResult.Ok($"已读取 {scope} 的说明书，{list.Count} 条指令；正文见 data.manual", new
                {
                    scope,
                    commandCount = list.Count,
                    owners = list.Select(item => item.Owner).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    manual,
                });
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.docs.audit",
            Domain = "HistoryDiana",
            CommandClass = "docs",
            Summary = "检查指令自描述的缺陷：缺示例、示例与参数对不上、参数说明缺失、默认值矛盾、引用已退役文档等",
            Example = "diana.docs.audit domain=janus",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "domain",
                    Description = "只检查该指令域；省略检查活宿主全部指令",
                    Position = 0,
                },
            ],
            Handler = context => DianaCommandGuard.RunAsync(async () =>
            {
                var domain = Normalize(context.GetString("domain"));
                var commands = await ReadCatalogAsync(bus).ConfigureAwait(false);
                if (domain != null)
                {
                    commands = commands.Where(item => item.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (commands.Count == 0)
                        return CommandResult.Fail($"活宿主没有指令域 {domain}；先执行 diana.docs.catalog");
                }

                var findings = commands.SelectMany(Audit)
                    .OrderBy(item => item.Command, StringComparer.Ordinal)
                    .ThenBy(item => item.Rule, StringComparer.Ordinal)
                    .ToList();
                var byRule = findings.GroupBy(item => item.Rule, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var byDomain = findings.GroupBy(item => item.Domain, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var affected = findings.Select(item => item.Command).Distinct(StringComparer.Ordinal).Count();

                return CommandResult.Ok(RenderAudit(commands.Count, affected, findings, byRule), new
                {
                    checkedCount = commands.Count,
                    affectedCount = affected,
                    findingCount = findings.Count,
                    byRule,
                    byDomain,
                    findings,
                });
            }),
        });
    }

    private static async Task<List<CatalogCommand>> ReadCatalogAsync(ICommandBus bus)
    {
        // 只读取数走安静执行，不进控制台历史；来源由宿主盖章 module:HistoryDiana。
        var result = await bus.InvokeAsync(CatalogCommandName, "docs").ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message.Contains("未知指令", StringComparison.Ordinal)
                ? $"当前宿主没有登记 {CatalogCommandName}；Diana 2.8.0 需要 HistoryVulcan 6.0.0 或更高版本"
                : $"读取宿主指令目录失败: {result.Message}");
        }

        if (result.Data is null)
            throw new InvalidOperationException($"{CatalogCommandName} 没有返回结构化结果");
        var payload = result.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(result.Data);
        if (payload.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{CatalogCommandName} 返回了不受支持的结构：根节点必须是数组");

        return payload.EnumerateArray().Select(ReadCommand).ToList();
    }

    private static CatalogCommand ReadCommand(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("指令目录条目必须是 JSON 对象");
        var name = Text(row, "commandName")
            ?? throw new InvalidOperationException("指令目录条目缺少 commandName");
        var source = Text(row, "source") ?? "";
        var owner = source.Equals("module", StringComparison.OrdinalIgnoreCase)
            ? Text(row, "sourceDetail") ?? "未知模块"
            : HostOwner;

        var parameters = new List<CatalogParameter>();
        if (Property(row, "parameters", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var allowed = Property(item, "allowedValues", out var values) && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray().Select(value => value.ToString()).Where(value => value.Length > 0).ToArray()
                    : [];
                parameters.Add(new CatalogParameter(
                    Text(item, "name") ?? "",
                    Text(item, "type") ?? "string",
                    Flag(item, "required"),
                    Text(item, "default"),
                    Property(item, "position", out var position) && position.ValueKind == JsonValueKind.Number
                        ? position.GetInt32()
                        : null,
                    allowed,
                    Text(item, "description") ?? ""));
            }
        }

        return new CatalogCommand(
            name,
            Text(row, "domain") ?? name.Split('.')[0],
            Text(row, "commandClass") is { Length: > 0 } commandClass ? commandClass : TwoSegmentClass,
            owner,
            Text(row, "summary") ?? "",
            Text(row, "example"),
            Flag(row, "readonly"),
            Flag(row, "dangerous"),
            Flag(row, "requiresUiThread"),
            Flag(row, "allowUnspecifiedParameters"),
            Text(row, "hiddenReason") is { Length: > 0 } hidden ? hidden : null,
            parameters);
    }

    internal static IEnumerable<AuditFinding> Audit(CatalogCommand command)
    {
        AuditFinding Finding(string rule, string detail) => new(command.Domain, command.Name, rule, detail);

        var summary = command.Summary.Trim();
        if (summary.Length < MinimumSummaryLength)
            yield return Finding("summary-missing", $"Summary 只有「{summary}」，说不清这条指令做什么");
        else if (summary.Length > MaximumSummaryLength)
            yield return Finding("summary-too-long", $"Summary {summary.Length} 字，一句话说明应在 {MaximumSummaryLength} 字内，细节放参数说明");
        if (RetiredDocumentReference.IsMatch(command.Summary))
            yield return Finding("retired-doc-reference", "Summary 引用了已退役的消费文档，说明书只剩自描述");
        if (command.Name.Count(character => character == '.') < 2)
            yield return Finding("name-two-segment", "指令名不是 <域>.<类>.<方法> 三段式，说明书里归不进任何命令类");

        foreach (var parameter in command.Parameters)
        {
            var description = parameter.Description.Trim();
            if (description.Length < MinimumParameterDescriptionLength)
                yield return Finding("parameter-undescribed", $"参数 {parameter.Name} 的说明「{description}」不足以让调用者填值");
            if (parameter.AllowedValues.Length == 0 && EnumeratedInText.IsMatch(description))
                yield return Finding("allowed-values-in-text", $"参数 {parameter.Name} 在说明里列举了取值，却没声明 AllowedValues，总线不会校验");
            if (RetiredDocumentReference.IsMatch(parameter.Description))
                yield return Finding("retired-doc-reference", $"参数 {parameter.Name} 的说明引用了已退役的消费文档");
            if (parameter.Required && parameter.Default != null)
                yield return Finding("required-with-default", $"参数 {parameter.Name} 既是必填又有默认值 {parameter.Default}");
            if (parameter.Type.Equals("bool", StringComparison.OrdinalIgnoreCase) && parameter.Default == null && !parameter.Required)
                yield return Finding("bool-without-default", $"可省略的布尔参数 {parameter.Name} 没写默认值，调用者不知道省略时是 true 还是 false");
            if (parameter.Default != null && parameter.AllowedValues.Length > 0
                && !parameter.AllowedValues.Contains(parameter.Default, StringComparer.OrdinalIgnoreCase))
                yield return Finding("default-not-allowed", $"参数 {parameter.Name} 的默认值 {parameter.Default} 不在取值 {string.Join("/", parameter.AllowedValues)} 中");
        }

        var positions = command.Parameters.Where(item => item.Position.HasValue)
            .Select(item => item.Position!.Value).OrderBy(value => value).ToList();
        if (positions.Where((value, index) => value != index).Any())
            yield return Finding("position-gap", $"位置参数序号应从 0 连续，实际为 {string.Join(",", positions)}");

        var example = command.Example?.Trim();
        if (string.IsNullOrEmpty(example))
        {
            if (command.Parameters.Count > 0)
                yield return Finding("example-missing", "有参数却没有示例，调用者只能猜写法");
            yield break;
        }

        if (RetiredDocumentReference.IsMatch(example))
            yield return Finding("retired-doc-reference", "示例引用了已退役的消费文档");

        ParsedCommand? parsed = null;
        string? syntaxError = null;
        try
        {
            parsed = CommandParser.Parse(example);
        }
        catch (CommandSyntaxException ex)
        {
            syntaxError = ex.Message;
        }

        if (parsed == null)
        {
            yield return Finding("example-unparsable", $"示例不能按指令语法解析: {syntaxError}");
            yield break;
        }

        if (!parsed.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase))
        {
            yield return Finding("example-wrong-command", $"示例调用的是 {parsed.Name}，不是本指令");
            yield break;
        }

        if (command.AllowUnspecifiedParameters)
            yield break;

        var positional = command.Parameters.Where(item => item.Position.HasValue)
            .OrderBy(item => item.Position!.Value).ToList();
        if (parsed.Positionals.Count > positional.Count)
            yield return Finding("example-bad-parameter", $"示例有多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positional.Count))}");

        var bound = new HashSet<string>(
            positional.Take(parsed.Positionals.Count).Select(item => item.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in parsed.Named.Keys)
        {
            var spec = command.Parameters.FirstOrDefault(item => item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                yield return Finding("example-bad-parameter", $"示例用了未声明的参数 {key}=");
                continue;
            }

            bound.Add(spec.Name);
            var value = parsed.Named[key];
            if (spec.AllowedValues.Length > 0 && !spec.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                yield return Finding("example-bad-parameter", $"示例里 {spec.Name}={value} 不在取值 {string.Join("/", spec.AllowedValues)} 中");
        }

        foreach (var required in command.Parameters.Where(item => item.Required && !bound.Contains(item.Name)))
            yield return Finding("example-missing-required", $"示例缺少必填参数 {required.Name}=，照抄会直接失败");
    }

    private static string RenderCatalog(IReadOnlyList<DomainEntry> domains, int total)
    {
        var builder = new StringBuilder();
        builder.Append($"活宿主指令目录（现查 {CatalogCommandName}）：{domains.Count} 个域，{total} 条指令。注册即文档，不再有消费文档。\n");
        builder.Append("看说明书：diana.docs.read domain=<域> [class=<类>]，或 diana.docs.read command=<完整指令名>；查自描述缺陷：diana.docs.audit。").Append('\n');
        foreach (var domain in domains)
        {
            builder.Append($"\n- {domain.Domain,-10} {string.Join("、", domain.Owners)}  {domain.CommandCount} 条");
            if (domain.HiddenCount > 0)
                builder.Append($"（{domain.HiddenCount} 条不对远端暴露）");
            builder.Append("\n    ");
            builder.Append(string.Join("  ", domain.Classes.Select(item => $"{item.Name}({item.Count})")));
        }

        return builder.ToString();
    }

    internal static string RenderManual(IReadOnlyList<CatalogCommand> commands)
    {
        var builder = new StringBuilder();
        string? currentClass = null;
        foreach (var command in commands)
        {
            if (!command.CommandClass.Equals(currentClass, StringComparison.OrdinalIgnoreCase))
            {
                currentClass = command.CommandClass;
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(currentClass == TwoSegmentClass
                    ? $"## {command.Domain} {currentClass}\n"
                    : $"## {command.Domain}.{currentClass}\n");
            }

            builder.Append('\n');
            builder.Append($"### {command.Name}  [{string.Join(" · ", Traits(command))}]").Append('\n');
            builder.Append(command.Summary).Append('\n');
            if (command.HiddenReason != null)
                builder.Append($"远端隐藏：{command.HiddenReason}").Append('\n');
            if (command.AllowUnspecifiedParameters)
                builder.Append("接受任意参数并原样转发。").Append('\n');
            foreach (var parameter in command.Parameters.OrderBy(item => item.Position ?? int.MaxValue))
            {
                var facts = new List<string> { parameter.Type, parameter.Required ? "必填" : "可省略" };
                if (parameter.Default != null)
                    facts.Add($"默认 {parameter.Default}");
                if (parameter.AllowedValues.Length > 0 && !parameter.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
                    facts.Add($"取值 {string.Join("|", parameter.AllowedValues)}");
                if (parameter.Position is { } position)
                    facts.Add($"位置 {position}");
                builder.Append($"- {parameter.Name}（{string.Join("，", facts)}）：{parameter.Description}").Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(command.Example))
                builder.Append($"示例：{command.Example}").Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private static IEnumerable<string> Traits(CatalogCommand command)
    {
        yield return command.Owner;
        yield return command.Readonly ? "只读" : "写";
        if (command.Dangerous)
            yield return "执行前确认";
        if (command.RequiresUiThread)
            yield return "UI 线程";
        if (command.HiddenReason != null)
            yield return "远端隐藏";
    }

    private static string RenderAudit(
        int checkedCount,
        int affected,
        IReadOnlyList<AuditFinding> findings,
        IReadOnlyDictionary<string, int> byRule)
    {
        if (findings.Count == 0)
            return $"已检查 {checkedCount} 条指令，自描述没有发现缺陷。";

        var builder = new StringBuilder(
            $"已检查 {checkedCount} 条指令：{affected} 条有缺陷，共 {findings.Count} 处。");
        builder.Append("\n按规则: ");
        builder.Append(string.Join("、", byRule.Select(item => $"{item.Key} {item.Value}")));
        foreach (var finding in findings.Take(MaximumAuditLines))
            builder.Append($"\n  {finding.Command}  [{finding.Rule}] {finding.Detail}");
        if (findings.Count > MaximumAuditLines)
            builder.Append($"\n  ……另有 {findings.Count - MaximumAuditLines} 处，见 data.findings，或加 domain= 缩小范围");
        return builder.ToString();
    }

    // Some MCP transports preserve JSON Unicode escapes as literal text before
    // converting tool arguments to the command bus. Decode them at the module boundary.
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Regex.Replace(value.Trim(), @"(?:\\|/)u([0-9a-fA-F]{4})", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString(),
            RegexOptions.CultureInvariant);
    }

    /// <summary>宿主 6.0.0 起 Data 是驼峰 JSON；属性名仍按大小写不敏感解析，进程内替身可能是 PascalCase。</summary>
    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? Text(JsonElement element, string name)
        => Property(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name)
        => Property(element, name, out var value) && value.ValueKind == JsonValueKind.True;

    internal sealed record CatalogParameter(
        string Name,
        string Type,
        bool Required,
        string? Default,
        int? Position,
        string[] AllowedValues,
        string Description);

    internal sealed record CatalogCommand(
        string Name,
        string Domain,
        string CommandClass,
        string Owner,
        string Summary,
        string? Example,
        bool Readonly,
        bool Dangerous,
        bool RequiresUiThread,
        bool AllowUnspecifiedParameters,
        string? HiddenReason,
        IReadOnlyList<CatalogParameter> Parameters);

    // Data 与宿主指令一样用驼峰字段名（宿主 6.0.0 约定），消费方按名取字段。
    internal sealed record AuditFinding(
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("rule")] string Rule,
        [property: JsonPropertyName("detail")] string Detail);

    private sealed record ClassEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("count")] int Count);

    private sealed record DomainEntry(
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("owners")] string[] Owners,
        [property: JsonPropertyName("commandCount")] int CommandCount,
        [property: JsonPropertyName("hiddenCount")] int HiddenCount,
        [property: JsonPropertyName("classes")] ClassEntry[] Classes);
}
