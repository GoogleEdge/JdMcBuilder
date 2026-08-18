using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class WorldEditCommandTests
{
    [Fact]
    public async Task BackendSendsMccWorldEditCommandsInOrder()
    {
        var tools = ToolSet("mcc_send_chat");
        var chatPayloads = new List<string>();
        var events = new List<string>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            Assert.Equal("mcc_send_chat", name);
            var text = GetText(arguments);
            chatPayloads.Add(text);
            events.Add($"send:{text}");
            return Task.FromResult(Result(new { success = true }));
        });
        var sampleCalls = new List<(BlockPosition Position, string Block)>();
        var backend = new WorldEditCommandBackend(
            new MccToolClient(fake),
            (position, block, _) =>
            {
                sampleCalls.Add((position, block));
                events.Add($"sample:{position}:{block}");
                return Task.CompletedTask;
            },
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("worldedit"));
        var range = new BlockRange(
            new BlockPosition(1, 64, 2),
            new BlockPosition(3, 65, 4));
        var batch = new FillBatch(
            "phase/fill/batch-0000",
            "phase",
            "fill",
            range,
            "minecraft:stone");

        var result = await backend.ExecuteAsync(batch);

        var expectedCommands = new[]
        {
            "//pos 1,64,2 3,65,4",
            "//set minecraft:stone"
        };

        Assert.True(result.Succeeded);
        Assert.False(result.Uncertain);
        Assert.Equal(expectedCommands, chatPayloads.ToArray());
        Assert.Equal(
            expectedCommands
                .Select(command => $"mcc_send_chat({command})")
                .ToArray(),
            result.ToolCalls.ToArray());
        Assert.Equal(
            new[] { (new BlockPosition(1, 64, 2), "minecraft:stone") },
            sampleCalls.ToArray());
        Assert.Equal(
            new[]
            {
                "send://pos 1,64,2 3,65,4",
                "send://set minecraft:stone",
                "sample:1 64 2:minecraft:stone"
            },
            events.ToArray());
    }

    [Fact]
    public async Task ProbeSendsMccWorldEditCommandsBeforeObservingResult()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_chat_history",
            "mcc_world_block_at");
        var calls = new List<(string Name, string? Text)>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add((name, name == "mcc_send_chat" ? GetText(arguments) : null));
            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "session-1" }),
                "mcc_world_state" => Result(new { dimension = "minecraft:overworld" }),
                "mcc_world_block_at" => Result(new { block = "minecraft:stone" }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(new MccToolClient(fake));
        var request = new BackendProbeRequest(
            new BlockRange(
                new BlockPosition(10, 64, 10),
                new BlockPosition(11, 64, 10)),
            new BlockRange(
                new BlockPosition(20, 64, 20),
                new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30));

        var report = await probe.ProbeApprovedAsync(request);

        Assert.True(report.Find("worldedit")!.IsVerified);
        Assert.Equal(
            [
                ("mcc_session_status", (string?)null),
                ("mcc_world_state", (string?)null),
                ("mcc_send_chat", (string?)"//pos 10,64,10 11,64,10"),
                ("mcc_send_chat", (string?)"//set minecraft:stone"),
                ("mcc_chat_history", (string?)null),
                ("mcc_world_block_at", (string?)null)
            ],
            calls.Take(6).ToArray());
    }

    private static IReadOnlyDictionary<string, McpToolDefinition> ToolSet(
        params string[] names) =>
        names.ToDictionary(
            name => name,
            name => new McpToolDefinition(
                name,
                null,
                JsonSerializer.SerializeToElement(new { type = "object" })),
            StringComparer.Ordinal);

    private static string GetText(object? arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        return element.GetProperty("text").GetString()
            ?? throw new InvalidOperationException("mcc_send_chat 缺少 text 参数。");
    }

    private static McpToolResult Result<T>(T value) =>
        new([], false, JsonSerializer.SerializeToElement(value));
}
