using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JdMcBuilder.Mcp;

public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly McpConnectionOptions _options;
    private long _nextRequestId;
    private bool _connected;

    public HttpMcpTransport(McpConnectionOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("MCP endpoint 必须是绝对 http/https URL。", nameof(options));
        }

        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = options.RequestTimeout;
    }

    public string? SessionId { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionId = null;
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var id = Interlocked.Increment(ref _nextRequestId);
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        using var request = CreateRequest(payload);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(McpFailureKind.Timeout, $"MCP 请求超时：{method}。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new McpException(McpFailureKind.Transport, $"无法连接 MCP endpoint：{_options.Endpoint}。", exception);
        }

        using (response)
        {
            CaptureSessionId(response);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (IsSessionExpired(response.StatusCode, body))
                {
                    InvalidateSession();
                    throw new McpException(
                        McpFailureKind.SessionExpired,
                        "MCP session 已过期或不存在；请重新连接，不会自动重放操作。")
                    {
                        HttpStatusCode = (int)response.StatusCode
                    };
                }

                throw new McpException(McpFailureKind.Http, $"MCP HTTP 错误 {(int)response.StatusCode}：{Truncate(body)}")
                {
                    HttpStatusCode = (int)response.StatusCode
                };
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var responseElement = ParseResponseBody(bodyText, contentType);
            if (responseElement.ValueKind != JsonValueKind.Object)
            {
                throw new McpException(McpFailureKind.Protocol, $"MCP 响应不是 JSON 对象：{method}。" );
            }

            ValidateJsonRpcResponse(responseElement, id, method);
            if (responseElement.TryGetProperty("error", out var errorElement))
            {
                var error = ParseError(errorElement);
                throw new McpException(ClassifyError(error), $"MCP 调用失败：{error.Message}")
                {
                    RpcErrorCode = error.Code
                };
            }

            return responseElement.TryGetProperty("result", out var result) ? result.Clone() : null;
        }
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };
        using var request = CreateRequest(payload);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            CaptureSessionId(response);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (IsSessionExpired(response.StatusCode, body))
                {
                    InvalidateSession();
                    throw new McpException(
                        McpFailureKind.SessionExpired,
                        "MCP session 已过期或不存在；请重新连接，不会自动重放操作。")
                    {
                        HttpStatusCode = (int)response.StatusCode
                    };
                }

                throw new McpException(McpFailureKind.Http, $"MCP 通知失败：HTTP {(int)response.StatusCode}：{Truncate(body)}")
                {
                    HttpStatusCode = (int)response.StatusCode
                };
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(McpFailureKind.Timeout, $"MCP 通知超时：{method}。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new McpException(McpFailureKind.Transport, $"无法发送 MCP 通知：{method}。", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        SessionId = null;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private HttpRequestMessage CreateRequest(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, McpJson.SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _options.ProtocolVersion);
        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
        }

        var environmentVariable = _options.AuthTokenEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var token = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return request;
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)
            || response.Headers.TryGetValues("MCP-Session-Id", out sessionValues))
        {
            SessionId = sessionValues.FirstOrDefault();
        }
    }

    private static JsonElement ParseResponseBody(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new McpException(McpFailureKind.Protocol, "MCP 返回了空响应。" );
        }

        if (contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var line in System.Linq.Enumerable.Reverse(body.Split('\n')))
            {
                var data = line.Trim();
                if (!data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var json = data[5..].Trim();
                if (json is "[DONE]" or "")
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Ignore non-JSON SSE comments/events and continue searching.
                }
            }

            throw new McpException(McpFailureKind.Protocol, "MCP SSE 响应中没有 JSON 数据。" );
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new McpException(McpFailureKind.Protocol, "MCP 响应不是有效 JSON。", exception);
        }
    }

    private static void ValidateJsonRpcResponse(JsonElement response, long requestId, string method)
    {
        if (!response.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal))
        {
            throw new McpException(McpFailureKind.Protocol, $"MCP {method} 响应缺少有效的 JSON-RPC 2.0 版本。" );
        }

        if (!response.TryGetProperty("id", out var responseId)
            || responseId.ValueKind != JsonValueKind.Number
            || !responseId.TryGetInt64(out var responseIdValue)
            || responseIdValue != requestId)
        {
            throw new McpException(McpFailureKind.Protocol, $"MCP {method} 响应 ID 与请求不匹配。" );
        }
    }

    private static McpJsonRpcError ParseError(JsonElement element)
    {
        var code = element.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value) ? value : -1;
        var message = element.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "未知错误" : "未知错误";
        var data = element.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : (JsonElement?)null;
        return new McpJsonRpcError(code, message, data);
    }

    private static McpFailureKind ClassifyError(McpJsonRpcError error) => error.Code switch
    {
        -32602 => McpFailureKind.InvalidArguments,
        -32601 => McpFailureKind.ToolNotFound,
        -32001 or -32003 => McpFailureKind.PermissionDenied,
        _ => McpFailureKind.RemoteFailure
    };

    private static bool IsSessionExpired(HttpStatusCode statusCode, string body) =>
        (statusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Gone)
        && body.Contains("session", StringComparison.OrdinalIgnoreCase)
        && (body.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || body.Contains("not_found", StringComparison.OrdinalIgnoreCase)
            || body.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || body.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    private void InvalidateSession()
    {
        SessionId = null;
        _connected = false;
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new McpException(McpFailureKind.Transport, "MCP transport 尚未连接。" );
        }
    }
}
