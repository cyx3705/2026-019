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
/// 代价是候选模块的 UI 不会被创建（不调用 IUiModule），能验的是命令行为本身。
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
            Summary = "把指定 z 快照目录按调用面装载为候选模块（只进内存，不写盘、不做发现）",
            Example = @"diana.trial.load path=F:\ai工作区\2026-020-HistoryJanus\71c79b7-1-x\z-HistoryJanus",
            Parameters =
            [
                Text("path", "候选 z 快照目录的绝对路径，需含 module.manifest.json", required: true, position: 0),
                Text("alias", "试用别名，省略时取清单里的模块名"),
            ],
            Handler = CommandDescriptor.Sync(context => Load(
                host,
                context.RequireString("path"),
                context.GetString("alias"))),
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
            Handler = CommandDescriptor.Sync(context => Unload(context.GetString("alias"))),
        });
    }

    private static CommandResult Load(IModuleContext host, string path, string? alias)
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
            return CommandResult.Fail($"不是 z 快照目录（缺 module.manifest.json）：{root}");

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

        var assemblyPath = Path.Combine(root, moduleName + ".dll");
        if (!File.Exists(assemblyPath))
            return CommandResult.Fail($"快照缺少程序集：{assemblyPath}");

        var key = string.IsNullOrWhiteSpace(alias) ? moduleName : alias.Trim();
        if (Trials.ContainsKey(key))
            return CommandResult.Fail($"别名 {key} 已在试用中，先 diana.trial.unload alias={key}。");

        var loadContext = new TrialLoadContext(key, assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
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

                // UI 一律不建：这条通道验的是调用面，建窗会把候选模块的窗口混进正式外壳。
                var instance = (IModuleContextAware)Activator.CreateInstance(type)!;
                instance.Attach(trialContext);
                attached++;
            }

            if (attached == 0)
            {
                loadContext.Unload();
                return CommandResult.Fail($"{moduleName} 没有可附着的 IModuleContextAware 类型，无法按调用面试用。");
            }

            var commands = trialRegistry.All().Select(descriptor => descriptor.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();
            var trial = new TrialModule(key, moduleName, moduleVersion, root, loadContext, trialRegistry, commands);
            if (!Trials.TryAdd(key, trial))
            {
                loadContext.Unload();
                return CommandResult.Fail($"别名 {key} 已被并发占用。");
            }

            var text = new StringBuilder();
            text.Append($"已试用装载 {moduleName} {moduleVersion}（别名 {key}）：{commands.Count} 条可调用命令");
            text.Append($"\n来源: {root}");
            text.Append("\n仅在内存中，未写盘、未进宿主注册表；用 diana.trial.call 调用，diana.trial.unload 卸载");
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
            });
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or BadImageFormatException
                                      or FileLoadException or TargetInvocationException
                                      or MissingMethodException or TypeLoadException)
        {
            loadContext.Unload();
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
            text.Append($"\n[{trial.Alias}] {trial.ModuleName} {trial.Version}  {trial.Commands.Count} 条命令");
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
            return CommandResult.Fail("当前没有试用中的候选模块，先 diana.trial.load。");

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

    private static CommandResult Unload(string? alias)
    {
        var targets = string.IsNullOrWhiteSpace(alias)
            ? Trials.Keys.ToList()
            : [alias.Trim()];

        if (targets.Count == 0)
            return CommandResult.Ok("当前没有试用中的候选模块。");

        var unloaded = new List<string>();
        foreach (var key in targets)
        {
            if (!Trials.TryRemove(key, out var trial))
                continue;
            trial.LoadContext.Unload();
            unloaded.Add($"{trial.Alias}({trial.ModuleName} {trial.Version})");
        }

        if (unloaded.Count == 0)
            return CommandResult.Fail($"没有别名为 {alias} 的试用模块。");

        // 可回收上下文的真正回收由 GC 决定；命令面已经摘除，调用不到了。
        return CommandResult.Ok($"已卸载 {unloaded.Count} 个候选模块：{string.Join("、", unloaded)}");
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };

    private sealed record TrialModule(
        string Alias,
        string ModuleName,
        string Version,
        string SourcePath,
        TrialLoadContext LoadContext,
        CommandRegistry Registry,
        IReadOnlyList<string> Commands);

    /// <summary>
    /// 候选模块的可回收装载上下文。
    /// </summary>
    /// <remarks>
    /// 宿主契约程序集必须落回默认上下文：候选模块实现的 <see cref="IModuleContextAware"/>
    /// 必须和 Diana 看到的是同一个类型，否则装载后接口判定恒为 false，试用面永远是空的。
    /// 只有候选自带的私有依赖才从快照目录加载。
    /// </remarks>
    private sealed class TrialLoadContext(string alias, string assemblyPath)
        : AssemblyLoadContext($"diana.trial:{alias}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 默认上下文已加载的一律共用，宿主契约类型因此保持同一性。
            if (Default.Assemblies.Any(assembly =>
                    string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromAssemblyPath(path);
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
