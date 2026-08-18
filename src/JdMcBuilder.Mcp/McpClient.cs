using System.Text.Json;

namespace JdMcBuilder.Mcp;

public interface IMcpToolInvoker
{
    IReadOnlyDictionary<string, McpToolDefinition> Tools { get; }
    Task<McpToolResult> CallToolAsync(string name, object? arguments, CancellationToken cancellationToken = default);
}

public sealed class McpClient : IMcpToolInvoker, IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly McpConnectionOptions _options;
    private readonly Dictionary<string, McpToolDefinition> _tools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public McpClient(IMcpTransport transport, McpConnectionOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new McpConnectionOptions();
    }

    public IReadOnlyDictionary<string, McpToolDefinition> Tools => _tools;
    public JsonElement? ServerCapabilities { get; private set; }
    public Uri Endpoint => _options.Endpoint;
    public string? SessionId => _transport.SessionId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var initializeResult = await _transport.SendRequestAsync(
                "initialize",
                new
                {
                    protocolVersion = _options.ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = _options.ClientName, version = _options.ClientVersion }
                },
                cancellationToken).ConfigureAwait(false);

            ServerCapabilities = initializeResult.HasValue && initializeResult.Value.TryGetProperty("capabilities", out var capabilities)
                ? capabilities.Clone()
                : null;
            await _transport.SendNotificationAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
            await RefreshToolsCoreAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            _initialized = false;
            _tools.Clear();
            ServerCapabilities = null;
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                throw new McpException(McpFailureKind.Protocol, "MCP 客户端尚未完成 initialize。" );
            }

            try
            {
                await RefreshToolsCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (McpException exception) when (exception.Kind == McpFailureKind.SessionExpired)
            {
                _initialized = false;
                _tools.Clear();
                ServerCapabilities = null;
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RefreshToolsCoreAsync(CancellationToken cancellationToken)
    {
        var result = await _transport.SendRequestAsync("tools/list", new { }, cancellationToken).ConfigureAwait(false);
        _tools.Clear();
        if (!result.HasValue || !result.Value.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null;
            var inputSchema = tool.TryGetProperty("inputSchema", out var schema) ? schema.Clone() : JsonSerializer.SerializeToElement(new { type = "object" }, McpJson.SerializerOptions);
            _tools[name] = new McpToolDefinition(name, description, inputSchema);
        }
    }

    public async Task<McpToolResult> CallToolAsync(string name, object? arguments, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                throw new McpException(McpFailureKind.Protocol, "MCP 客户端尚未完成 initialize。" );
            }

            if (!_tools.ContainsKey(name))
            {
                throw new McpException(McpFailureKind.ToolNotFound, $"MCP 工具不存在或尚未发现：{name}。" );
            }

            JsonElement? result;
            try
            {
                result = await _transport.SendRequestAsync(
                    "tools/call",
                    new { name, arguments = arguments ?? new { } },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (McpException exception) when (exception.Kind == McpFailureKind.SessionExpired)
            {
                _initialized = false;
                _tools.Clear();
                ServerCapabilities = null;
                throw;
            }

            if (!result.HasValue)
            {
                throw new McpException(McpFailureKind.Protocol, $"工具 {name} 返回空结果。" );
            }

            var resultValue = result.Value;
            var content = resultValue.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array
                ? contentElement.EnumerateArray().Select(item => item.Clone()).ToArray()
                : Array.Empty<JsonElement>();
            var isError = resultValue.TryGetProperty("isError", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;
            var structured = resultValue.TryGetProperty("structuredContent", out var structuredElement) ? structuredElement.Clone() : (JsonElement?)null;
            // Keep the complete tool result as well. MCC deployments may put
            // the success data directly under the result (for example
            // data.material), rather than under structuredContent.
            var toolResult = new McpToolResult(
                content,
                isError,
                structured,
                resultValue.Clone());
            if (McpToolResultInspector.ClassifyFailure(toolResult) is { } failureKind)
            {
                throw new McpException(
                    failureKind,
                    $"MCP 工具 {name} 返回失败：{toolResult.ToDiagnosticText()}");
            }

            return toolResult;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            _tools.Clear();
            ServerCapabilities = null;
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(McpClient));
        }
    }

}
