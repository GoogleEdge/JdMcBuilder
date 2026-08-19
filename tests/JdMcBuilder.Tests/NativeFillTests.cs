using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class NativeFillTests
{
    [Fact]
    public void PlanUsesNormalizedRangeAndDeduplicatesCorners()
    {
        var plan = NativeFillVerificationPlan.Create(
            BlockRange.FromUnordered(
                new BlockPosition(2, 64, 3),
                new BlockPosition(1, 64, 3)),
            "minecraft:stone");

        Assert.Equal(
            "/fill 1 64 3 2 64 3 minecraft:stone",
            plan.Command);
        Assert.Equal(
            [
                new BlockPosition(1, 64, 3),
                new BlockPosition(2, 64, 3)
            ],
            plan.SamplePositions);
    }

    [Fact]
    public async Task ProbePollsDelayedVisibilityWithoutResendingFill()
    {
        var calls = new List<(string Name, object? Arguments)>();
        var nativeReadCounts = new Dictionary<BlockPosition, int>();
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add((name, arguments));
            if (name == "mcc_send_chat"
                && GetText(arguments).StartsWith("//pos ", StringComparison.Ordinal))
            {
                return Task.FromResult(Result(new
                {
                    success = false,
                    error = "WorldEdit probe unavailable"
                }));
            }

            if (name == "mcc_world_block_at")
            {
                var position = GetPosition(arguments);
                if (position == new BlockPosition(10, 64, 10))
                {
                    return Task.FromResult(
                        Result(new { x = position.X, y = position.Y, z = position.Z, material = "Stone" }));
                }

                var readCount = nativeReadCounts.TryGetValue(position, out var count)
                    ? count + 1
                    : 1;
                nativeReadCounts[position] = readCount;
                return Task.FromResult(readCount <= 1
                    ? Result(new { x = position.X, y = position.Y, z = position.Z, material = "Air" })
                    : Result(new { x = position.X, y = position.Y, z = position.Z, material = "Stone" }));
            }

            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                "mcc_send_chat" => Result(new { success = true }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            nativeFillVerificationOptions: FastOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(21, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Available, report.Find("native-fill")!.Status);
        Assert.Equal(2, calls.Count(call => call.Name == "mcc_send_chat"));
        Assert.Single(
            calls,
            call => call.Name == "mcc_send_chat"
                && GetText(call.Arguments) == "/fill 20 64 20 21 64 20 minecraft:stone");
        Assert.Equal(4, calls.Count(call => call.Name == "mcc_world_block_at"));
    }

    [Fact]
    public async Task ProbeDoesNotRequireChatHistoryForNativeFill()
    {
        var calls = new List<(string Name, object? Arguments)>();
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add((name, arguments));
            if (name == "mcc_send_chat"
                && GetText(arguments).StartsWith("//pos ", StringComparison.Ordinal))
            {
                return Task.FromResult(Result(new
                {
                    success = false,
                    error = "WorldEdit probe unavailable"
                }));
            }

            if (name == "mcc_world_block_at")
            {
                var position = GetPosition(arguments);
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "Stone"
                }));
            }

            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                "mcc_send_chat" => Result(new { success = true }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            nativeFillVerificationOptions: FastOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Unavailable, report.Find("worldedit")!.Status);
        Assert.Equal(BackendStatus.Available, report.Find("native-fill")!.Status);
        Assert.Single(
            calls,
            call => call.Name == "mcc_send_chat"
                && GetText(call.Arguments) == "/fill 20 64 20 20 64 20 minecraft:stone");
        Assert.DoesNotContain(
            calls,
            call => call.Name == "mcc_send_chat"
                && GetText(call.Arguments).StartsWith("//set ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistentMismatchRemainsUnverifiedAndDoesNotProbePlaceBlock()
    {
        var calls = new List<string>();
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at",
            "mcc_place_block",
            "mcc_select_item",
            "mcc_player_stats");
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add(name);
            var position = name == "mcc_world_block_at"
                ? GetPosition(arguments)
                : default;
            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                "mcc_world_block_at" when position == new BlockPosition(10, 64, 10) => Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "Stone"
                }),
                "mcc_world_block_at" => Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "Air"
                }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            nativeFillVerificationOptions: FastOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(21, 64, 20)),
            new BlockPosition(30, 64, 30)));
        var native = report.Find("native-fill")!;

        Assert.Equal(BackendStatus.Unverified, native.Status);
        Assert.True(native.WriteMayHaveBeenDispatched);
        Assert.Contains("/fill 20 64 20 21 64 20 minecraft:stone", native.Reason, StringComparison.Ordinal);
        Assert.Contains("采样点", native.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("mcc_place_block", calls);
    }

    [Fact]
    public async Task NativeBackendSendsFillOnceAndPollsAllCorners()
    {
        var chatCommands = new List<string>();
        var sampleCalls = new List<BlockPosition>();
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            if (name == "mcc_send_chat")
            {
                chatCommands.Add(GetText(arguments));
                return Task.FromResult(Result(new { success = true }));
            }

            var position = GetPosition(arguments);
            sampleCalls.Add(position);
            return Task.FromResult(Result(new
            {
                x = position.X,
                y = position.Y,
                z = position.Z,
                material = "Stone"
            }));
        });
        var backend = new NativeFillBackend(
            new MccToolClient(fake),
            FastOptions(),
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-fill"));
        var batch = new FillBatch(
            "phase/fill/batch-0000",
            "phase",
            "fill",
            new BlockRange(new BlockPosition(1, 64, 3), new BlockPosition(2, 64, 3)),
            "minecraft:stone");

        var result = await backend.ExecuteAsync(batch);

        Assert.True(result.Succeeded);
        Assert.Equal(["/fill 1 64 3 2 64 3 minecraft:stone"], chatCommands);
        Assert.Equal(
            [new BlockPosition(1, 64, 3), new BlockPosition(2, 64, 3)],
            sampleCalls);
    }

    [Fact]
    public async Task NativeBackendRejectsReturnedCoordinateMismatch()
    {
        var tools = ToolSet("mcc_send_chat", "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) =>
            Task.FromResult(name == "mcc_world_block_at"
                ? Result(new { x = 99, y = 64, z = 3, material = "Stone" })
                : Result(new { success = true })));
        var backend = new NativeFillBackend(
            new MccToolClient(fake),
            FastOptions(),
            BackendStatus.Available,
            verification: BackendVerification.CreateForTesting("native-fill"));
        var batch = new FillBatch(
            "phase/fill/batch-0000",
            "phase",
            "fill",
            new BlockRange(new BlockPosition(1, 64, 3), new BlockPosition(1, 64, 3)),
            "minecraft:stone");

        var exception = await Assert.ThrowsAsync<BackendException>(() => backend.ExecuteAsync(batch));

        Assert.True(exception.Uncertain);
        Assert.Contains("返回坐标", exception.Message, StringComparison.Ordinal);
    }

    private static NativeFillVerificationOptions FastOptions() => new()
    {
        MaxAttemptsPerSample = 3,
        OverallTimeout = TimeSpan.FromSeconds(1),
        InitialDelay = TimeSpan.Zero,
        MaximumDelay = TimeSpan.Zero,
        DelayAsync = static (_, _) => Task.CompletedTask
    };

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
