using System.Security.Cryptography;
using System.Text;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// 小工具集：哈希、编码、标识与时间。都是无副作用的一行计算，合并为一个 <c>kit</c> 类
/// 而不是按 text / identity / time 各开一个只有一两条命令的类。
/// </summary>
internal static class DianaKitCommands
{
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(Readonly(
            "diana.kit.sha256",
            "计算文本的 SHA-256（十六进制大写）",
            "diana.kit.sha256 text=hello",
            context => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(context.RequireString("text")))),
            Text("text", "要哈希的文本", required: true, position: 0)));

        registry.Register(Readonly(
            "diana.kit.base64",
            "把 UTF-8 文本编码为 Base64",
            "diana.kit.base64 text=hello",
            context => Convert.ToBase64String(
                Encoding.UTF8.GetBytes(context.RequireString("text"))),
            Text("text", "要编码的文本", required: true, position: 0)));

        registry.Register(Readonly(
            "diana.kit.guid",
            "生成一个新 GUID",
            "diana.kit.guid",
            _ => Guid.NewGuid().ToString()));

        registry.Register(Readonly(
            "diana.kit.now",
            "当前本地时间与 Unix 秒",
            "diana.kit.now",
            _ => new
            {
                local = DateTime.Now,
                unix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            }));
    }

    private static CommandDescriptor Readonly(
        string name,
        string summary,
        string example,
        Func<CommandContext, object?> handler,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            Domain = "HistoryDiana",
            CommandClass = "kit",
            Summary = summary,
            Example = example,
            Readonly = true,
            Parameters = parameters,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(data: handler(context))),
        };

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };
}
