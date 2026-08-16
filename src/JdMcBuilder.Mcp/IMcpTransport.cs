using System.Text.Json;

namespace JdMcBuilder.Mcp;

public interface IMcpTransport : IAsyncDisposable
{
    string? SessionId { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);
    Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default);
}
