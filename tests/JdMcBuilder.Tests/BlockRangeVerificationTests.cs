using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class BlockRangeVerificationTests
{
    [Fact]
    public void PlansStableDeduplicatedCornersForPointLinePlaneAndVolume()
    {
        Assert.Single(Plan(1, 2, 3, 1, 2, 3).SamplePositions);
        Assert.Equal(2, Plan(1, 2, 3, 4, 2, 3).SamplePositions.Count);
        Assert.Equal(4, Plan(1, 2, 3, 4, 2, 6).SamplePositions.Count);
        Assert.Equal(8, Plan(1, 2, 3, 4, 5, 6).SamplePositions.Count);

        Assert.Equal(
            [
                new BlockPosition(0, 64, 205),
                new BlockPosition(0, 64, 211),
                new BlockPosition(48, 64, 205),
                new BlockPosition(48, 64, 211)
            ],
            Plan(48, 64, 211, 0, 64, 205).SamplePositions);
    }

    [Fact]
    public async Task VerifierRetriesOnlyPendingCorners()
    {
        var counts = new Dictionary<BlockPosition, int>();
        var fake = new FakeMcpToolInvoker(
            Tools(),
            (_, arguments, _) =>
            {
                var position = Position(arguments);
                counts[position] = counts.TryGetValue(position, out var count)
                    ? count + 1
                    : 1;
                var first = new BlockPosition(0, 64, 205);
                var material = position == first || counts[position] > 1
                    ? "GrayConcrete"
                    : "Air";
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material
                }));
            });
        var verifier = new BlockRangeVerifier(
            new MccToolClient(fake),
            FastOptions());
        var plan = Plan(0, 64, 205, 48, 64, 211);

        var result = await verifier.VerifyAsync(plan);

        Assert.True(result.Verified);
        Assert.Equal(1, counts[new BlockPosition(0, 64, 205)]);
        Assert.All(
            counts.Where(item => item.Key != new BlockPosition(0, 64, 205)),
            item => Assert.Equal(2, item.Value));
    }

    [Fact]
    public async Task VerifierRejectsWrongReturnedCoordinate()
    {
        var fake = new FakeMcpToolInvoker(
            Tools(),
            (_, _, _) => Task.FromResult(Result(new
            {
                x = 99,
                y = 64,
                z = 205,
                material = "GrayConcrete"
            })));
        var verifier = new BlockRangeVerifier(
            new MccToolClient(fake),
            FastOptions());

        var exception = await Assert.ThrowsAnyAsync<BackendException>(() =>
            verifier.VerifyAsync(Plan(0, 64, 205, 0, 64, 205)));

        Assert.True(exception.Uncertain);
        Assert.Contains("返回坐标", exception.Message, StringComparison.Ordinal);
    }

    private static BlockRangeVerificationPlan Plan(
        int x1,
        int y1,
        int z1,
        int x2,
        int y2,
        int z2) =>
        BlockRangeVerificationPlan.Create(
            new BlockRange(
                new BlockPosition(x1, y1, z1),
                new BlockPosition(x2, y2, z2)),
            "minecraft:gray_concrete");

    private static BlockRangeVerificationOptions FastOptions() => new()
    {
        MaxAttemptsPerSample = 2,
        OverallTimeout = TimeSpan.FromSeconds(1),
        InitialDelay = TimeSpan.Zero,
        MaximumDelay = TimeSpan.Zero,
        DelayAsync = static (_, _) => Task.CompletedTask
    };

    private static IReadOnlyDictionary<string, McpToolDefinition> Tools() =>
        new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_world_block_at"] = new(
                "mcc_world_block_at",
                null,
                JsonSerializer.SerializeToElement(new { type = "object" }))
        };

    private static BlockPosition Position(object? arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        return new BlockPosition(
            element.GetProperty("x").GetInt32(),
            element.GetProperty("y").GetInt32(),
            element.GetProperty("z").GetInt32());
    }

    private static McpToolResult Result<T>(T value) =>
        new([], false, JsonSerializer.SerializeToElement(value));
}
