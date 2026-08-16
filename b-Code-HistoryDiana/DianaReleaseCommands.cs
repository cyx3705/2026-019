using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>
/// 发布管线的命令面：把 <c>Publish-OneHistoryModule.ps1</c> 作为分离子进程拉起，
/// 让模块发布和其余能力一样原生暴露为 MCP 工具。
/// </summary>
/// <remarks>
/// 关键约束：运行状态只落在日志文件里，不放在本模块的内存里。
/// 管线跑到最后会 <c>vulcan.module.reload</c>，发布 HistoryDiana 时本模块自身会在发布途中
/// 被卸载重载——任何存在静态字段里的运行记录都会随之蒸发。因此 start 立即返回 run 标识，
/// status/log 一律现场读日志目录，Diana 被换掉也不影响追踪。
///
/// 子进程自己把 stdout/stderr 重定向进日志（PowerShell 的 <c>*&gt;</c>），Diana 不做流泵送：
/// 泵送线程会随模块卸载而中断，日志就断在半截。退出码单独落一个纯 ASCII 的
/// <c>.exit</c> 文件，status 据此判断"仍在跑 / 成功 / 失败"，不依赖进程句柄。
/// </remarks>
internal static class DianaReleaseCommands
{
    private const string ExitFileSuffix = ".exit";
    private const string DianaProjectName = "2026-019-HistoryDiana";

    public static void Register(CommandRegistry registry, IModuleContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.release.modules",
            Domain = "HistoryDiana",
            CommandClass = "release",
            Summary = "列出发布登记表里的模块及其项目目录",
            Example = "diana.release.modules",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => Modules(host.Settings)),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.release.status",
            Domain = "HistoryDiana",
            CommandClass = "release",
            Summary = "查看发布运行状态：仍在跑 / 成功 / 失败，附日志末尾",
            Example = "diana.release.status",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "附带的日志末尾行数，范围 1~200", "20"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Status(
                host,
                context.GetString("run"),
                context.GetInt("lines", 20))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.release.log",
            Domain = "HistoryDiana",
            CommandClass = "release",
            Summary = "读取某次发布运行的日志末尾",
            Example = "diana.release.log run=20260815-090000-HistoryMercury lines=80",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "返回的末尾行数，范围 1~500", "80"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Log(
                host,
                context.GetString("run"),
                context.GetInt("lines", 80))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.release.cycle",
            Domain = "HistoryDiana",
            CommandClass = "release",
            Summary = "跑通门禁、写入本树 z 并提交；工作区再卸正式模块做内存试用",
            Example = "diana.release.cycle name=HistoryJanus msg=fix-layout worktree=abc-1-codex-fix",
            Parameters =
            [
                Text("name", "已登记的模块名，见 diana.release.modules", required: true, position: 0),
                Text("msg", "提交说明", required: true, position: 1),
                Text("worktree", "AI 工作区目录名或绝对路径；省略则在主树正式促级后提交"),
                Bool("ui", "工作区试用是否建验收界面（并先卸同名正式模块）", "true"),
            ],
            Handler = async context => await CycleAsync(
                host,
                context.RequireString("name"),
                context.RequireString("msg"),
                context.GetString("worktree"),
                context.GetBool("ui"),
                context.Progress,
                context.Cancellation).ConfigureAwait(false),
        });
    }

    private static CommandResult Modules(ISettingsService settings)
    {
        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return CommandResult.Fail($"找不到发布登记表：{registryPath}");

        List<(string Name, string Kind, string Project)> rows = [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (document.RootElement.TryGetProperty("modules", out var modules))
            {
                foreach (var module in modules.EnumerateArray())
                {
                    rows.Add((
                        module.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        module.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "",
                        module.TryGetProperty("projectDirectory", out var dir) ? dir.GetString() ?? "" : ""));
                }
            }
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"发布登记表解析失败：{ex.Message}");
        }

        rows = rows.Where(row => row.Name.Length > 0).OrderBy(row => row.Name, StringComparer.Ordinal).ToList();
        var text = new StringBuilder($"已登记模块: {rows.Count} 个");
        foreach (var row in rows)
            text.Append($"\n  {row.Name,-18} {row.Kind,-8} {row.Project}");
        text.Append("\n宿主 HistoryVulcan 由管线内置特例配置，不在此表中。");
        return CommandResult.Ok(text.ToString(), rows.Select(row => new { row.Name, row.Kind, row.Project }).ToList());
    }

    private static CommandResult Start(
        IModuleContext host,
        string name,
        bool publish,
        string? worktree,
        string? commitRoot = null,
        string? commitMessage = null)
    {
        var moduleName = name.Trim();
        var registryPath = RegistryPath(host.Settings);
        if (!File.Exists(registryPath))
            return CommandResult.Fail($"找不到发布登记表：{registryPath}");

        bool known;
        var moduleProject = moduleName;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            known = false;
            if (document.RootElement.TryGetProperty("modules", out var modules))
            {
                foreach (var module in modules.EnumerateArray())
                {
                    if (!module.TryGetProperty("name", out var value)
                        || !string.Equals(value.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    known = true;
                    if (module.TryGetProperty("projectDirectory", out var dir) && dir.GetString() is { Length: > 0 } project)
                        moduleProject = project;
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"发布登记表解析失败：{ex.Message}");
        }

        // 宿主是管线里的内置特例，不在登记表里，但确实可发布。
        if (!known && !moduleName.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{moduleName} 不在发布登记表里，见 diana.release.modules。");

        // 工作区不写正式 Clio z：-Publish 只从主树来。无 -Publish 时脚本把候选写入该工作树自己的 z-*。
        string? projectRootOverride = null;
        if (!string.IsNullOrWhiteSpace(worktree))
        {
            if (publish)
                return CommandResult.Fail("worktree 与 publish=true 互斥：正式促级只能从主树构建。");
            projectRootOverride = Path.IsPathFullyQualified(worktree.Trim())
                ? worktree.Trim()
                : Path.Combine(DianaWorktreeCommands.ResolveRootPublic(host.Settings), moduleProject, worktree.Trim());
            if (!Directory.Exists(projectRootOverride))
                return CommandResult.Fail($"工作区不存在：{projectRootOverride}");
        }

        var scriptPath = Path.Combine(DianaProjectRoot(host.Settings), "b-Code", "Publish-OneHistoryModule.ps1");
        if (!File.Exists(scriptPath))
            return CommandResult.Fail($"找不到发布管线脚本：{scriptPath}");

        var logDirectory = LogDirectory(host);
        Directory.CreateDirectory(logDirectory);
        var run = $"{DateTime.Now:yyyyMMdd-HHmmss}-{moduleName}";
        var logPath = Path.Combine(logDirectory, run + ".log");

        // 子进程自己写日志：Diana 被管线热重载时，泵送线程会断，日志就断在半截。
        // 必须用嵌套的 powershell.exe -File 调起管线，不能在本会话里 `& 脚本`：
        // 管线内部会 exit，那会直接终结整个会话，后面的收尾语句一行都跑不到，
        // 实测表现为子进程已退出却没有留下退出码文件。
        var command = new StringBuilder();
        command.Append("& powershell.exe -NoProfile -ExecutionPolicy Bypass -File ");
        command.Append($"{Quote(scriptPath)} -Module {Quote(moduleName)}");
        if (publish)
            command.Append(" -Publish");
        if (projectRootOverride != null)
            command.Append($" -SourceWorktree {Quote(projectRootOverride)}");
        // 退出码单独落一个纯 ASCII 文件，不往日志里追加：`*>` 重定向用的是控制台编码，
        // 再用 Out-File 追加会混进另一种编码，实测哨兵行被写成 UTF-16 而无法解析。
        command.Append($" *> {Quote(logPath)}; ");
        command.Append("$__pipe = $LASTEXITCODE; if ($null -eq $__pipe) { $__pipe = 1 }; ");
        if (!string.IsNullOrWhiteSpace(commitRoot) && !string.IsNullOrWhiteSpace(commitMessage))
        {
            var msgPath = logPath + ".msg";
            File.WriteAllText(msgPath, commitMessage.Trim() + Environment.NewLine, new UTF8Encoding(false));
            command.Append("if ($__pipe -eq 0) { ");
            command.Append($"git -C {Quote(commitRoot)} add -A; ");
            command.Append($"git -C {Quote(commitRoot)} diff --cached --quiet; ");
            command.Append("if ($LASTEXITCODE -ne 0) { ");
            command.Append($"git -C {Quote(commitRoot)} -c i18n.commitEncoding=utf-8 commit -F {Quote(msgPath)}; ");
            command.Append("$__pipe = $LASTEXITCODE ");
            command.Append("} else { Write-Host 'Nothing to commit after pipeline.' } ");
            command.Append("}; ");
        }

        command.Append($"Set-Content -Path {Quote(logPath + ExitFileSuffix)} -Value $__pipe -Encoding ascii");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = DianaProjectRoot(host.Settings),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command.ToString());

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return CommandResult.Fail("发布管线子进程启动失败。");

            var mode = publish
                ? "构建 + 门禁 + 正式促级"
                : projectRootOverride != null
                    ? "构建 + 门禁 + 写入工作区 z"
                    : "只构建候选并跑门禁";
            if (!string.IsNullOrWhiteSpace(commitRoot))
                mode += " + 提交";
            return CommandResult.Ok(
                $"已拉起 {moduleName} 的发布管线（{mode}），run={run}，pid={process.Id}\n"
                + $"日志: {logPath}\n"
                + "管线在独立进程中运行，用 diana.release.status 查看进度；发布 HistoryDiana 时本模块会被热重载，状态仍从日志读取。",
                new { Run = run, Module = moduleName, Publish = publish, Pid = process.Id, Log = logPath });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CommandResult.Fail($"发布管线子进程启动失败：{ex.Message}");
        }
    }

    private static CommandResult Status(IModuleContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 200), out var exitCode, out var finished);
        var runId = Path.GetFileNameWithoutExtension(logPath!);
        var state = !finished
            ? "仍在运行"
            : exitCode == 0 ? "成功" : $"失败（退出码 {exitCode}）";

        var text = new StringBuilder($"[{runId}] {state}");
        text.Append($"\n日志: {logPath}");
        text.Append($"\n--- 末尾 {tail.Count} 行 ---");
        foreach (var line in tail)
            text.Append('\n').Append(line);

        return CommandResult.Ok(text.ToString(), new
        {
            Run = runId,
            Finished = finished,
            ExitCode = exitCode,
            Success = finished && exitCode == 0,
            Log = logPath,
        });
    }

    private static CommandResult Log(IModuleContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 500), out _, out _);
        var text = new StringBuilder($"[{Path.GetFileNameWithoutExtension(logPath!)}] 末尾 {tail.Count} 行");
        foreach (var line in tail)
            text.Append('\n').Append(line);
        return CommandResult.Ok(text.ToString(), new { Log = logPath, Lines = tail });
    }

    private static (string? LogPath, string? Error) ResolveRun(IModuleContext host, string? run)
    {
        var logDirectory = LogDirectory(host);
        if (!Directory.Exists(logDirectory))
            return (null, "还没有任何发布运行记录。");

        if (!string.IsNullOrWhiteSpace(run))
        {
            var named = Path.Combine(logDirectory, run.Trim() + ".log");
            return File.Exists(named) ? (named, null) : (null, $"找不到运行记录 {run.Trim()}。");
        }

        var newest = new DirectoryInfo(logDirectory).GetFiles("*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest == null ? (null, "还没有任何发布运行记录。") : (newest.FullName, null);
    }

    /// <summary>读日志末尾；允许被子进程同时写入，因此以共享模式打开。</summary>
    private static List<string> ReadTail(string path, int lines, out int exitCode, out bool finished)
    {
        exitCode = 0;
        finished = false;
        var all = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                all.Add(line);
        }
        catch (IOException)
        {
            return ["(日志暂时不可读，管线正在写入)"];
        }

        var exitPath = path + ExitFileSuffix;
        if (File.Exists(exitPath))
        {
            try
            {
                finished = int.TryParse(File.ReadAllText(exitPath).Trim(), out var parsed);
                exitCode = finished ? parsed : 0;
            }
            catch (IOException)
            {
            }
        }

        return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
    }

    private static string DianaProjectRoot(ISettingsService settings)
        => Path.Combine(DianaLibraryRoot.Resolve(settings), DianaProjectName);

    private static string RegistryPath(ISettingsService settings)
        => Path.Combine(DianaProjectRoot(settings), "b-Code", "module-publish.manifest.json");

    private static string LogDirectory(IModuleContext host)
        => Path.Combine(host.DataDirectory, "release");

    private static async Task<CommandResult> CycleAsync(
        IModuleContext host,
        string name,
        string message,
        string? worktree,
        bool createUi,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var moduleName = name.Trim();
        var commitMessage = message.Trim();
        if (commitMessage.Length == 0)
            return CommandResult.Fail("msg 不能为空。");

        if (!TryResolveModule(host.Settings, moduleName, out var module))
            return CommandResult.Fail($"{moduleName} 不在发布登记表里，见 diana.release.modules。");

        var mainProject = Path.Combine(DianaLibraryRoot.Resolve(host.Settings), module.ProjectDirectory);
        var isWorktree = !string.IsNullOrWhiteSpace(worktree);
        string repoRoot;
        if (isWorktree)
        {
            if (moduleName.Equals("HistoryDiana", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("HistoryDiana 只在主线 cycle；不要给它传 worktree。");

            repoRoot = DianaWorktreeCommands.ResolveWorktreePath(
                host.Settings, module.ProjectDirectory, worktree!.Trim());
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"工作区不存在：{repoRoot}");
        }
        else
        {
            repoRoot = mainProject;
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"项目主树不存在：{repoRoot}");
        }

        var started = Start(
            host,
            moduleName,
            publish: !isWorktree,
            worktree: isWorktree ? repoRoot : null,
            commitRoot: repoRoot,
            commitMessage: commitMessage);
        if (!started.Success)
            return started;

        var run = ReadRunId(started);
        if (string.IsNullOrWhiteSpace(run))
            return CommandResult.Fail("管线已拉起，但没有返回 run 标识。\n" + started.Message);

        progress?.Report($"已拉起 {run}，等待门禁和提交结束…");
        var (finished, exitCode, tail) = await WaitForRunAsync(host, run, progress, cancellation)
            .ConfigureAwait(false);
        if (!finished)
            return CommandResult.Fail($"cycle 等待结束：{tail}\nrun={run}");
        if (exitCode != 0)
        {
            return CommandResult.Fail(
                $"cycle 失败（退出码 {exitCode}）。提交未执行或未成功。\nrun={run}\n--- 日志末尾 ---\n{tail}");
        }

        var text = new StringBuilder($"cycle 完成：{moduleName} 已门禁通过并提交。\n仓库: {repoRoot}\nrun={run}");
        if (!isWorktree)
        {
            text.Append("\n主树已正式写入 z 并热重载。");
            return CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = (string?)null,
                Run = run,
                Committed = true,
                Trial = (string?)null,
            });
        }

        if (!string.Equals(module.Kind, "module", StringComparison.OrdinalIgnoreCase))
        {
            text.Append("\n宿主候选不走 trial，可继续在工作区开发或 diana.worktree.merge。");
            return CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = repoRoot,
                Run = run,
                Committed = true,
                Trial = (string?)null,
            });
        }

        var snapshot = Path.Combine(repoRoot, module.FormalDirectory);
        progress?.Report($"提交完成，正在试用装载 {snapshot}…");
        await DianaTrialCommands.UnloadMatchingAsync(host, moduleName, snapshot, cancellation)
            .ConfigureAwait(false);
        var trial = await DianaTrialCommands.LoadFromPathAsync(
            host, snapshot, alias: null, createUi, cancellation).ConfigureAwait(false);
        text.Append('\n').Append(trial.Success ? trial.Message : "试用未成功（提交已保留，不回滚）：" + trial.Message);
        text.Append("\n可继续在此工作区开发，或 diana.worktree.merge 并回主线。");
        return trial.Success
            ? CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = repoRoot,
                Run = run,
                Committed = true,
                Trial = snapshot,
            })
            : CommandResult.Fail(text.ToString());
    }

    private static async Task<(bool Finished, int ExitCode, string Tail)> WaitForRunAsync(
        IModuleContext host,
        string run,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var (logPath, error) = ResolveRun(host, run);
            if (error != null)
                return (false, -1, error);

            var tail = ReadTail(logPath!, 40, out var exitCode, out var finished);
            if (finished)
                return (true, exitCode, string.Join('\n', tail));
            if (DateTime.UtcNow >= deadline)
                return (false, -1, "等待超时（20 分钟）。管线可能仍在跑，用 diana.release.status 查看。\n" + string.Join('\n', tail));

            progress?.Report($"[{run}] 仍在运行…");
            await Task.Delay(2000, cancellation).ConfigureAwait(false);
        }
    }

    private static string? ReadRunId(CommandResult started)
    {
        var run = started.Data?.GetType().GetProperty("Run")?.GetValue(started.Data) as string;
        if (!string.IsNullOrWhiteSpace(run))
            return run;
        var match = Regex.Match(started.Message, @"run=([A-Za-z0-9._-]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static bool TryResolveModule(ISettingsService settings, string name, out ReleaseModule module)
    {
        module = default!;
        if (name.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
        {
            module = new ReleaseModule("HistoryVulcan", "host", "2026-023-HistoryVulcan", "z-HistoryVulcan");
            return true;
        }

        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (!document.RootElement.TryGetProperty("modules", out var modules))
                return false;
            foreach (var entry in modules.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var value)
                    || !string.Equals(value.GetString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = entry.TryGetProperty("projectDirectory", out var dir) ? dir.GetString() ?? "" : "";
                var formal = entry.TryGetProperty("formalDirectory", out var formalDir) ? formalDir.GetString() ?? "" : "";
                var kind = entry.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "module" : "module";
                if (project.Length == 0 || formal.Length == 0)
                    return false;
                module = new ReleaseModule(name, kind, project, formal);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static bool TryResolveModuleByProject(ISettingsService settings, string projectDirectory, out ReleaseModule module)
    {
        module = default!;
        if (projectDirectory.Equals("2026-023-HistoryVulcan", StringComparison.OrdinalIgnoreCase))
        {
            module = new ReleaseModule("HistoryVulcan", "host", "2026-023-HistoryVulcan", "z-HistoryVulcan");
            return true;
        }

        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (!document.RootElement.TryGetProperty("modules", out var modules))
                return false;
            foreach (var entry in modules.EnumerateArray())
            {
                if (!entry.TryGetProperty("projectDirectory", out var dir)
                    || !string.Equals(dir.GetString(), projectDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var moduleName = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var formal = entry.TryGetProperty("formalDirectory", out var formalDir) ? formalDir.GetString() ?? "" : "";
                var kind = entry.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "module" : "module";
                if (moduleName.Length == 0 || formal.Length == 0)
                    return false;
                module = new ReleaseModule(moduleName, kind, projectDirectory, formal);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal sealed record ReleaseModule(string Name, string Kind, string ProjectDirectory, string FormalDirectory);

    /// <summary>PowerShell 单引号字符串：内部单引号翻倍转义。</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Required = required, Position = position };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Bool,
            Default = defaultValue,
            AllowedValues = ["true", "false"],
        };

    private static ParameterSpec Int(string name, string description, string defaultValue)
        => new() { Name = name, Description = description, Type = ParamType.Int, Default = defaultValue };
}
