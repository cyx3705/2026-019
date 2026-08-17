using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryDiana;

/// <summary>
/// MCP entry for package hot-reload. Test loads use the same Vulcan install
/// command as cycle, merge, and the Modules page 热重载 button.
/// </summary>
internal static class DianaTrialCommands
{
    public static void Register(CommandRegistry registry, IModuleContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.trial.load",
            Domain = "HistoryDiana",
            CommandClass = "trial",
            Summary = "把候选或历史发布包交给 Vulcan 热重载到运行区",
            Example = @"diana.trial.load path=F:\ai工作区\2026-024-HistoryMinerva\<工作区>\z-Publish\HistoryMinerva-v4.3.18",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "path",
                    Description = "含 manifest 与完整 SHA256SUMS 的候选或历史发布包目录",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = async context => await DianaHotReload.InstallAsync(
                host,
                context.RequireString("path"),
                "host:diana.trial.load",
                context.Cancellation).ConfigureAwait(false),
        });
    }
}
