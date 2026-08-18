using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task McpClientSerializesWorldEditChatTextUnchanged()
    {
        string? toolCallRequestBody = null;
        var handler = new StubHandler(request =>
        {
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            var responseId = root.TryGetProperty("id", out var id)
                ? id.GetRawText()
                : null;
            string result;
            switch (method)
            {
                case "initialize":
                    result = "{\"capabilities\":{}}";
                    break;
                case "notifications/initialized":
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case "tools/list":
                    result = "{\"tools\":[{\"name\":\"mcc_send_chat\",\"inputSchema\":{\"type\":\"object\"}}]}";
                    break;
                case "tools/call":
                    toolCallRequestBody = requestBody;
                    result = "{\"content\":[]}";
                    break;
                default:
                    throw new InvalidOperationException($"意外的 MCP 请求：{method}。");
            }

            if (responseId is null)
            {
                throw new InvalidOperationException($"MCP 请求缺少 ID：{method}。");
            }

            var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{responseId},\"result\":{result}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await using var client = new McpClient(transport);
        await client.ConnectAsync();
        const string command = "//pos 1,64,2 3,65,4";

        await new MccToolClient(client).SendChatAsync(command);

        Assert.NotNull(toolCallRequestBody);
        using var request = JsonDocument.Parse(toolCallRequestBody!);
        Assert.Equal("tools/call", request.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "//pos 1,64,2 3,65,4",
            request.RootElement
                .GetProperty("params")
                .GetProperty("arguments")
                .GetProperty("text")
                .GetString());
    }

    [Fact]
    public async Task McpClientPreservesDirectMccWorldBlockResult()
    {
        var handler = new StubHandler(request =>
        {
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            var responseId = root.TryGetProperty("id", out var id)
                ? id.GetRawText()
                : null;
            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (responseId is null)
            {
                throw new InvalidOperationException($"MCP 请求缺少 ID：{method}。");
            }

            var result = method switch
            {
                "initialize" => "{\"capabilities\":{}}",
                "tools/list" => "{\"tools\":[{\"name\":\"mcc_world_block_at\",\"inputSchema\":{\"type\":\"object\"}}]}",
                "tools/call" => "{\"success\":true,\"data\":{\"x\":1,\"y\":64,\"z\":1,\"material\":\"Stone\",\"blockId\":1,\"blockMeta\":0,\"stateId\":1,\"properties\":{}}}",
                _ => throw new InvalidOperationException($"意外的 MCP 请求：{method}。")
            };

            var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{responseId},\"result\":{result}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await using var client = new McpClient(transport);
        await client.ConnectAsync();

        var result = await new MccToolClient(client).WorldBlockAtAsync(1, 64, 1);

        Assert.True(result.TryGetBlockId(out var block));
        Assert.Equal("minecraft:stone", block);
    }

    [Fact]
    public async Task SessionNotFoundInvalidatesClientWithoutRetryingToolCall()
    {
        var toolCallCount = 0;
        var handler = new StubHandler(request =>
        {
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            var responseId = root.TryGetProperty("id", out var id)
                ? id.GetRawText()
                : null;
            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (method == "tools/call")
            {
                toolCallCount++;
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"error\":{\"code\":-32001,\"message\":\"Session not found\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (responseId is null)
            {
                throw new InvalidOperationException($"MCP 请求缺少 ID：{method}。");
            }

            var result = method switch
            {
                "initialize" => "{\"capabilities\":{}}",
                "tools/list" => "{\"tools\":[{\"name\":\"mcc_send_chat\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => throw new InvalidOperationException($"意外的 MCP 请求：{method}。")
            };
            var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{responseId},\"result\":{result}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(new McpConnectionOptions(), http);
        await using var client = new McpClient(transport);
        await client.ConnectAsync();

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync("mcc_send_chat", new { text = "//set minecraft:stone" }));

        Assert.Equal(McpFailureKind.SessionExpired, exception.Kind);
        Assert.Null(transport.SessionId);
        Assert.Equal(1, toolCallCount);

        var reconnectRequired = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync("mcc_send_chat", new { text = "//set minecraft:stone" }));

        Assert.Equal(McpFailureKind.Protocol, reconnectRequired.Kind);
        Assert.Equal(1, toolCallCount);
    }

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
