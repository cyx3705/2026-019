using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// 模块运行时要用的外部事实：项目库根与 MCP 地址。
/// </summary>
/// <remarks>
/// 2.7.0 起一律经总线问（宿主 6.0.0 统一契约）：项目库根问 <c>vulcan.host.info</c>，
/// MCP 地址问 <c>portunus.mcp.status</c>。此前读 Cursor 的 <c>mcp.json</c>、Portunus 写的 <c>endpoint.json</c>，
/// 项目库根写死缺省——都是绕开总线的文件约定，别的模块一改布局这里就静默失配。
/// 环境变量覆盖仍保留（测试与排障用），只接受值本身，不再接受文件路径。
/// </remarks>
internal static class DianaRuntime
{
    private const string DefaultProjectLibraryRoot = @"C:\OneHistory\HistoryClio";

    /// <summary>项目库根：<c>HISTORYCLIO_ROOT</c> → <c>vulcan.host.info</c> 的 <c>libraryRoot</c> → 缺省。</summary>
    public static string ResolveProjectLibraryRoot(ICommandBus? bus)
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYCLIO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        var fromHost = bus == null ? null : ReadString(bus, "vulcan.host.info", "libraryRoot");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(fromHost) ? DefaultProjectLibraryRoot : fromHost);
    }

    /// <summary>MCP 地址：<c>HISTORYVULCAN_ENDPOINT</c>（URL）→ <c>portunus.mcp.status</c> 的 <c>url</c>。</summary>
    public static McpEndpoint ReadMcpEndpoint(ICommandBus? bus)
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYVULCAN_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var configuredUri) || configuredUri.IsFile)
                throw new InvalidOperationException("HISTORYVULCAN_ENDPOINT 必须是 MCP 的 http 回环 URL");
            return new McpEndpoint(RequireLoopbackMcpUri(configuredUri), null);
        }

        if (bus == null)
            throw new InvalidOperationException("HistoryDiana 尚未接上宿主总线，无法查询 MCP 地址");

        var url = ReadString(bus, "portunus.mcp.status", "url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("MCP 服务未运行或 HistoryPortunus 未装载（portunus.mcp.status 没有给出地址）");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"portunus.mcp.status 给出的地址无效: {url}");
        return new McpEndpoint(RequireLoopbackMcpUri(uri), null);
    }

    /// <summary>安静执行一条只读指令，按 JSON 字段名取一个字符串；指令失败或没有该字段时为 null。</summary>
    private static string? ReadString(ICommandBus bus, string command, string field)
    {
        var result = bus.InvokeAsync(command, "").GetAwaiter().GetResult();
        if (!result.Success || result.Data == null)
            return null;
        try
        {
            var data = result.Data is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty(field, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Uri RequireLoopbackMcpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !uri.IsLoopback
            || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.TrimEnd('/').Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HistoryVulcan MCP URL 必须是 http 回环地址并以 /mcp 为路径");
        }

        return uri;
    }
}

internal sealed record McpEndpoint(Uri Uri, string? AccessToken);
