using System.Diagnostics;
using System.IO;
using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>
/// AI 工作区的命令面：开分支工作树、列出、回收。
/// </summary>
/// <remarks>
/// 位置不硬编码。优先级是 <c>root=</c> 参数 &gt; 设置项 <c>ai.worktreeroot</c> &gt; 默认
/// <see cref="DefaultRoot"/>，因此默认能用、想改随时改、单次想换也不用改设置。
///
/// 分支名与目录名同形：<c>ai/&lt;项目&gt;/&lt;短SHA&gt;-&lt;序号&gt;-&lt;标识&gt;</c>。
/// 带短 SHA 是为了让工作区一眼看出基于哪个提交；带序号是因为同一提交上常并行开多个尝试，
/// 上一轮 Janus 的性能修复和工作区特性就是同一 SHA 的 -1 与 -2。
/// </remarks>
internal static class DianaWorktreeCommands
{
    public const string DefaultRoot = @"F:\ai工作区";
    public const string KeyWorktreeRoot = "ai.worktreeroot";

    public static void Register(CommandRegistry registry, IModuleContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.worktree.root",
            Domain = "HistoryDiana",
            CommandClass = "worktree",
            Summary = "查看或设置 AI 工作区根目录（省略 path 时查询）",
            Example = @"diana.worktree.root path=F:\ai工作区",
            Parameters = [Text("path", "新的根目录绝对路径；省略时只查询", position: 0)],
            Handler = CommandDescriptor.Sync(context => Root(host.Settings, context.GetString("path"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.worktree.create",
            Domain = "HistoryDiana",
            CommandClass = "worktree",
            Summary = "为项目开一个 AI 工作区（git worktree + 新分支）",
            Example = "diana.worktree.create project=2026-020-HistoryJanus slug=cachekey",
            Parameters =
            [
                Text("project", "项目目录名，例如 2026-020-HistoryJanus", required: true, position: 0),
                Text("slug", "本次改动的短标识，只用小写字母数字和连字符", required: true, position: 1),
                Text("root", "本次使用的工作区根，省略时用设置值"),
            ],
            Handler = CommandDescriptor.Sync(context => Create(
                host.Settings,
                context.RequireString("project"),
                context.RequireString("slug"),
                context.GetString("root"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.worktree.list",
            Domain = "HistoryDiana",
            CommandClass = "worktree",
            Summary = "列出某项目已开的 AI 工作区",
            Example = "diana.worktree.list project=2026-020-HistoryJanus",
            Parameters = [Text("project", "项目目录名，省略时列出全部", position: 0)],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => List(host.Settings, context.GetString("project"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.worktree.remove",
            Domain = "HistoryDiana",
            CommandClass = "worktree",
            Summary = "回收一个 AI 工作区；分支保留，不删提交",
            Example = "diana.worktree.remove project=2026-020-HistoryJanus name=71c79b7-2-cachekey",
            Parameters =
            [
                Text("project", "项目目录名", required: true, position: 0),
                Text("name", "工作区目录名", required: true, position: 1),
                Bool("force", "有未提交改动时是否强制移除", "false"),
            ],
            Handler = CommandDescriptor.Sync(context => Remove(
                host.Settings,
                context.RequireString("project"),
                context.RequireString("name"),
                context.GetBool("force"))),
        });
    }

    private static CommandResult Root(ISettingsService settings, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Ok($"AI 工作区根: {ResolveRoot(settings, null)}（默认 {DefaultRoot}）");

        var value = path.Trim();
        if (!Path.IsPathFullyQualified(value))
            return CommandResult.Fail($"必须是绝对路径：{value}");

        settings.Set(KeyWorktreeRoot, value);
        return CommandResult.Ok($"AI 工作区根已设为: {value}");
    }

    private static CommandResult Create(ISettingsService settings, string project, string slug, string? rootOverride)
    {
        var projectName = project.Trim();
        var identifier = slug.Trim().ToLowerInvariant();
        if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return CommandResult.Fail("slug 只能包含小写字母、数字和连字符。");

        var projectPath = Path.Combine(DianaLibraryRoot.Resolve(settings), projectName);
        if (!Directory.Exists(projectPath))
            return CommandResult.Fail($"项目不存在：{projectPath}");
        if (!DianaLibraryRoot.IsGitProject(projectPath))
            return CommandResult.Fail($"不是 git 项目：{projectPath}");

        var (head, headError) = Git(projectPath, "rev-parse", "--short", "HEAD");
        if (headError != null)
            return CommandResult.Fail($"读取 HEAD 失败：{headError}");

        var root = ResolveRoot(settings, rootOverride);
        var projectRoot = Path.Combine(root, projectName);
        Directory.CreateDirectory(projectRoot);

        // 同一提交上常并行开多个尝试，序号取当前已存在的同 SHA 工作区数加一。
        var ordinal = Directory.Exists(projectRoot)
            ? Directory.GetDirectories(projectRoot)
                .Count(dir => Path.GetFileName(dir).StartsWith(head + "-", StringComparison.Ordinal)) + 1
            : 1;
        var name = $"{head}-{ordinal}-{identifier}";
        var worktreePath = Path.Combine(projectRoot, name);
        var branch = $"ai/{projectName}/{name}";

        if (Directory.Exists(worktreePath))
            return CommandResult.Fail($"工作区已存在：{worktreePath}");

        var (_, addError) = Git(projectPath, "worktree", "add", "-b", branch, worktreePath, "HEAD");
        if (addError != null)
            return CommandResult.Fail($"建工作区失败：{addError}");

        var text = new StringBuilder($"已开工作区 {name}");
        text.Append($"\n路径: {worktreePath}");
        text.Append($"\n分支: {branch}（基于 {head}）");
        text.Append($"\n项目: {projectPath}");
        return CommandResult.Ok(text.ToString(), new
        {
            Name = name,
            Path = worktreePath,
            Branch = branch,
            BaseCommit = head,
            Project = projectName,
        });
    }

    private static CommandResult List(ISettingsService settings, string? project)
    {
        var root = ResolveRoot(settings, null);
        if (!Directory.Exists(root))
            return CommandResult.Ok($"工作区根还不存在：{root}");

        var projectDirs = string.IsNullOrWhiteSpace(project)
            ? Directory.GetDirectories(root)
            : Directory.GetDirectories(root).Where(dir =>
                string.Equals(Path.GetFileName(dir), project.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

        var rows = new List<object>();
        var text = new StringBuilder();
        foreach (var projectDir in projectDirs.OrderBy(dir => dir, StringComparer.Ordinal))
        {
            foreach (var worktree in Directory.GetDirectories(projectDir).OrderBy(dir => dir, StringComparer.Ordinal))
            {
                var (status, _) = Git(worktree, "status", "--porcelain");
                var (branch, _) = Git(worktree, "rev-parse", "--abbrev-ref", "HEAD");
                var dirty = !string.IsNullOrWhiteSpace(status);
                text.Append($"\n  {Path.GetFileName(projectDir)}/{Path.GetFileName(worktree),-32} {(dirty ? "×有改动" : "✓干净")}  {branch}");
                rows.Add(new
                {
                    Project = Path.GetFileName(projectDir),
                    Name = Path.GetFileName(worktree),
                    Path = worktree,
                    Branch = branch,
                    Dirty = dirty,
                });
            }
        }

        return CommandResult.Ok(
            rows.Count == 0 ? $"{root} 下没有 AI 工作区。" : $"AI 工作区: {rows.Count} 个（根 {root}）{text}",
            rows);
    }

    private static CommandResult Remove(ISettingsService settings, string project, string name, bool force)
    {
        var root = ResolveRoot(settings, null);
        var worktreePath = Path.Combine(root, project.Trim(), name.Trim());
        if (!Directory.Exists(worktreePath))
            return CommandResult.Fail($"工作区不存在：{worktreePath}");

        var projectPath = Path.Combine(DianaLibraryRoot.Resolve(settings), project.Trim());
        if (!Directory.Exists(projectPath))
            return CommandResult.Fail($"项目不存在：{projectPath}");

        // 分支一律保留：工作区是可再生的，提交不是。
        var arguments = force
            ? new[] { "worktree", "remove", "--force", worktreePath }
            : ["worktree", "remove", worktreePath];
        var (_, error) = Git(projectPath, arguments);
        if (error != null)
        {
            return CommandResult.Fail(force
                ? $"移除失败：{error}"
                : $"移除失败（有未提交改动时加 force=true）：{error}");
        }

        return CommandResult.Ok($"已回收工作区 {name.Trim()}，分支保留未删。", new { Path = worktreePath });
    }

    private static string ResolveRoot(ISettingsService settings, string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride) && Path.IsPathFullyQualified(rootOverride.Trim()))
            return rootOverride.Trim();
        var configured = settings.Get(KeyWorktreeRoot);
        return string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured.Trim();
    }

    private static (string Output, string? Error) Git(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null)
                return ("", "无法启动 git");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return process.ExitCode == 0
                ? (output.Trim(), null)
                : (output.Trim(), string.IsNullOrWhiteSpace(error) ? $"git 退出码 {process.ExitCode}" : error.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ("", ex.Message);
        }
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Required = required, Position = position };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new() { Name = name, Description = description, Type = ParamType.Bool, Default = defaultValue };
}
