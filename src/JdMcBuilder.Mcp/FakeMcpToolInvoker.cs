namespace JdMcBuilder.Mcp;

public sealed class FakeMcpToolInvoker : IMcpToolInvoker
{
    private readonly Func<string, object?, CancellationToken, Task<McpToolResult>> _handler;

    public FakeMcpToolInvoker(
        IReadOnlyDictionary<string, McpToolDefinition> tools,
        Func<string, object?, CancellationToken, Task<McpToolResult>> handler)
    {
        Tools = tools;
        _handler = handler;
    }

    public IReadOnlyDictionary<string, McpToolDefinition> Tools { get; }

    public Task<McpToolResult> CallToolAsync(string name, object? arguments, CancellationToken cancellationToken = default) =>
        _handler(name, arguments, cancellationToken);
}
