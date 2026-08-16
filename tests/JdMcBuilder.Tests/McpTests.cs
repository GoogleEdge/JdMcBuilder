using System.Text.Json;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task MccClientPassesTypedArgumentsToFakeInvoker()
    {
        var tools = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_send_chat"] = new("mcc_send_chat", null, JsonSerializer.SerializeToElement(new { type = "object" })),
            ["mcc_session_status"] = new("mcc_session_status", null, JsonSerializer.SerializeToElement(new { type = "object" }))
        };
        var calls = new List<(string Name, object? Arguments)>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add((name, arguments));
            return Task.FromResult(new McpToolResult([], false));
        });
        var client = new MccToolClient(fake);

        await client.SendChatAsync("//pos1 0 64 0");
        await client.SessionStatusAsync();

        Assert.Equal(2, calls.Count);
        Assert.Equal("mcc_send_chat", calls[0].Name);
        Assert.Contains("pos1", calls[0].Arguments!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityDetectorRequiresVerificationTools()
    {
        var tools = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_send_chat"] = new("mcc_send_chat", null, JsonSerializer.SerializeToElement(new { type = "object" }))
        };

        var report = MccCapabilityDetector.Detect(tools);

        Assert.Equal(CapabilityStatus.Unavailable, report.Find("worldedit")!.Status);
        Assert.Equal(CapabilityStatus.Unavailable, report.Find("native-fill")!.Status);
        Assert.Equal(CapabilityStatus.Unavailable, report.Find("place-block")!.Status);
    }

    [Fact]
    public void CapabilityDetectorReportsUnverifiedWithCompletePrerequisites()
    {
        var tools = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_send_chat"] = Definition("mcc_send_chat"),
            ["mcc_chat_history"] = Definition("mcc_chat_history"),
            ["mcc_world_block_at"] = Definition("mcc_world_block_at"),
            ["mcc_place_block"] = Definition("mcc_place_block"),
            ["mcc_select_item"] = Definition("mcc_select_item"),
            ["mcc_player_stats"] = Definition("mcc_player_stats")
        };

        var report = MccCapabilityDetector.Detect(tools);

        Assert.Equal(CapabilityStatus.Unverified, report.Find("worldedit")!.Status);
        Assert.Equal(CapabilityStatus.Unverified, report.Find("native-fill")!.Status);
        Assert.Equal(CapabilityStatus.Unverified, report.Find("place-block")!.Status);
    }

    [Fact]
    public void ToolResultInspectorRejectsFalseStructuredSuccess()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { success = false }));

        Assert.Equal(McpFailureKind.RemoteFailure, McpToolResultInspector.ClassifyFailure(result));
    }

    [Fact]
    public void ToolResultExtractsAndNormalizesBlockId()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { block = "minecraft:Stone" }));

        Assert.True(result.TryGetBlockId(out var block));
        Assert.Equal("minecraft:stone", block);
    }

    [Fact]
    public void ToolResultExtractsHeldItemId()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(
                new { player = new { mainHand = "Stone" } }));

        Assert.True(result.TryGetItemId(out var item));
        Assert.Equal("Stone", item);
    }

    private static McpToolDefinition Definition(string name) =>
        new(name, null, JsonSerializer.SerializeToElement(new { type = "object" }));
}
