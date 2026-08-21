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
    public async Task VerifierDoesNotReadUnloadedChunkAsAir()
    {
        var calls = new List<string>();
        var fake = new FakeMcpToolInvoker(
            Tools("mcc_world_block_at", "mcc_chunk_status"),
            (name, arguments, _) =>
            {
                calls.Add(name);
                var position = Position(arguments);
                return Task.FromResult(name == "mcc_chunk_status"
                    ? Result(new
                    {
                        location = new { x = position.X, y = position.Y, z = position.Z },
                        chunk = new { x = position.X >> 4, z = position.Z >> 4 },
                        loaded = false,
                        fullyLoaded = false
                    })
                    : Result(new
                    {
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        material = "Air"
                    }));
            });
        var verifier = new BlockRangeVerifier(
            new MccToolClient(fake),
            new BlockRangeVerificationOptions
            {
                MaxAttemptsPerSample = 2,
                OverallTimeout = TimeSpan.FromSeconds(1),
                InitialDelay = TimeSpan.Zero,
                MaximumDelay = TimeSpan.Zero,
                DelayAsync = static (_, _) => Task.CompletedTask
            });

        var exception = await Assert.ThrowsAnyAsync<BackendException>(() =>
            verifier.VerifyAsync(Plan(0, 64, 205, 0, 64, 205)));

        Assert.True(exception.Uncertain);
        Assert.Contains("尚未完全加载", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mcc_world_block_at", calls);
        Assert.Equal(2, calls.Count(name => name == "mcc_chunk_status"));
    }

    [Fact]
    public async Task VerifierReadsBlockAfterReadyChunkStatus()
    {
        var calls = new List<string>();
        var fake = new FakeMcpToolInvoker(
            Tools("mcc_world_block_at", "mcc_chunk_status"),
            (name, arguments, _) =>
            {
                calls.Add(name);
                var position = Position(arguments);
                return Task.FromResult(name == "mcc_chunk_status"
                    ? Result(new
                    {
                        location = new { x = position.X, y = position.Y, z = position.Z },
                        chunk = new { x = position.X >> 4, z = position.Z >> 4 },
                        loaded = true,
                        fullyLoaded = true
                    })
                    : Result(new
                    {
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        material = "GrayConcrete"
                    }));
            });
        var verifier = new BlockRangeVerifier(
            new MccToolClient(fake),
            new BlockRangeVerificationOptions
            {
                MaxAttemptsPerSample = 1,
                OverallTimeout = TimeSpan.FromSeconds(1),
                InitialDelay = TimeSpan.Zero,
                MaximumDelay = TimeSpan.Zero,
                DelayAsync = static (_, _) => Task.CompletedTask
            });

        var result = await verifier.VerifyAsync(Plan(0, 64, 205, 0, 64, 205));

        Assert.True(result.Verified);
        Assert.Equal(["mcc_chunk_status", "mcc_world_block_at"], calls);
    }

    [Fact]
    public async Task VerifierKeepsCompatibilityWhenChunkStatusToolIsMissing()
    {
        var calls = new List<string>();
        var fake = new FakeMcpToolInvoker(
            Tools("mcc_world_block_at"),
            (name, arguments, _) =>
            {
                calls.Add(name);
                var position = Position(arguments);
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "GrayConcrete"
                }));
            });
        var verifier = new BlockRangeVerifier(
            new MccToolClient(fake),
            FastOptions());

        var result = await verifier.VerifyAsync(Plan(0, 64, 205, 0, 64, 205));

        Assert.True(result.Verified);
        Assert.Equal(["mcc_world_block_at"], calls);
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

    private static IReadOnlyDictionary<string, McpToolDefinition> Tools(
        params string[] names)
    {
        var selected = names.Length == 0
            ? ["mcc_world_block_at"]
            : names;
        return selected.ToDictionary(
            name => name,
            name => new McpToolDefinition(
                name,
                null,
                JsonSerializer.SerializeToElement(new { type = "object" })),
            StringComparer.Ordinal);
    }

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
