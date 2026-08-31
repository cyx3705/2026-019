using BaseVariable;

namespace HistoryDiana;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryDiana";
    public override string Description => "前端图形查看、AI 工作区巡检、z 文档通道与 MCP 中继";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("HistoryDiana 程序集未携带版本信息。");

    // 命令由 IModuleContext.RegisterCommands 显式登记，避免旧反射前缀形成两段式命令。
    public override Type? MainClassType => null;
}
