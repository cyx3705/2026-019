using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoryDiana;

internal sealed class OhsmcpClient : IDisposable
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    private static long _nextRequestId;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    private OhsmcpClient(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public static OhsmcpClient FromEndpoint(McpEndpoint endpoint, int? timeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var timeout = timeoutSeconds is int explicitTimeout
            ? Math.Clamp(explicitTimeout, 5, 3600)
            : DefaultTimeoutSeconds;

        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeout),
        };
        if (!string.IsNullOrEmpty(endpoint.AccessToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AccessToken);

        return new OhsmcpClient(http, endpoint.Uri);
    }

    public async Task<IReadOnlyList<RelayTool>> ListToolsAsync()
    {
        var result = await SendAsync("tools/list", null).ConfigureAwait(false);
        if (!result.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("MCP tools/list 返回格式无效");
        }

        var tools = new List<RelayTool>();
        foreach (var item in toolsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement)
                || nameElement.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            var description = item.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? ""
                : "";
            var schema = item.TryGetProperty("inputSchema", out var schemaElement)
                ? schemaElement.Clone()
                : EmptyObject();
            tools.Add(new RelayTool(name, description, schema));
        }

        return tools;
    }

    public async Task<IReadOnlySet<string>> ListVisibleModuleToolNamesAsync()
    {
        var arguments = new JsonObject { ["mcp"] = "visible" };
        var result = await CallToolCoreAsync("vulcan_command_list", arguments).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("vulcan.command.list 拒绝模块来源查询");
        }

        // 优先读 structuredContent.data；content 文本块作回退。
        if (result.TryGetProperty("structuredContent", out var structuredElement)
            && structuredElement.ValueKind == JsonValueKind.Object
            && structuredElement.TryGetProperty("data", out var dataElement)
            && TryReadModuleNames(dataElement, out var fromStructured))
        {
            return fromStructured;
        }

        // 回退:扫 content 文本块。兼容 V2.4.4 及更早网关,永久保留,不是过渡代码。
        if (!result.TryGetProperty("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("vulcan.command.list 返回格式无效");
        }

        foreach (var content in contentElement.EnumerateArray())
        {
            if (!content.TryGetProperty("text", out var textElement)
                || textElement.GetString() is not { Length: > 0 } text)
            {
                continue;
            }

            try
            {
                using var rows = JsonDocument.Parse(text);
                if (TryReadModuleNames(rows.RootElement, out var fromContent))
                    return fromContent;
            }
            catch (JsonException)
            {
                // 第一段通常是人类可读摘要，继续寻找结构化 JSON 文本段。
            }
        }

        throw new InvalidOperationException("vulcan.command.list 未返回可解析的模块清单");
    }

    /// <summary>
    /// 从 command_list 的行数组提取模块工具名。structuredContent 优先路径与
    /// content 回退路径共用本函数,确保两条路径解析逻辑不漂移。
    /// </summary>
    private static bool TryReadModuleNames(JsonElement rows, out IReadOnlySet<string> names)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            names = new HashSet<string>();
            return false;
        }

        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.EnumerateArray())
        {
            var source = row.TryGetProperty("Source", out var sourceElement)
                ? sourceElement.GetString()
                : null;
            var toolName = row.TryGetProperty("McpToolName", out var toolElement)
                ? toolElement.GetString()
                : null;
            if (source != null
                && source.Equals("module", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(toolName))
            {
                collected.Add(toolName);
            }
        }

        names = collected;
        return true;
    }

    public Task<JsonElement> CallToolAsync(string name, JsonObject arguments)
        => CallToolCoreAsync(name, arguments);

    private async Task<JsonElement> CallToolCoreAsync(string name, JsonObject arguments)
    {
        var parameters = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.DeepClone(),
        };
        return await SendAsync("tools/call", parameters).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(string method, JsonObject? parameters)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = method,
        };
        if (parameters != null)
            payload["params"] = parameters.DeepClone();

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("MCP 请求超时，目标可能仍在宿主内执行；不会自动重试");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接当前 HistoryVulcan MCP 回环服务");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("MCP 认证失败");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"MCP 返回 HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new InvalidOperationException("MCP 响应超过 4 MiB 上限");

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new InvalidOperationException("读取 MCP 响应失败");
            }

            if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
                throw new InvalidOperationException("MCP 响应超过 4 MiB 上限");

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var rpcError))
                {
                    var message = rpcError.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "未知错误"
                        : "未知错误";
                    throw new InvalidOperationException($"MCP 拒绝请求: {message}");
                }

                if (!root.TryGetProperty("result", out var result))
                    throw new InvalidOperationException("MCP JSON-RPC 响应缺少 result");
                return result.Clone();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("MCP 返回了无效 JSON");
            }
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}

public sealed record RelayTool(string Name, string Description, JsonElement InputSchema);
