namespace JdMcBuilder.Mcp;

public sealed record McpConnectionOptions
{
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:33333/mcp");
    public string? AuthTokenEnvironmentVariable { get; init; } = "MCC_MCP_AUTH_TOKEN";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string ProtocolVersion { get; init; } = "2025-06-18";
    public string ClientName { get; init; } = "JdMcBuilder";
    public string ClientVersion { get; init; } = "0.1.0";
}

public sealed record McpClientInfo(string Name, string Version);
