using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class SetBlockTests
{
    [Fact]
    public async Task BackendSendsEachSetblockAndVerifiesSameCoordinate()
    {
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var commands = new List<string>();
        var samples = new List<BlockPosition>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            if (name == "mcc_send_chat")
            {
                commands.Add(GetText(arguments));
                return Task.FromResult(Result(new
                {
                    timestampUtc = "2026-08-19T10:13:15Z",
                    kind = "system",
                    text = "更改了位于6, 64, 1的方块"
                }));
            }

            var position = GetPosition(arguments);
            samples.Add(position);
            return Task.FromResult(Result(new
            {
                x = position.X,
                y = position.Y,
                z = position.Z,
                material = "Stone"
            }));
        });
        var mcc = new MccToolClient(fake);
        var backend = new NativeSetBlockBackend(
            mcc,
            new NativeSetBlockVerifier(
                mcc,
                new NativeSetBlockVerificationOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                }),
            "test-target",
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-setblock"));
        var batch = new ExplicitBlocksBatch(
            "phase/details/batch-0000",
            "phase",
            "details",
            [
                new BlockPlacement(new BlockPosition(6, 64, 1), "minecraft:stone"),
                new BlockPlacement(new BlockPosition(7, 64, 1), "minecraft:stone")
            ]);

        var result = await backend.ExecuteAsync(batch);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.BlocksChanged);
        Assert.Equal(
            [
                "/setblock 6 64 1 minecraft:stone",
                "/setblock 7 64 1 minecraft:stone"
            ],
            commands);
        Assert.Equal(
            [
                "mcc_send_chat(/setblock 6 64 1 minecraft:stone)",
                "mcc_send_chat(/setblock 7 64 1 minecraft:stone)"
            ],
            result.ToolCalls);
        Assert.Equal(
            [new BlockPosition(6, 64, 1), new BlockPosition(7, 64, 1)],
            samples);
    }

    [Fact]
    public async Task BackendRejectsInvalidLaterPlacementBeforeAnySend()
    {
        var calls = new List<string>();
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) =>
        {
            calls.Add(name);
            return Task.FromResult(Result(new { success = true }));
        });
        var backend = new NativeSetBlockBackend(
            new MccToolClient(fake),
            new NativeSetBlockVerifier(
                new MccToolClient(fake),
                new NativeSetBlockVerificationOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                }),
            "test-target",
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-setblock"));
        var batch = new ExplicitBlocksBatch(
            "phase/details/batch-0000",
            "phase",
            "details",
            [
                new BlockPlacement(new BlockPosition(6, 64, 1), "minecraft:stone"),
                new BlockPlacement(new BlockPosition(7, 64, 1), "minecraft:stone", new Dictionary<string, string>
                {
                    ["facing"] = "north"
                })
            ]);

        var exception = await Assert.ThrowsAsync<BackendException>(() => backend.ExecuteAsync(batch));

        Assert.False(exception.Uncertain);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task BackendMarksSendFailureAfterDispatchAsUncertain()
    {
        var calls = new List<string>();
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) =>
        {
            calls.Add(name);
            throw new McpException(McpFailureKind.Transport, "连接在发送后中断");
        });
        var backend = new NativeSetBlockBackend(
            new MccToolClient(fake),
            new NativeSetBlockVerifier(
                new MccToolClient(fake),
                new NativeSetBlockVerificationOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                }),
            "test-target",
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-setblock"));
        var batch = new ExplicitBlocksBatch(
            "phase/details/batch-0000",
            "phase",
            "details",
            [new BlockPlacement(new BlockPosition(6, 64, 1), "minecraft:stone")]);

        var exception = await Assert.ThrowsAsync<BackendException>(() => backend.ExecuteAsync(batch));

        Assert.True(exception.Uncertain);
        Assert.Equal(["mcc_send_chat"], calls);
    }

    [Fact]
    public async Task BackendPollsDelayedVisibilityWithoutResendingSetblock()
    {
        var calls = new List<string>();
        var reads = 0;
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add(name);
            if (name == "mcc_world_block_at")
            {
                reads++;
                var position = GetPosition(arguments);
                return Task.FromResult(reads == 1
                    ? Result(new { x = position.X, y = position.Y, z = position.Z, material = "Air" })
                    : Result(new { x = position.X, y = position.Y, z = position.Z, material = "Stone" }));
            }

            return Task.FromResult(Result(new { text = "更改了位于6, 64, 1的方块" }));
        });
        var mcc = new MccToolClient(fake);
        var backend = new NativeSetBlockBackend(
            mcc,
            new NativeSetBlockVerifier(
                mcc,
                new NativeSetBlockVerificationOptions
                {
                    MaxAttemptsPerPlacement = 2,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                }),
            "test-target",
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-setblock"));
        var batch = new ExplicitBlocksBatch(
            "phase/details/batch-0000",
            "phase",
            "details",
            [new BlockPlacement(new BlockPosition(6, 64, 1), "minecraft:stone")]);

        var result = await backend.ExecuteAsync(batch);

        Assert.True(result.Succeeded);
        Assert.Equal(1, calls.Count(name => name == "mcc_send_chat"));
        Assert.Equal(2, calls.Count(name => name == "mcc_world_block_at"));
        Assert.DoesNotContain("mcc_place_block", calls);
    }

    [Fact]
    public async Task VerifierRejectsLateReadAfterCancellation()
    {
        var readStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readResult = new TaskCompletionSource<McpToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = ToolSet("mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) =>
        {
            if (name == "mcc_world_block_at")
            {
                readStarted.TrySetResult(true);
                return readResult.Task;
            }

            throw new InvalidOperationException($"Unexpected tool: {name}");
        });
        var verifier = new NativeSetBlockVerifier(
            new MccToolClient(fake),
            new NativeSetBlockVerificationOptions
            {
                MaxAttemptsPerPlacement = 1,
                OverallTimeout = TimeSpan.FromSeconds(1),
                InitialDelay = TimeSpan.Zero,
                MaximumDelay = TimeSpan.Zero,
                DelayAsync = static (_, _) => Task.CompletedTask
            });
        using var cancellation = new CancellationTokenSource();
        var verification = verifier.VerifyAsync(
            new BlockPosition(6, 64, 1),
            "minecraft:stone",
            cancellation.Token);

        await readStarted.Task;
        cancellation.Cancel();
        readResult.TrySetResult(Result(new
        {
            x = 6,
            y = 64,
            z = 1,
            material = "Stone"
        }));

        var exception = await Assert.ThrowsAsync<BackendException>(() => verification);

        Assert.True(exception.Uncertain);
        Assert.Contains("验证被取消", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendKeepsPersistentMismatchUncertainWithoutResendingSetblock()
    {
        var calls = new List<string>();
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) =>
        {
            calls.Add(name);
            return Task.FromResult(name == "mcc_world_block_at"
                ? Result(new { x = 6, y = 64, z = 1, material = "Dirt" })
                : Result(new { text = "更改了位于6, 64, 1的方块" }));
        });
        var mcc = new MccToolClient(fake);
        var backend = new NativeSetBlockBackend(
            mcc,
            new NativeSetBlockVerifier(
                mcc,
                new NativeSetBlockVerificationOptions
                {
                    MaxAttemptsPerPlacement = 2,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                }),
            "test-target",
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-setblock"));
        var batch = new ExplicitBlocksBatch(
            "phase/details/batch-0000",
            "phase",
            "details",
            [new BlockPlacement(new BlockPosition(6, 64, 1), "minecraft:stone")]);

        var exception = await Assert.ThrowsAsync<BackendException>(() => backend.ExecuteAsync(batch));

        Assert.True(exception.Uncertain);
        Assert.Equal(1, calls.Count(name => name == "mcc_send_chat"));
        Assert.Equal(2, calls.Count(name => name == "mcc_world_block_at"));
        Assert.DoesNotContain("mcc_place_block", calls);
    }

    private static IReadOnlyDictionary<string, McpToolDefinition> ToolSet(params string[] names) =>
        names.ToDictionary(
            name => name,
            name => new McpToolDefinition(
                name,
                null,
                JsonSerializer.SerializeToElement(new { type = "object" })),
            StringComparer.Ordinal);

    private static string GetText(object? arguments) =>
        JsonSerializer.SerializeToElement(arguments).GetProperty("text").GetString()
            ?? throw new InvalidOperationException("缺少 text 参数。");

    private static BlockPosition GetPosition(object? arguments)
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
