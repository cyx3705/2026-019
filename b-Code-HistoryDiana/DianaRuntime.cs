using System.IO;
using System.Text.Json;

namespace HistoryDiana;

/// <summary>Resolves module-owned runtime inputs after Vulcan 5.0 removed host settings from IModuleContext.</summary>
internal static class DianaRuntime
{
    private const string DefaultProjectLibraryRoot = @"C:\OneHistory\HistoryClio";

    public static string ResolveProjectLibraryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYCLIO_ROOT");
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultProjectLibraryRoot : configured.Trim();
        return Path.GetFullPath(value);
    }

    public static McpEndpoint ReadMcpEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("HISTORYVULCAN_ENDPOINT");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HistoryVulcan", "endpoint.json")
            : Path.GetFullPath(configured.Trim());
        if (!File.Exists(path))
            throw new InvalidOperationException($"找不到 HistoryVulcan MCP endpoint.json: {path}");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var token = root.TryGetProperty("accessToken", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (root.TryGetProperty("endpoint", out var endpointElement)
                && Uri.TryCreate(endpointElement.GetString(), UriKind.Absolute, out var endpoint))
            {
                return new McpEndpoint(endpoint, token);
            }

            if (root.TryGetProperty("url", out var urlElement)
                && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            {
                return new McpEndpoint(url, token);
            }

            if (!root.TryGetProperty("port", out var portElement)
                || !portElement.TryGetInt32(out var port)
                || port is < 1024 or > 65535)
            {
                throw new InvalidOperationException("endpoint.json 缺少有效的 port 或 endpoint");
            }

            return new McpEndpoint(new Uri($"http://127.0.0.1:{port}/mcp"), token);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("HistoryVulcan endpoint.json 不是有效 JSON", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("读取 HistoryVulcan endpoint.json 失败", ex);
        }
    }
}

internal sealed record McpEndpoint(Uri Uri, string? AccessToken);
