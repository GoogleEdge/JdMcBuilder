using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class ProbeTests
{
    [Fact]
    public async Task ApprovedProbeCreatesIndependentTargetBoundProofs()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_server_info",
            "mcc_send_chat",
            "mcc_chat_history",
            "mcc_world_block_at",
            "mcc_place_block",
            "mcc_select_item",
            "mcc_player_stats");
        var calls = new List<string>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add(name);
            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1", host = "localhost", port = 25565 }),
                "mcc_world_state" => Result(new { worldName = "probe", dimension = "minecraft:overworld" }),
                "mcc_server_info" => Result(new { version = "Leaf 1.21.11" }),
                "mcc_player_stats" => Result(new { mainHand = "Stone" }),
                "mcc_world_block_at" => Result(new
                {
                    x = 10,
                    y = 64,
                    z = 10,
                    material = "Stone",
                    blockId = 1,
                    blockMeta = 0,
                    stateId = 1,
                    properties = new { }
                }),
                _ => Result(new { success = true })
            });
        });
        var mcc = new MccToolClient(fake);
        var probe = new CommandCapabilityProbe(mcc);
        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(3, report.Results.Count);
        Assert.All(report.Results, result =>
        {
            Assert.Equal(BackendStatus.Available, result.Status);
            Assert.NotNull(result.Verification);
            Assert.Equal(report.TargetFingerprint, result.Verification!.TargetFingerprint);
            Assert.Equal(result.BackendId, result.Verification.BackendId);
        });
        Assert.Contains("mcc_send_chat", calls);
        Assert.Contains("mcc_place_block", calls);
    }

    [Fact]
    public async Task ProbeDoesNotCreateProofWhenBlockResponseIsUnrecognized()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_chat_history",
            "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) => Task.FromResult(name switch
        {
            "mcc_session_status" => Result(new { sessionId = "s1" }),
            "mcc_world_state" => Result(new { dimension = "overworld" }),
            "mcc_world_block_at" => Result(new { blockId = 1, blockMeta = 0 }),
            _ => Result(new { success = true })
        }));
        var probe = new CommandCapabilityProbe(new MccToolClient(fake));

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));
        var worldEdit = report.Find("worldedit")!;

        Assert.Equal(BackendStatus.Unverified, worldEdit.Status);
        Assert.Null(worldEdit.Verification);
        Assert.True(worldEdit.WriteMayHaveBeenDispatched);
        Assert.Contains("无法解析", worldEdit.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("实际 。", worldEdit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeDoesNotCreateProofWhenWriteVerificationMismatches()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_chat_history",
            "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) => Task.FromResult(name switch
        {
            "mcc_session_status" => Result(new { sessionId = "s1" }),
            "mcc_world_state" => Result(new { dimension = "overworld" }),
            "mcc_world_block_at" => Result(new { block = "minecraft:dirt" }),
            _ => Result(new { success = true })
        }));
        var probe = new CommandCapabilityProbe(new MccToolClient(fake));

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Unverified, report.Find("worldedit")!.Status);
        Assert.Null(report.Find("worldedit")!.Verification);
    }

    private static IReadOnlyDictionary<string, McpToolDefinition> ToolSet(
        params string[] names) =>
        names.ToDictionary(
            name => name,
            Definition,
            StringComparer.Ordinal);

    private static McpToolDefinition Definition(string name) =>
        new(name, null, JsonSerializer.SerializeToElement(new { type = "object" }));

    private static McpToolResult Result<T>(T value) =>
        new([], false, JsonSerializer.SerializeToElement(value));
}
