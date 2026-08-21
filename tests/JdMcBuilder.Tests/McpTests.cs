using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
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
        var command = new CommandSafety().BuildWorldEditSelection(
            new BlockRange(
                new BlockPosition(0, 64, 0),
                new BlockPosition(1, 65, 1)));

        await client.SendChatAsync(command);
        await client.SessionStatusAsync();

        Assert.Equal(2, calls.Count);
        Assert.Equal("mcc_send_chat", calls[0].Name);
        var chatArguments = JsonSerializer.SerializeToElement(calls[0].Arguments);
        Assert.Equal("//pos 0,64,0 1,65,1", chatArguments.GetProperty("text").GetString());
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
        Assert.Equal(CapabilityStatus.Unavailable, report.Find("native-setblock")!.Status);
    }

    [Fact]
    public void CapabilityDetectorReportsUnverifiedWithCompletePrerequisites()
    {
        var tools = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_send_chat"] = Definition("mcc_send_chat"),
            ["mcc_chat_history"] = Definition("mcc_chat_history"),
            ["mcc_world_block_at"] = Definition("mcc_world_block_at")
        };

        var report = MccCapabilityDetector.Detect(tools);

        Assert.Equal(CapabilityStatus.Unverified, report.Find("worldedit")!.Status);
        Assert.Equal(CapabilityStatus.Unverified, report.Find("native-fill")!.Status);
        Assert.Equal(CapabilityStatus.Unverified, report.Find("native-setblock")!.Status);
    }

    [Fact]
    public void CapabilityDetectorDoesNotRequireChatHistory()
    {
        var tools = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal)
        {
            ["mcc_send_chat"] = Definition("mcc_send_chat"),
            ["mcc_world_block_at"] = Definition("mcc_world_block_at")
        };

        var report = MccCapabilityDetector.Detect(tools);

        Assert.Equal(CapabilityStatus.Unverified, report.Find("native-fill")!.Status);
        Assert.Contains("不影响", report.Find("native-fill")!.Reason, StringComparison.Ordinal);
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
    public void ToolResultExtractsMccWorldBlockMaterial()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(
                new
                {
                    success = true,
                    data = new
                    {
                        x = 1,
                        y = 64,
                        z = 1,
                        material = "Stone",
                        blockId = 1,
                        blockMeta = 0,
                        stateId = 1,
                        properties = new { }
                    }
                }));

        Assert.True(result.TryGetBlockId(out var block));
        Assert.Equal("minecraft:stone", block);
    }

    [Fact]
    public void ToolResultExtractsMaterialFromJsonTextContent()
    {
        var result = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "{\"material\":\"Stone\",\"blockId\":1}"
            })],
            false);

        Assert.True(result.TryGetBlockId(out var block));
        Assert.Equal("minecraft:stone", block);
    }

    [Fact]
    public void ToolResultExtractsReadyChunkStatus()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                success = true,
                data = new
                {
                    location = new { x = 0, y = 64, z = 205 },
                    chunk = new { x = 0, z = 12 },
                    loaded = true,
                    fullyLoaded = true
                }
            }));

        Assert.True(result.TryGetChunkStatus(
            new BlockPosition(0, 64, 205),
            out var sample));
        Assert.Equal(0, sample.ChunkX);
        Assert.Equal(12, sample.ChunkZ);
        Assert.True(sample.Loaded);
        Assert.True(sample.FullyLoaded);
    }

    [Fact]
    public void ToolResultRejectsUnloadedChunkWithWrongCoordinate()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                location = new { x = 48, y = 64, z = 211 },
                chunk = new { x = 3, z = 13 },
                loaded = false,
                fullyLoaded = false
            }));

        Assert.False(result.TryGetChunkStatus(
            new BlockPosition(0, 64, 205),
            out _));
    }

    [Fact]
    public void TargetIdentityIgnoresStaleHumanReadableContent()
    {
        var mcc = new MccToolClient(new FakeMcpToolInvoker(
            new Dictionary<string, McpToolDefinition>(),
            (_, _, _) => throw new InvalidOperationException("No calls expected.")));
        var session = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "{\"sessionId\":\"stale\"}"
            })],
            false,
            JsonSerializer.SerializeToElement(new { sessionId = "current" }));
        var world = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { worldId = "campus", dimension = "overworld" }));

        var fingerprint = TargetFingerprintBuilder.Create(mcc, session, world);

        Assert.NotEmpty(fingerprint);
        Assert.DoesNotContain("stale", fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetIdentityAcceptsRealMccJsonTextContent()
    {
        var mcc = new MccToolClient(new FakeMcpToolInvoker(
            new Dictionary<string, McpToolDefinition>(),
            (_, _, _) => throw new InvalidOperationException("No calls expected.")));
        var session = JsonTextResult(new
        {
            success = true,
            data = new
            {
                host = "127.0.0.1",
                port = 25565,
                username = "MCCBot",
                protocolVersion = 776,
                terrainEnabled = true,
                location = new { x = 80.8, y = 65, z = 9.69 }
            }
        });
        var world = JsonTextResult(new
        {
            success = true,
            data = new
            {
                host = "127.0.0.1",
                port = 25565,
                dimension = "minecraft:overworld",
                loadedChunkCount = 799,
                pendingChunkCount = 0
            }
        });

        var fingerprint = TargetFingerprintBuilder.Create(mcc, session, world);

        Assert.NotEmpty(fingerprint);
        Assert.Equal(
            MachineStringLookupStatus.Found,
            session.TryGetConsistentMachineString(out var host, "host"));
        Assert.Equal("127.0.0.1", host);
    }

    [Fact]
    public void TargetIdentityRejectsConflictingJsonTextCandidates()
    {
        var mcc = new MccToolClient(new FakeMcpToolInvoker(
            new Dictionary<string, McpToolDefinition>(),
            (_, _, _) => throw new InvalidOperationException("No calls expected.")));
        var conflictingSession = new McpToolResult(
            [
                JsonSerializer.SerializeToElement(new
                {
                    type = "text",
                    text = "{\"host\":\"127.0.0.1\"}"
                }),
                JsonSerializer.SerializeToElement(new
                {
                    type = "text",
                    text = "{\"host\":\"127.0.0.2\"}"
                })
            ],
            false);

        var exception = Assert.Throws<BackendException>(() =>
            TargetFingerprintBuilder.Create(
                mcc,
                conflictingSession,
                JsonTextResult(new { dimension = "minecraft:overworld" })));

        Assert.False(exception.Uncertain);
        Assert.Contains("冲突", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetIdentityIgnoresOrdinaryTextContent()
    {
        var result = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "当前连接正常，但没有机器身份 JSON。"
            })],
            false);

        Assert.Equal(
            MachineStringLookupStatus.Missing,
            result.TryGetConsistentMachineString(out _, "host"));
    }

    [Fact]
    public void MachineIdentityWinsOverJsonTextContent()
    {
        var result = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "{\"host\":\"stale-host\"}"
            })],
            false,
            JsonSerializer.SerializeToElement(new { host = "current-host" }));

        Assert.Equal(
            MachineStringLookupStatus.Found,
            result.TryGetConsistentMachineString(out var host, "host"));
        Assert.Equal("current-host", host);
    }

    [Fact]
    public void ConflictingMachineIdentityValuesFailClosed()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { host = "one" }),
            JsonSerializer.SerializeToElement(new { host = "two" }));

        Assert.Equal(
            MachineStringLookupStatus.Conflict,
            result.TryGetConsistentMachineString(out _, "host"));
    }

    [Fact]
    public void ToolResultExtractsMccSampleCoordinates()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                success = true,
                data = new { x = 1, y = 64, z = 3, material = "Stone" }
            }));

        Assert.True(result.TryGetBlockSample(out var block, out var position));
        Assert.Equal("minecraft:stone", block);
        Assert.Equal(new BlockPosition(1, 64, 3), position);
    }

    [Theory]
    [InlineData("OakPlanks", "minecraft:oak_planks")]
    [InlineData("RedstoneBlock", "minecraft:redstone_block")]
    [InlineData("minecraft:OakPlanks", "minecraft:oak_planks")]
    public void ToolResultNormalizesPascalCaseMaterials(string material, string expected)
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { material }));

        Assert.True(result.TryGetBlockId(out var block));
        Assert.Equal(expected, block);
    }

    [Fact]
    public void ToolResultRejectsCoordinateLessBlockSample()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { material = "Stone" }));

        Assert.False(result.TryGetBlockSample(out _, out _));
    }

    [Fact]
    public void ToolResultRejectsConflictingBlockSamples()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                first = new { x = 1, y = 64, z = 3, material = "Stone" },
                second = new { x = 1, y = 64, z = 3, material = "Dirt" }
            }));

        Assert.False(result.TryGetBlockSample(out _, out _));
    }

    [Fact]
    public void ToolResultRejectsConflictingBlockAliasesInOneSample()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                x = 1,
                y = 64,
                z = 3,
                material = "Stone",
                block = "Dirt"
            }));

        Assert.False(result.TryGetBlockSample(out _, out _));
    }

    [Fact]
    public void ToolResultAcceptsEquivalentBlockAliasesInOneSample()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new
            {
                x = 1,
                y = 64,
                z = 3,
                material = "Stone",
                block = "minecraft:stone"
            }));

        Assert.True(result.TryGetBlockSample(out var block, out var position));
        Assert.Equal("minecraft:stone", block);
        Assert.Equal(new BlockPosition(1, 64, 3), position);
    }

    [Fact]
    public void ToolResultDoesNotInheritWrapperCoordinatesIntoBareContentJson()
    {
        var result = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "{\"material\":\"Stone\"}"
            })],
            false,
            JsonSerializer.SerializeToElement(new
            {
                x = 1,
                y = 64,
                z = 3,
                content = new { material = "Stone" }
            }));

        Assert.False(result.TryGetBlockSample(out _, out _));
    }

    [Fact]
    public void ToolResultInspectorRejectsFalseJsonTextEnvelope()
    {
        var result = new McpToolResult(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = "{\"success\":false,\"errorCode\":\"permission_denied\"}"
            })],
            false);

        Assert.Equal(McpFailureKind.PermissionDenied, McpToolResultInspector.ClassifyFailure(result));
    }

    [Fact]
    public void ToolResultDoesNotInferCanonicalIdFromNumericMetadata()
    {
        var result = new McpToolResult(
            [],
            false,
            JsonSerializer.SerializeToElement(new { blockId = 1, blockMeta = 0 }));

        Assert.False(result.TryGetBlockId(out _));
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

    private static McpToolResult JsonTextResult<T>(T value) =>
        new(
            [JsonSerializer.SerializeToElement(new
            {
                type = "text",
                text = JsonSerializer.Serialize(value)
            })],
            false);

    private static McpToolDefinition Definition(string name) =>
        new(name, null, JsonSerializer.SerializeToElement(new { type = "object" }));
}
