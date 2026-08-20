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
            "mcc_world_block_at");
        var calls = new List<string>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            calls.Add(name);
            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1", host = "localhost", port = 25565 }),
                "mcc_world_state" => Result(new { worldName = "probe", dimension = "minecraft:overworld" }),
                "mcc_server_info" => Result(new { version = "Leaf 1.21.11" }),
                "mcc_world_block_at" => Result(new
                {
                    x = GetPosition(arguments).X,
                    y = GetPosition(arguments).Y,
                    z = GetPosition(arguments).Z,
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
        Assert.DoesNotContain("mcc_place_block", calls);
        Assert.Equal(4, calls.Count(name => name == "mcc_send_chat"));
    }

    [Fact]
    public async Task ProbeDoesNotCreateProofWhenBlockResponseIsUnrecognized()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
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
    public async Task ProbeWorldEditPollsDelayedVisibilityWithoutResendingCommands()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var sendCount = 0;
        var chatCommands = new List<string>();
        var readsByPosition = new Dictionary<BlockPosition, int>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            if (name == "mcc_send_chat")
            {
                sendCount++;
                chatCommands.Add(GetText(arguments));
                return Task.FromResult(Result(new { success = true }));
            }

            if (name == "mcc_world_block_at")
            {
                var position = GetPosition(arguments);
                var positionReadCount = readsByPosition.TryGetValue(position, out var previous)
                    ? previous + 1
                    : 1;
                readsByPosition[position] = positionReadCount;
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = position == new BlockPosition(10, 64, 10)
                        && positionReadCount == 1
                        ? "Air"
                        : "Stone"
                }));
            }

            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            worldEditVerificationOptions: FastWorldEditOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Available, report.Find("worldedit")!.Status);
        Assert.Equal(4, sendCount);
        Assert.Equal(
            ["//pos 10,64,10 10,64,10", "//set minecraft:stone"],
            chatCommands.Take(2).ToArray());
        Assert.Equal(2, readsByPosition[new BlockPosition(10, 64, 10)]);
        Assert.Equal(1, readsByPosition[new BlockPosition(20, 64, 20)]);
        Assert.Equal(1, readsByPosition[new BlockPosition(30, 64, 30)]);
    }

    [Fact]
    public async Task ProbeWorldEditPersistentMismatchDoesNotCreateProofOrResend()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var sendCount = 0;
        var readCount = 0;
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            if (name == "mcc_send_chat")
            {
                sendCount++;
                return Task.FromResult(Result(new { success = true }));
            }

            if (name == "mcc_world_block_at")
            {
                readCount++;
                var position = GetPosition(arguments);
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "Air"
                }));
            }

            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            worldEditVerificationOptions: FastWorldEditOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));
        var worldEdit = report.Find("worldedit")!;

        Assert.Equal(BackendStatus.Unverified, worldEdit.Status);
        Assert.Null(worldEdit.Verification);
        Assert.True(worldEdit.WriteMayHaveBeenDispatched);
        Assert.Equal(2, sendCount);
        Assert.Equal(2, readCount);
        Assert.Contains("最后实际 minecraft:air", worldEdit.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("/fill", report.Results.Select(result => result.Reason));
    }

    [Fact]
    public async Task ProbeSetBlockPollsDelayedVisibilityAndCreatesProof()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var sendCount = 0;
        var readCount = 0;
        var readsByPosition = new Dictionary<BlockPosition, int>();
        var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
        {
            if (name == "mcc_send_chat")
            {
                sendCount++;
            }

            if (name == "mcc_world_block_at")
            {
                readCount++;
                var position = GetPosition(arguments);
                var positionReadCount = readsByPosition.TryGetValue(position, out var previous)
                    ? previous + 1
                    : 1;
                readsByPosition[position] = positionReadCount;
                var block = position == new BlockPosition(10, 64, 10)
                    ? "Stone"
                    : position == new BlockPosition(30, 64, 30) && positionReadCount == 1
                        ? "Air"
                        : "Stone";
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = block
                }));
            }

            return Task.FromResult(name switch
            {
                "mcc_session_status" => Result(new { sessionId = "s1" }),
                "mcc_world_state" => Result(new { dimension = "overworld" }),
                _ => Result(new { success = true })
            });
        });
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            nativeFillVerificationOptions: new NativeFillVerificationOptions
            {
                MaxAttemptsPerSample = 1,
                InitialDelay = TimeSpan.Zero,
                MaximumDelay = TimeSpan.Zero,
                DelayAsync = static (_, _) => Task.CompletedTask
            },
            nativeSetBlockVerificationOptions: FastSetBlockOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Available, report.Find("native-setblock")!.Status);
        Assert.Equal(4, sendCount);
        Assert.Equal(4, readCount);
    }

    [Fact]
    public async Task ProbeDoesNotCreateProofWhenWriteVerificationMismatches()
    {
        var tools = ToolSet(
            "mcc_session_status",
            "mcc_world_state",
            "mcc_send_chat",
            "mcc_world_block_at");
        var fake = new FakeMcpToolInvoker(tools, (name, _, _) => Task.FromResult(name switch
        {
            "mcc_session_status" => Result(new { sessionId = "s1" }),
            "mcc_world_state" => Result(new { dimension = "overworld" }),
            "mcc_world_block_at" => Result(new { block = "minecraft:dirt" }),
            _ => Result(new { success = true })
        }));
        var probe = new CommandCapabilityProbe(
            new MccToolClient(fake),
            nativeSetBlockVerificationOptions: FastSetBlockOptions());

        var report = await probe.ProbeApprovedAsync(new BackendProbeRequest(
            new BlockRange(new BlockPosition(10, 64, 10), new BlockPosition(10, 64, 10)),
            new BlockRange(new BlockPosition(20, 64, 20), new BlockPosition(20, 64, 20)),
            new BlockPosition(30, 64, 30)));

        Assert.Equal(BackendStatus.Unverified, report.Find("worldedit")!.Status);
        Assert.Null(report.Find("worldedit")!.Verification);
    }

    private static NativeSetBlockVerificationOptions FastSetBlockOptions() => new()
    {
        MaxAttemptsPerPlacement = 2,
        OverallTimeout = TimeSpan.FromSeconds(1),
        InitialDelay = TimeSpan.Zero,
        MaximumDelay = TimeSpan.Zero,
        DelayAsync = static (_, _) => Task.CompletedTask
    };

    private static WorldEditVerificationOptions FastWorldEditOptions() => new()
    {
        MaxAttempts = 2,
        OverallTimeout = TimeSpan.FromSeconds(1),
        InitialDelay = TimeSpan.Zero,
        MaximumDelay = TimeSpan.Zero,
        DelayAsync = static (_, _) => Task.CompletedTask
    };

    private static IReadOnlyDictionary<string, McpToolDefinition> ToolSet(
        params string[] names) =>
        names.ToDictionary(
            name => name,
            Definition,
            StringComparer.Ordinal);

    private static McpToolDefinition Definition(string name) =>
        new(name, null, JsonSerializer.SerializeToElement(new { type = "object" }));

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
