using System.Net;
using System.Net.Http;
using System.Text;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task TransportRejectsMismatchedJsonRpcResponseId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await transport.ConnectAsync();

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            transport.SendRequestAsync("initialize", new { }));

        Assert.Equal(McpFailureKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task TransportRejectsMissingJsonRpcVersion()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":1,\"result\":{}}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await transport.ConnectAsync();

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            transport.SendRequestAsync("initialize", new { }));

        Assert.Equal(McpFailureKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task TransportSendsJsonRpcAndCapturesSessionHeader()
    {
        string? requestBody = null;
        string? protocolHeader = null;
        var handler = new StubHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            protocolHeader = request.Headers.GetValues("MCP-Protocol-Version").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { { "Mcp-Session-Id", "session-1" } },
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await transport.ConnectAsync();

        var result = await transport.SendRequestAsync("initialize", new { value = 1 });

        Assert.Equal("2025-06-18", protocolHeader);
        Assert.NotNull(requestBody);
        Assert.Contains("initialize", requestBody!, StringComparison.Ordinal);
        Assert.True(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("session-1", transport.SessionId);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
