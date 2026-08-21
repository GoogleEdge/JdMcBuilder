using System.Text.Json;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Execution;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Tests;

public sealed class BuildRecoveryTests
{
    [Fact]
    public async Task RecoveryUsesOnlyReadOnlyIdentityAndBlockToolsAndResolvesJournal()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = new BuildJournal(path);
            var state = new BuildJournalState(
                "session-1",
                "sha256:test",
                "minecraft:overworld",
                "worldedit",
                [],
                [batchId],
                "response lost",
                "target-1");
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var calls = new List<string>();
            var tools = Tools(
                "mcc_world_block_at",
                "mcc_session_status",
                "mcc_world_state",
                "mcc_server_info",
                "mcc_send_chat");
            var fake = new FakeMcpToolInvoker(tools, (name, arguments, _) =>
            {
                calls.Add(name);
                Assert.NotEqual("mcc_send_chat", name);
                if (name == "mcc_world_block_at")
                {
                    var position = Position(arguments);
                    return Task.FromResult(Result(new
                    {
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        material = "GrayConcrete"
                    }));
                }

                return Task.FromResult(Result(new { success = true }));
            });
            var mcc = new MccToolClient(fake);
            var range = new BlockRange(
                new BlockPosition(0, 64, 205),
                new BlockPosition(48, 64, 211));
            var batch = new FillBatch(
                batchId,
                "site-roads",
                "road-edge-001",
                range,
                "minecraft:gray_concrete");
            var service = new BuildRecoveryService(mcc: mcc, journal: journal);
            var request = new BuildRecoveryRequest(
                batchId,
                "sha256:test",
                snapshot!,
                [batch],
                _ => Task.FromResult("sha256:test"),
                _ => Task.FromResult("target-1"),
                () => true);

            var result = await service.ResolveAsync(request);

            Assert.True(result.Resolved);
            Assert.Equal(4, calls.Count(name => name == "mcc_world_block_at"));
            Assert.DoesNotContain("mcc_send_chat", calls);
            Assert.Equal([batchId], result.State!.CompletedBatches);
            Assert.Empty(result.State.UncertainBatches);
            Assert.Null(result.State.LastError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryMismatchLeavesExactJournalBytesUnchanged()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-fail-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = new BuildJournal(path);
            var state = new BuildJournalState(
                "session-1",
                "sha256:test",
                "minecraft:overworld",
                "worldedit",
                [],
                [batchId],
                "response lost",
                "target-1");
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var tools = Tools("mcc_world_block_at");
            var fake = new FakeMcpToolInvoker(tools, (_, arguments, _) =>
            {
                var position = Position(arguments);
                return Task.FromResult(Result(new
                {
                    x = position.X,
                    y = position.Y,
                    z = position.Z,
                    material = "Air"
                }));
            });
            var mcc = new MccToolClient(fake);
            var batch = new FillBatch(
                batchId,
                "site-roads",
                "road-edge-001",
                new BlockRange(
                    new BlockPosition(0, 64, 205),
                    new BlockPosition(48, 64, 211)),
                "minecraft:gray_concrete");
            var service = new BuildRecoveryService(
                journal,
                mcc,
                new JdMcBuilder.Backends.BlockRangeVerificationOptions
                {
                    MaxAttemptsPerSample = 1,
                    OverallTimeout = TimeSpan.FromSeconds(1),
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    DelayAsync = static (_, _) => Task.CompletedTask
                });

            var result = await service.ResolveAsync(new BuildRecoveryRequest(
                batchId,
                "sha256:test",
                snapshot!,
                [batch],
                _ => Task.FromResult("sha256:test"),
                _ => Task.FromResult("target-1"),
                () => true));

            Assert.Equal(BuildRecoveryStatus.VerificationFailed, result.Status);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Contains("minecraft:air", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryCancellationDuringReadReturnsCancelledAndPreservesJournal()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-cancel-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var readStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = new FakeMcpToolInvoker(
                Tools("mcc_world_block_at"),
                async (_, _, cancellationToken) =>
                {
                    readStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));
            using var cancellation = new CancellationTokenSource();

            var resolving = service.ResolveAsync(
                Request(batchId, snapshot!, () => true),
                cancellation.Token);
            await readStarted.Task;
            cancellation.Cancel();
            var result = await resolving;

            Assert.Equal(BuildRecoveryStatus.Cancelled, result.Status);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsMalformedWorldBlockSchemaBeforeAnyWorldRead()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-schema-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var calls = 0;
            var malformedSchema = JsonSerializer.SerializeToElement(new
            {
                properties = new
                {
                    x = new { type = "integer" },
                    y = new { type = "integer" },
                    z = new { type = "integer" }
                },
                required = new[] { "x", "y", "z" }
            });
            var fake = new FakeMcpToolInvoker(
                ToolsWithWorldBlockSchema(
                    malformedSchema,
                    "mcc_world_block_at",
                    "mcc_send_chat",
                    "mcc_run_internal_command",
                    "mcc_place_block"),
                (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult(Result(new { success = true }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));

            var result = await service.ResolveAsync(
                Request(batchId, snapshot!, () => true));

            Assert.Equal(BuildRecoveryStatus.Rejected, result.Status);
            Assert.Equal(0, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryRejectsExtraWorldBlockSchemaContractBeforeAnyWorldRead(
        bool extraPropertyIsRequired)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-extra-schema-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var calls = 0;
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    x = new { type = "integer" },
                    y = new { type = "integer" },
                    z = new { type = "integer" },
                    dimension = new { type = "string" }
                },
                required = extraPropertyIsRequired
                    ? new[] { "x", "y", "z", "dimension" }
                    : new[] { "x", "y", "z" }
            });
            var fake = new FakeMcpToolInvoker(
                ToolsWithWorldBlockSchema(
                    schema,
                    "mcc_world_block_at",
                    "mcc_send_chat"),
                (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult(Result(new { success = true }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));

            var result = await service.ResolveAsync(
                Request(batchId, snapshot!, () => true));

            Assert.Equal(BuildRecoveryStatus.Rejected, result.Status);
            Assert.Equal(0, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsConnectionChangedBeforeIdentityReadWithoutWorldRead()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-identity-connection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var reads = 0;
            var fingerprintReads = 0;
            var connectionChecks = 0;
            var fake = new FakeMcpToolInvoker(
                Tools("mcc_world_block_at"),
                (_, _, _) =>
                {
                    reads++;
                    return Task.FromResult(Result(new { success = true }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));
            var baseline = Request(batchId, snapshot!, () => ++connectionChecks < 2);
            var request = baseline with
            {
                ReadTargetFingerprintAsync = _ =>
                {
                    fingerprintReads++;
                    return Task.FromResult("target-1");
                }
            };

            var result = await service.ResolveAsync(request);

            Assert.Equal(BuildRecoveryStatus.ConnectionChanged, result.Status);
            Assert.Equal(0, fingerprintReads);
            Assert.Equal(0, reads);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsMalformedPlannedBatchBeforeWorldRead()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-invalid-plan-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var calls = 0;
            var fake = new FakeMcpToolInvoker(
                Tools("mcc_world_block_at", "mcc_send_chat"),
                (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult(Result(new { success = true }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));
            var invalid = new FillBatch(
                batchId,
                " ",
                "road-edge-001",
                new BlockRange(
                    new BlockPosition(0, 64, 205),
                    new BlockPosition(48, 64, 211)),
                "minecraft:gray_concrete");
            var request = Request(batchId, snapshot!, () => true) with
            {
                PlannedBatches = [invalid]
            };

            var result = await service.ResolveAsync(request);

            Assert.Equal(BuildRecoveryStatus.Rejected, result.Status);
            Assert.Equal(0, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsConnectionChangedBeforeSamplingWithoutWorldRead()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-preconnection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var reads = 0;
            var connectionChecks = 0;
            var fake = new FakeMcpToolInvoker(
                Tools("mcc_world_block_at"),
                (_, _, _) =>
                {
                    reads++;
                    return Task.FromResult(Result(new { success = true }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));

            var result = await service.ResolveAsync(Request(
                batchId,
                snapshot!,
                () => ++connectionChecks < 2));

            Assert.Equal(BuildRecoveryStatus.ConnectionChanged, result.Status);
            Assert.Equal(0, reads);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsChangedConnectionAfterSamplingAndPreservesJournal()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-recovery-connection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            const string batchId = "site-roads/road-edge-001/batch-0000";
            var journal = await CreateJournalAsync(path, batchId);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var reads = 0;
            var connectionCurrent = true;
            var fake = new FakeMcpToolInvoker(
                Tools(
                    "mcc_world_block_at",
                    "mcc_send_chat",
                    "mcc_run_internal_command",
                    "mcc_place_block",
                    "mcc_select_item",
                    "mcc_quit_client"),
                (name, arguments, _) =>
                {
                    Assert.Equal("mcc_world_block_at", name);
                    reads++;
                    var position = Position(arguments);
                    if (reads == 4)
                    {
                        connectionCurrent = false;
                    }

                    return Task.FromResult(Result(new
                    {
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        material = "GrayConcrete"
                    }));
                });
            var service = new BuildRecoveryService(journal, new MccToolClient(fake));

            var result = await service.ResolveAsync(
                Request(batchId, snapshot!, () => connectionCurrent));

            Assert.Equal(BuildRecoveryStatus.ConnectionChanged, result.Status);
            Assert.Equal(4, reads);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<BuildJournal> CreateJournalAsync(
        string path,
        string batchId)
    {
        var journal = new BuildJournal(path);
        await journal.SaveAsync(new BuildJournalState(
            "session-1",
            "sha256:test",
            "minecraft:overworld",
            "worldedit",
            [],
            [batchId],
            "response lost",
            "target-1"));
        return journal;
    }

    private static BuildRecoveryRequest Request(
        string batchId,
        BuildJournalSnapshot snapshot,
        Func<bool> isConnectionCurrent) =>
        new(
            batchId,
            "sha256:test",
            snapshot,
            [new FillBatch(
                batchId,
                "site-roads",
                "road-edge-001",
                new BlockRange(
                    new BlockPosition(0, 64, 205),
                    new BlockPosition(48, 64, 211)),
                "minecraft:gray_concrete")],
            _ => Task.FromResult("sha256:test"),
            _ => Task.FromResult("target-1"),
            isConnectionCurrent);

    private static IReadOnlyDictionary<string, McpToolDefinition> Tools(
        params string[] names) =>
        ToolsWithWorldBlockSchema(
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    x = new { type = "integer" },
                    y = new { type = "integer" },
                    z = new { type = "integer" }
                },
                required = new[] { "x", "y", "z" }
            }),
            names);

    private static IReadOnlyDictionary<string, McpToolDefinition>
        ToolsWithWorldBlockSchema(
            JsonElement schema,
            params string[] names) =>
        names.ToDictionary(
            name => name,
            name => new McpToolDefinition(
                name,
                null,
                name == "mcc_world_block_at"
                    ? schema
                    : JsonSerializer.SerializeToElement(new { type = "object" })),
            StringComparer.Ordinal);

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
