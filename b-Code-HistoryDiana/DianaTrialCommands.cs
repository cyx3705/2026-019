using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>
/// 候选模块的试用装载：把 AI 工作区里的 z 快照按调用面加载进内存，供人工逐条调用验证。
/// </summary>
/// <remarks>
/// 这条通道刻意不做三件事，否则它就变成了第二套模块系统：
/// ①不写盘——不改 settings、不动模块槽、不碰正式 z，宿主重启后痕迹全无；
/// ②不做发现——只认调用方显式给出的路径，不扫描、不监听、不注册发现根；
/// ③不进宿主实注册表——候选命令登记在本通道私有的 <see cref="CommandRegistry"/> 里，
///   只能经 <c>diana.trial.call</c> 调用，因此不会和正式模块的同名命令抢注册，
///   也不会出现在补全、面板和 MCP 工具表里。
///
/// MCP 默认 <c>ui=true</c>：一条命令完成卸同名正式模块、内存试用和前端验收界面。
/// 只验调用面时显式 <c>ui=false</c>。**界面不在本进程创建**：
/// Diana 住在无窗 <c>--service</c> 后台进程，那里没有 <c>IShellUiRegistrar</c> 也没有 Dispatcher，
/// 而 WPF 对象不可能递过进程边界。候选程序集因此在前端再装一份——由宿主 3.11.4 的
/// <c>vulcan.module.trialui.load/unload</c> 承接，本进程只经前端命令代理把请求递过去。
/// 前端那份同样不进正式模块快照。
///
/// <c>ui=true</c> 时若正式模块已装载，会先执行宿主 3.11.5 的 <c>vulcan.module.unload</c>
/// 卸掉同名正式模块（释放工具窗口 Id），试用结束再 <c>vulcan.module.reload</c> 装回。
/// 不得对 HistoryDiana 自己做这件事，否则本命令会把自己卸掉。
/// Diana 热重载会丢掉静态试用表但可回收 ALC 仍可能锁着 DLL：宿主登记命令时、unload 和 merge
/// 都会清扫残留的 <c>diana.trial:*</c> 以及仍映射磁盘文件的可回收上下文，不需要关宿主。
/// 试用程序集从内存流装载。正在试用装载中的 HistoryDiana 副本不得清扫自己。
///
/// 另需注意：<c>Attach</c> 是候选模块自己的代码，它可能启动监视器、全局快捷键一类的进程级副作用，
/// 这些副作用会和正式模块并存直到 <c>diana.trial.unload</c>。
/// </remarks>
internal static class DianaTrialCommands
{
    private static readonly ConcurrentDictionary<string, TrialModule> Trials =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(CommandRegistry registry, IModuleContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.trial.load",
            Domain = "HistoryDiana",
            CommandClass = "trial",
            Summary = "临时注册候选：先卸同名正式模块，再把指定目录装进内存；默认建验收界面。不改发现根、不写正式槽或 AppData",
            Example = @"diana.trial.load path=F:\ai工作区\2026-024-HistoryMinerva\<工作区>\z-HistoryMinerva",
            Parameters =
            [
                Text("path", "含 module.manifest.json 的目录（工作区 z-* 或候选目录均可）", required: true, position: 0),
                Text("alias", "试用别名，省略时取清单里的模块名"),
                Bool("ui", "是否建验收界面（并先卸同名正式模块）；看一眼时保持默认 true", "true"),
            ],
            Handler = async context => await LoadAsync(
                host,
                context.RequireString("path"),
                context.GetString("alias"),
                context.GetBool("ui", true),
                context.Cancellation).ConfigureAwait(false),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.trial.list",
            Domain = "HistoryDiana",
            CommandClass = "trial",
            Summary = "列出当前已装载的候选模块及其可调用命令",
            Example = "diana.trial.list",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => List()),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.trial.call",
            Domain = "HistoryDiana",
            CommandClass = "trial",
            Summary = "调用候选模块的一条命令并返回其原始结果",
            Example = "diana.trial.call name=janus.proj.list args=\"status=true\"",
            Parameters =
            [
                Text("name", "候选命令全名，例如 janus.proj.list", required: true, position: 0),
                Text("args", "参数串，与控制台写法一致：name=value 空格分隔，值含空格时加引号"),
                Text("alias", "命令重名时用它指定是哪个候选模块"),
            ],
            Handler = async context => await CallAsync(
                context.RequireString("name"),
                context.GetString("args"),
                context.GetString("alias"),
                context.Progress,
                context.Cancellation).ConfigureAwait(false),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.trial.unload",
            Domain = "HistoryDiana",
            CommandClass = "trial",
            Summary = "卸载候选模块，省略别名时全部卸载",
            Example = "diana.trial.unload alias=HistoryJanus",
            Parameters = [Text("alias", "要卸载的试用别名，省略表示全部", position: 0)],
            Handler = async context => await UnloadAsync(
                host, context.GetString("alias"), context.Cancellation).ConfigureAwait(false),
        });

        // 宿主 Diana 热重载会丢掉静态试用表，但 diana.trial:* 可回收 ALC 仍在进程里锁 DLL。
        // 夹具会把 HistoryDiana.dll 再装进试用 ALC；那份副本的静态表是空的，若在这里清扫
        // 会把正在 Attach 的自己卸掉。只让非试用上下文里的宿主副本清扫。
        var selfName = AssemblyLoadContext.GetLoadContext(typeof(DianaTrialCommands).Assembly)?.Name ?? "";
        if (!selfName.StartsWith("diana.trial:", StringComparison.Ordinal))
            SweepOrphanTrialContexts();
    }

    internal static Task<CommandResult> LoadFromPathAsync(
        IModuleContext host,
        string path,
        string? alias,
        bool createUi,
        CancellationToken cancellation)
        => LoadAsync(host, path, alias, createUi, cancellation);

    private static async Task<CommandResult> LoadAsync(
        IModuleContext host,
        string path,
        string? alias,
        bool createUi,
        CancellationToken cancellation)
    {
        string root;
        try
        {
            root = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CommandResult.Fail($"路径无效：{ex.Message}");
        }

        if (!Directory.Exists(root))
            return CommandResult.Fail($"目录不存在：{root}");

        var manifestPath = Path.Combine(root, "module.manifest.json");
        if (!File.Exists(manifestPath))
            return CommandResult.Fail($"不是模块快照目录（缺 module.manifest.json）：{root}");

        string moduleName;
        string moduleVersion;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            moduleName = document.RootElement.TryGetProperty("name", out var name)
                ? name.GetString() ?? "" : "";
            moduleVersion = document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"清单解析失败：{ex.Message}");
        }

        if (moduleName.Length == 0)
            return CommandResult.Fail("清单没有声明 name。");

        if (moduleName.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail(
                "HistoryVulcan 是宿主，不是模块。不能 diana.trial.load。开工作区后用 diana.release.cycle name=HistoryVulcan。");
        }

        var assemblyPath = Path.Combine(root, moduleName + ".dll");
        if (!File.Exists(assemblyPath))
            return CommandResult.Fail($"快照缺少程序集：{assemblyPath}");

        var key = string.IsNullOrWhiteSpace(alias) ? moduleName : alias.Trim();
        if (Trials.ContainsKey(key))
            return CommandResult.Fail($"别名 {key} 已在试用中，先 diana.trial.unload alias={key}。");

        // 界面一律由前端建，Diana 自己不碰 IUiModule。Diana 住在无窗 --service 进程里：
        // 那里没有 IShellUiRegistrar，也没有 Dispatcher，就地 new 一个 WPF 视图只会当场抛。
        // 宿主 3.11.4 起用 vulcan.module.trialui.* 承接这件事，本进程只负责把请求递过去。
        //
        // 这道闸放在装载之前：可回收上下文的回收由 GC 决定，为一个必然失败的请求先把候选
        // 程序集映射进来，会把那份 DLL 锁到下一次 GC——覆盖候选、删工作区都会跟着失败。
        if (createUi && host.Bus.FrontendExecutor == null)
            return CommandResult.Fail("ui=true 需要已连接的 Vulcan 前端；当前没有可用的前端中继。");

        var displacedFormal = false;
        if (createUi)
        {
            var (displaced, fail) = await TryDisplaceFormalModuleAsync(host, moduleName, cancellation)
                .ConfigureAwait(false);
            if (fail != null)
                return fail;
            displacedFormal = displaced;
        }

        var loadContext = new TrialLoadContext(key, assemblyPath);
        try
        {
            var assembly = loadContext.LoadModule(assemblyPath);
            var trialRegistry = new CommandRegistry();
            var trialContext = new TrialModuleContext(host, trialRegistry);

            var attached = 0;
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;
                if (!typeof(IModuleContextAware).IsAssignableFrom(type))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                var instance = (IModuleContextAware)Activator.CreateInstance(type)!;
                instance.Attach(trialContext);
                attached++;
            }

            if (attached == 0)
            {
                loadContext.Unload();
                await RestoreFormalModulesIfIdleAsync(host, displacedFormal, cancellation).ConfigureAwait(false);
                return CommandResult.Fail($"{moduleName} 没有可附着的 IModuleContextAware 类型，无法按调用面试用。");
            }

            var commands = trialRegistry.All().Select(descriptor => descriptor.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();

            // 先占别名再建界面：反过来的话，TryAdd 撞车时前端已经多出一个没人认领、
            // 也没人卸得掉的窗口。
            var trial = new TrialModule(
                key, moduleName, moduleVersion, root, loadContext, trialRegistry, commands, createUi, displacedFormal);
            if (!Trials.TryAdd(key, trial))
            {
                loadContext.Unload();
                await RestoreFormalModulesIfIdleAsync(host, displacedFormal, cancellation).ConfigureAwait(false);
                return CommandResult.Fail($"别名 {key} 已被并发占用。");
            }

            if (createUi)
            {
                var relayResult = await RelayToFrontendAsync(
                    host,
                    $"vulcan.module.trialui.load path={CommandParser.QuoteArg(root)} alias={CommandParser.QuoteArg(key)}",
                    key,
                    cancellation).ConfigureAwait(false);
                if (!relayResult.Success)
                {
                    Trials.TryRemove(key, out _);
                    loadContext.Unload();
                    await RestoreFormalModulesIfIdleAsync(host, displacedFormal, cancellation).ConfigureAwait(false);
                    return CommandResult.Fail($"前端试用界面创建失败：{relayResult.Message}");
                }
            }

            var text = new StringBuilder();
            text.Append($"已试用装载 {moduleName} {moduleVersion}（别名 {key}）：{commands.Count} 条可调用命令");
            text.Append($"\n来源: {root}");
            text.Append("\n仅在内存中，未写盘、未进宿主注册表；用 diana.trial.call 调用，diana.trial.unload 卸载");
            if (displacedFormal)
            {
                text.Append($"\n已先卸载正式模块 {moduleName}，以免工具窗口 Id 冲突；diana.trial.unload 后会重装正式模块");
            }
            foreach (var name in commands.Take(30))
                text.Append($"\n  {name}");
            if (commands.Count > 30)
                text.Append($"\n  ... 另有 {commands.Count - 30} 条，见 diana.trial.list");

            return CommandResult.Ok(text.ToString(), new
            {
                Alias = key,
                Module = moduleName,
                Version = moduleVersion,
                Source = root,
                Commands = commands,
                Ui = createUi ? "frontend" : "disabled",
                DisplacedFormal = displacedFormal,
            });
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or BadImageFormatException
                                      or FileLoadException or TargetInvocationException
                                      or MissingMethodException or TypeLoadException)
        {
            loadContext.Unload();
            await RestoreFormalModulesIfIdleAsync(host, displacedFormal, cancellation).ConfigureAwait(false);
            var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            return CommandResult.Fail($"装载 {moduleName} 失败：{reason}");
        }
    }

    private static CommandResult List()
    {
        if (Trials.IsEmpty)
            return CommandResult.Ok("当前没有试用中的候选模块。");

        var text = new StringBuilder($"试用中的候选模块: {Trials.Count} 个");
        foreach (var trial in Trials.Values.OrderBy(item => item.Alias, StringComparer.Ordinal))
        {
            text.Append($"\n[{trial.Alias}] {trial.ModuleName} {trial.Version}  {trial.Commands.Count} 条命令" +
                        $"  UI={(trial.FrontendUi ? "前端" : "无")}");
            text.Append($"\n  来源: {trial.SourcePath}");
            foreach (var name in trial.Commands)
                text.Append($"\n    {name}");
        }

        return CommandResult.Ok(text.ToString(), Trials.Values.Select(trial => new
        {
            trial.Alias,
            Module = trial.ModuleName,
            trial.Version,
            Source = trial.SourcePath,
            trial.Commands,
            Ui = trial.FrontendUi ? "frontend" : "disabled",
        }).ToList());
    }

    private static async Task<CommandResult> CallAsync(
        string name,
        string? args,
        string? alias,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (Trials.IsEmpty)
            return CommandResult.Fail("当前没有试用中的候选模块，先 diana.trial.load 或 diana.release.cycle。");

        var candidates = Trials.Values
            .Where(trial => string.IsNullOrWhiteSpace(alias)
                            || trial.Alias.Equals(alias.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(trial => trial.Registry.TryGet(name.Trim(), out _))
            .ToList();

        if (candidates.Count == 0)
            return CommandResult.Fail($"试用中的候选模块没有命令 {name}，用 diana.trial.list 查看。");
        if (candidates.Count > 1)
        {
            return CommandResult.Fail(
                $"命令 {name} 在多个候选模块中存在（{string.Join("、", candidates.Select(t => t.Alias))}），用 alias= 指定。");
        }

        var target = candidates[0];
        target.Registry.TryGet(name.Trim(), out var descriptor);

        var (values, error) = BindArguments(descriptor, args);
        if (error != null)
            return CommandResult.Fail(error);

        try
        {
            var context = new CommandContext(descriptor, values, $"diana.trial:{target.Alias}", progress, cancellation);
            var result = await descriptor.Handler(context).ConfigureAwait(false);
            var prefix = result.Success ? "✓" : "✗";
            return CommandResult.Ok(
                $"[{target.Alias}] {descriptor.Name} {prefix} {result.Message}",
                new { trial = target.Alias, command = descriptor.Name, result.Success, result.Message, result.Data });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 候选模块抛出的异常是试用的有效结果，不能让它打穿 Diana。
            return CommandResult.Fail($"[{target.Alias}] {descriptor.Name} 抛出 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把控制台写法的参数串绑定到候选命令的参数表，缺省值按 ParameterSpec 补齐。</summary>
    private static (IReadOnlyDictionary<string, string> Values, string? Error) BindArguments(
        CommandDescriptor descriptor, string? args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();

        foreach (var token in Tokenize(args))
        {
            var separator = token.IndexOf('=');
            if (separator > 0)
                values[token[..separator]] = token[(separator + 1)..];
            else
                positional.Add(token);
        }

        var byPosition = descriptor.Parameters
            .Where(parameter => parameter.Position.HasValue)
            .OrderBy(parameter => parameter.Position!.Value)
            .ToList();
        for (var index = 0; index < positional.Count && index < byPosition.Count; index++)
        {
            if (!values.ContainsKey(byPosition[index].Name))
                values[byPosition[index].Name] = positional[index];
        }

        foreach (var parameter in descriptor.Parameters)
        {
            if (!values.ContainsKey(parameter.Name) && parameter.Default != null)
                values[parameter.Name] = parameter.Default;
        }

        var missing = descriptor.Parameters
            .Where(parameter => parameter.Required && !values.ContainsKey(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToList();
        if (missing.Count > 0)
            return (values, $"缺少必填参数：{string.Join("、", missing)}");

        // 试用工具咽下拼错的参数名，等于让人对着一个没生效的入参看结果。
        var known = descriptor.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = values.Keys.Where(key => !known.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            var accepted = known.Count == 0 ? "该命令不接受参数" : $"可用参数：{string.Join("、", known.OrderBy(k => k, StringComparer.Ordinal))}";
            return (values, $"未知参数：{string.Join("、", unknown)}。{accepted}");
        }

        foreach (var parameter in descriptor.Parameters)
        {
            if (parameter.AllowedValues is { Length: > 0 } allowed
                && values.TryGetValue(parameter.Name, out var value)
                && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return (values, $"参数 {parameter.Name} 只接受：{string.Join("、", allowed)}");
            }
        }

        return (values, null);
    }

    /// <summary>按引号感知切分参数串；引号内的空格保留。</summary>
    private static IEnumerable<string> Tokenize(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;

        var current = new StringBuilder();
        var inQuote = false;
        foreach (var character in args)
        {
            if (character == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (!inQuote && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static async Task<CommandResult> UnloadAsync(
        IModuleContext host,
        string? alias,
        CancellationToken cancellation)
    {
        var targets = string.IsNullOrWhiteSpace(alias)
            ? Trials.Keys.ToList()
            : [alias.Trim()];

        if (targets.Count == 0)
        {
            var orphans = SweepOrphanTrialContexts();
            if (orphans > 0)
            {
                return CommandResult.Ok($"当前没有登记中的试用；已回收 {orphans} 个残留装载上下文。");
            }

            var leftover = DescribeCollectibleContexts();
            return CommandResult.Ok(string.IsNullOrEmpty(leftover)
                ? "当前没有试用中的候选模块。"
                : $"当前没有登记中的试用。仍有可回收装载上下文：{leftover}");
        }

        var unloaded = new List<string>();
        var uiFailures = new List<string>();
        var displacedAny = false;
        foreach (var key in targets)
        {
            if (!Trials.TryRemove(key, out var trial))
                continue;

            // 界面先拆再卸 ALC。前端拆不掉时只记一笔继续走：候选的命令面已经摘除，
            // 把可回收上下文留在进程里换不回那个窗口，只会多留一份泄漏。
            if (trial.FrontendUi)
            {
                var relayResult = await RelayToFrontendAsync(
                    host,
                    $"vulcan.module.trialui.unload alias={CommandParser.QuoteArg(trial.Alias)}",
                    trial.Alias,
                    cancellation).ConfigureAwait(false);
                if (!relayResult.Success)
                {
                    uiFailures.Add($"{trial.Alias}: {relayResult.Message}");
                    host.Log.Warn("diana.trial", $"前端试用界面卸载失败（{trial.Alias}）：{relayResult.Message}");
                }
            }

            displacedAny |= trial.DisplacedFormal;
            trial.LoadContext.Unload();
            unloaded.Add($"{trial.Alias}({trial.ModuleName} {trial.Version})");
        }

        var swept = SweepOrphanTrialContexts();
        if (unloaded.Count == 0)
        {
            return swept == 0
                ? CommandResult.Fail($"没有别名为 {alias} 的试用模块。")
                : CommandResult.Ok($"没有登记中的试用别名 {alias}；已回收 {swept} 个残留装载上下文。");
        }

        var restored = await RestoreFormalModulesIfIdleAsync(host, displacedAny, cancellation)
            .ConfigureAwait(false);

        // 可回收上下文的真正回收由 GC 决定；命令面已经摘除，调用不到了。
        var text = $"已卸载 {unloaded.Count} 个候选模块：{string.Join("、", unloaded)}";
        if (uiFailures.Count > 0)
        {
            text += $"\n前端界面未能拆除（需手工执行 vulcan.module.trialui.unload）：\n  " +
                    string.Join("\n  ", uiFailures);
        }

        if (restored is { Success: true })
            text += "\n已 vulcan.module.reload 装回正式模块";
        else if (restored is { Success: false })
            text += $"\n正式模块未能自动装回（{restored.Message}）；请手工执行 vulcan.module.reload";

        return CommandResult.Ok(text);
    }

    internal static async Task<CommandResult> UnloadMatchingAsync(
        IModuleContext host,
        string? moduleName,
        string? sourcePrefix,
        CancellationToken cancellation)
    {
        string? prefix = null;
        if (!string.IsNullOrWhiteSpace(sourcePrefix))
        {
            try
            {
                prefix = Path.GetFullPath(sourcePrefix.Trim()).TrimEnd('\\', '/')
                         + Path.DirectorySeparatorChar;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                prefix = null;
            }
        }

        var keys = Trials
            .Where(pair =>
            {
                if (!string.IsNullOrWhiteSpace(moduleName)
                    && (pair.Key.Equals(moduleName, StringComparison.OrdinalIgnoreCase)
                        || pair.Value.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (prefix == null)
                    return false;
                try
                {
                    var source = Path.GetFullPath(pair.Value.SourcePath);
                    return source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                           || source.Equals(prefix.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return false;
                }
            })
            .Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
            return CommandResult.Ok("没有需要卸载的试用模块。");

        var parts = new List<string>();
        foreach (var key in keys)
        {
            var result = await UnloadAsync(host, key, cancellation).ConfigureAwait(false);
            parts.Add(result.Message);
        }

        return CommandResult.Ok(string.Join('\n', parts));
    }

    /// <summary>
    /// 回收工作区前释放试用：字典里的候选、前端残留界面，以及可回收 ALC 仍锁着的 DLL。
    /// Diana 热重载会丢掉静态 <see cref="Trials"/>，但前端试用程序集和工作区文件还在。
    /// </summary>
    internal static async Task<CommandResult> ReleaseForWorktreeAsync(
        IModuleContext host,
        string? moduleName,
        string worktreePath,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(host);
        var parts = new List<string>();
        var matching = await UnloadMatchingAsync(host, moduleName, worktreePath, cancellation)
            .ConfigureAwait(false);
        parts.Add(matching.Message);

        if (!string.IsNullOrWhiteSpace(moduleName) && host.Bus.FrontendExecutor != null)
        {
            var alias = moduleName.Trim();
            var relay = await RelayToFrontendAsync(
                    host,
                    $"vulcan.module.trialui.unload alias={CommandParser.QuoteArg(alias)}",
                    alias,
                    cancellation)
                .ConfigureAwait(false);
            parts.Add(relay.Success
                ? $"已拆除前端试用界面 {alias}"
                : $"前端试用界面 {alias} 无需拆除或未能拆除：{relay.Message}");
        }

        CollectTrialAssemblies();
        SweepOrphanTrialContexts();
        return CommandResult.Ok(string.Join('\n', parts));
    }

    internal static int SweepOrphanTrialContexts()
    {
        var live = Trials.Values.Select(trial => (AssemblyLoadContext)trial.LoadContext)
            .ToHashSet();
        var self = AssemblyLoadContext.GetLoadContext(typeof(DianaTrialCommands).Assembly);
        if (self != null)
            live.Add(self);

        var swept = 0;
        foreach (var context in AssemblyLoadContext.All.ToArray())
        {
            if (!context.IsCollectible || live.Contains(context))
                continue;

            var trialNamed = context.Name is { Length: > 0 } name
                             && name.StartsWith("diana.trial:", StringComparison.Ordinal);
            if (!trialNamed && !HoldsFileBackedAssemblies(context))
                continue;

            try
            {
                context.Unload();
                swept++;
            }
            catch (InvalidOperationException)
            {
                // 已在卸载中。
            }
        }

        CollectTrialAssemblies();
        return swept;
    }

    internal static bool DropWithoutUnloadForTests(string alias)
        => Trials.TryRemove(alias, out _);

    internal static bool HasTrialLoadContext(string alias)
        => AssemblyLoadContext.All.Any(context =>
            string.Equals(context.Name, "diana.trial:" + alias, StringComparison.Ordinal));

    internal static void CollectTrialAssemblies()
    {
        for (var round = 0; round < 3; round++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static bool HoldsFileBackedAssemblies(AssemblyLoadContext context)
    {
        try
        {
            return context.Assemblies.Any(assembly => assembly.Location is { Length: > 0 });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string DescribeCollectibleContexts()
    {
        try
        {
            var names = AssemblyLoadContext.All
                .Where(context => context.IsCollectible)
                .Select(context => context.Name ?? "(unnamed)")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            return names.Count == 0 ? "" : string.Join("、", names);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// 把一条命令递给已连接的 Vulcan 前端。
    ///
    /// 走 <see cref="CommandBus.FrontendExecutor"/> 而不是 <c>Bus.ExecuteAsync</c>：后者会先在后台
    /// 注册表里找同名命令，而 <c>vulcan.module.trialui.*</c> 在后台只是一份前端代理，
    /// 绕开注册表直接投递少一层依赖，前端没连上时也能立刻给出确定的失败。
    /// </summary>
    private static async Task<CommandResult> RelayToFrontendAsync(
        IModuleContext host,
        string commandText,
        string alias,
        CancellationToken cancellation)
    {
        var relay = host.Bus.FrontendExecutor;
        if (relay == null)
            return CommandResult.Fail("没有可用的 Vulcan 前端中继。");

        try
        {
            return await relay(commandText, $"diana.trial:{alias}", cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"前端中继异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>ui=true</c> 前若同名正式模块已装载，先走宿主 <c>vulcan.module.unload</c> 释放工具窗口 Id。
    /// 卸 HistoryDiana 自己会把本命令从 MCP 摘掉，因此跳过。
    /// </summary>
    private static async Task<(bool Displaced, CommandResult? Fail)> TryDisplaceFormalModuleAsync(
        IModuleContext host,
        string moduleName,
        CancellationToken cancellation)
    {
        if (moduleName.Equals("HistoryDiana", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        CommandResult listed;
        try
        {
            listed = await host.Bus.ExecuteAsync("vulcan.module.list", "diana.trial", cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, CommandResult.Fail($"无法确认正式模块是否已装载：{ex.Message}"));
        }

        if (!listed.Success)
        {
            return (false, CommandResult.Fail($"无法确认正式模块是否已装载：{listed.Message}"));
        }

        if (!FormalModuleIsLoaded(listed, moduleName))
            return (false, null);

        CommandResult unloaded;
        try
        {
            unloaded = await host.Bus.ExecuteAsync(
                    $"vulcan.module.unload name={CommandParser.QuoteArg(moduleName)}",
                    "diana.trial",
                    cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, CommandResult.Fail($"卸载正式模块 {moduleName} 失败：{ex.Message}"));
        }

        if (unloaded.Success)
            return (true, null);

        if (IsUnknownCommand(unloaded, "vulcan.module.unload"))
        {
            return (false, CommandResult.Fail(
                "ui=true 卸正式模块需要 HistoryVulcan 3.11.5 及以上（vulcan.module.unload）。当前宿主没有这条命令。"));
        }

        if (unloaded.Message.Contains("没有已装载的模块", StringComparison.Ordinal))
            return (false, null);

        return (false, CommandResult.Fail($"卸载正式模块 {moduleName} 失败：{unloaded.Message}"));
    }

    private static bool FormalModuleIsLoaded(CommandResult listed, string moduleName)
    {
        if (listed.Data is System.Collections.IEnumerable rows)
        {
            foreach (var row in rows)
            {
                if (row is null)
                    continue;
                var type = row.GetType();
                var name = type.GetProperty("ModuleName")?.GetValue(row) as string
                           ?? type.GetProperty("Name")?.GetValue(row) as string;
                if (moduleName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        foreach (var line in listed.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Equals(moduleName, StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(moduleName + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnknownCommand(CommandResult result, string commandName)
        => !result.Success
           && result.Message.Contains("未知指令", StringComparison.Ordinal)
           && result.Message.Contains(commandName, StringComparison.Ordinal);

    /// <summary>
    /// 没有仍占用正式模块位置的试用时，把 z 里的正式模块装回来。
    /// 返回 reload 的结果；未发起 reload 时返回 <see langword="null"/>。
    /// </summary>
    private static async Task<CommandResult?> RestoreFormalModulesIfIdleAsync(
        IModuleContext host,
        bool displaced,
        CancellationToken cancellation)
    {
        if (!displaced)
            return null;
        if (Trials.Values.Any(trial => trial.DisplacedFormal))
            return null;

        try
        {
            var restored = await host.Bus.ExecuteAsync("vulcan.module.reload", "diana.trial", cancellation)
                .ConfigureAwait(false);
            if (!restored.Success)
                host.Log.Warn("diana.trial", $"正式模块未能自动装回：{restored.Message}");
            return restored;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            host.Log.Warn("diana.trial", $"正式模块未能自动装回：{ex.Message}");
            return CommandResult.Fail(ex.Message);
        }
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
            AllowedValues = ["true", "false"],
        };

    private sealed record TrialModule(
        string Alias,
        string ModuleName,
        string Version,
        string SourcePath,
        TrialLoadContext LoadContext,
        CommandRegistry Registry,
        IReadOnlyList<string> Commands,
        bool FrontendUi,
        bool DisplacedFormal);

    /// <summary>
    /// 候选模块的可回收装载上下文。
    /// </summary>
    /// <remarks>
    /// 宿主契约程序集必须落回默认上下文：候选模块实现的 <see cref="IModuleContextAware"/>
    /// 必须和 Diana 看到的是同一个类型，否则装载后接口判定恒为 false，试用面永远是空的。
    /// 只有候选自带的私有依赖才从快照目录加载。和宿主一样从内存流装载，避免锁住工作区 DLL。
    /// </remarks>
    private sealed class TrialLoadContext(string alias, string assemblyPath)
        : AssemblyLoadContext($"diana.trial:{alias}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        internal Assembly LoadModule(string path) => LoadFromMemory(path);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 默认上下文已加载的一律共用，宿主契约类型因此保持同一性。
            if (Default.Assemblies.Any(assembly =>
                    string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromMemory(path);
        }

        private Assembly LoadFromMemory(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return LoadFromStream(new MemoryStream(bytes));
        }
    }

    /// <summary>
    /// 交给候选模块的宿主上下文：服务全部转发宿主真身，只把命令登记引到私有注册表。
    /// </summary>
    private sealed class TrialModuleContext(IModuleContext host, CommandRegistry registry) : IModuleContext
    {
        public CommandBus Bus => host.Bus;

        public ISettingsService Settings => host.Settings;

        public IShellLog Log => host.Log;

        public string DataDirectory => host.DataDirectory;

        public void RegisterCommands(Action<CommandRegistry> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            configure(registry);
        }
    }
}
