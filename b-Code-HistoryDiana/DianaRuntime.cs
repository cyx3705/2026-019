using System.IO;
using System.Text.Json;

namespace HistoryDiana;

/// <summary>Resolves module-owned runtime inputs after Vulcan 5.0 removed host settings from IModuleContext.</summary>
internal static class DianaRuntime
{
    private const string DefaultProjectLibraryRoot = @"C:\OneHistory\HistoryClio";
    private const string CursorServerName = "history-vulcan";

    public static string ResolveProjectLibraryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYCLIO_ROOT");
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultProjectLibraryRoot : configured.Trim();
        return Path.GetFullPath(value);
    }

    public static McpEndpoint ReadMcpEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYVULCAN_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var value = configured.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var configuredUri)
                && !configuredUri.IsFile)
                return new McpEndpoint(RequireLoopbackMcpUri(configuredUri), null);
            return ReadLegacyEndpointFile(Path.GetFullPath(value));
        }

        var configuredMcpFile = Environment.GetEnvironmentVariable("HISTORYVULCAN_MCP_CONFIG");
        var path = string.IsNullOrWhiteSpace(configuredMcpFile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json")
            : Path.GetFullPath(configuredMcpFile.Trim());
        if (!File.Exists(path))
            throw new InvalidOperationException($"找不到 HistoryVulcan MCP 配置: {path}");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty(CursorServerName, out var server)
                || server.ValueKind != JsonValueKind.Object
                || !server.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            {
                throw new InvalidOperationException($"MCP 配置缺少 mcpServers.{CursorServerName}.url");
            }

            return new McpEndpoint(RequireLoopbackMcpUri(url), null);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("HistoryVulcan MCP 配置不是有效 JSON", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("读取 HistoryVulcan MCP 配置失败", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("读取 HistoryVulcan MCP 配置失败", ex);
        }
    }

    private static McpEndpoint ReadLegacyEndpointFile(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"找不到 HistoryVulcan MCP endpoint.json: {path}");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var token = root.TryGetProperty("accessToken", out var tokenElement)
                        && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (root.TryGetProperty("endpoint", out var endpointElement)
                && endpointElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(endpointElement.GetString(), UriKind.Absolute, out var endpoint))
            {
                return new McpEndpoint(RequireLoopbackMcpUri(endpoint), token);
            }

            if (root.TryGetProperty("url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            {
                return new McpEndpoint(RequireLoopbackMcpUri(url), token);
            }

            if (!root.TryGetProperty("port", out var portElement)
                || portElement.ValueKind != JsonValueKind.Number
                || !portElement.TryGetInt32(out var port)
                || port is < 1024 or > 65535)
            {
                throw new InvalidOperationException("endpoint.json 缺少有效的 port 或 endpoint");
            }

            return new McpEndpoint(RequireLoopbackMcpUri(new Uri($"http://127.0.0.1:{port}/mcp")), token);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("HistoryVulcan endpoint.json 不是有效 JSON", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("读取 HistoryVulcan endpoint.json 失败", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("读取 HistoryVulcan endpoint.json 失败", ex);
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
