using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Execution;

namespace JdMcBuilder.Tests;

public sealed class JournalTests
{
    [Fact]
    public async Task JournalRoundTripsStateAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jdmc-journal-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new BuildJournal(path);
            var state = BuildJournalState.Create("sha256:test", "worldedit", "target-1") with
            {
                CompletedBatches = ["p/o/batch-0000"],
                UncertainBatches = [],
                LastError = null
            };

            await journal.SaveAsync(state);
            var loaded = await journal.LoadAsync();

            Assert.NotNull(loaded);
            Assert.Equal(state.BlueprintHash, loaded!.BlueprintHash);
            Assert.Equal(state.CompletedBatches, loaded.CompletedBatches);
            Assert.Equal("target-1", loaded.TargetFingerprint);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.*.tmp"));
        }
        finally
        {
            File.Delete(path);
            var archiveDirectory = Path.Combine(Path.GetDirectoryName(path)!, "archives");
            if (Directory.Exists(archiveDirectory))
            {
                Directory.Delete(archiveDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArchiveStaleAndResetArchivesExactStateAndLeavesActiveJournalMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jdmc-archive-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            var state = new BuildJournalState(
                "session-old",
                "sha256:old",
                "minecraft:overworld",
                "worldedit",
                ["phase/batch-0000"],
                [],
                "old checkpoint",
                "target-1");
            await journal.SaveAsync(state);
            var originalBytes = await File.ReadAllBytesAsync(path);
            var snapshot = await journal.ReadSnapshotAsync();

            var result = await journal.ArchiveStaleAndResetAsync(snapshot!, "sha256:new");

            Assert.Equal(JournalArchiveStatus.Archived, result.Status);
            Assert.True(result.Archived);
            Assert.NotNull(result.ArchivePath);
            Assert.False(File.Exists(path));
            Assert.Null(await journal.LoadAsync());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(result.ArchivePath!));
            Assert.Equal(state.SessionId, result.State!.SessionId);
            Assert.Equal(state.BlueprintHash, result.State.BlueprintHash);
            Assert.Equal(state.CompletedBatches, result.State.CompletedBatches);
            Assert.Equal(state.UncertainBatches, result.State.UncertainBatches);
            Assert.Equal(state.LastError, result.State.LastError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveStaleAndResetRefusesSameHashWithoutChangingJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jdmc-archive-same-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            await journal.SaveAsync(BuildJournalState.Create("sha256:same", "worldedit"));
            var originalBytes = await File.ReadAllBytesAsync(path);
            var snapshot = await journal.ReadSnapshotAsync();

            var result = await journal.ArchiveStaleAndResetAsync(snapshot!, "sha256:same");

            Assert.Equal(JournalArchiveStatus.NotStale, result.Status);
            Assert.Null(result.ArchivePath);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveStaleAndResetRefusesChangedSnapshotAndUncertainState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jdmc-archive-guard-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            await journal.SaveAsync(BuildJournalState.Create("sha256:old", "worldedit"));
            var snapshot = await journal.ReadSnapshotAsync();
            await journal.SaveAsync(BuildJournalState.Create("sha256:changed", "worldedit"));

            var changed = await journal.ArchiveStaleAndResetAsync(snapshot!, "sha256:new");
            Assert.Equal(JournalArchiveStatus.ChangedSinceSnapshot, changed.Status);
            Assert.True(File.Exists(path));

            await journal.SaveAsync(BuildJournalState.Create("sha256:old", "worldedit") with
            {
                UncertainBatches = ["phase/batch-0001"],
                LastError = "write response lost"
            });
            var uncertainSnapshot = await journal.ReadSnapshotAsync();
            var blocked = await journal.ArchiveStaleAndResetAsync(uncertainSnapshot!, "sha256:new");

            Assert.Equal(JournalArchiveStatus.BlockedByUncertain, blocked.Status);
            Assert.Null(blocked.ArchivePath);
            Assert.True(File.Exists(path));
            Assert.Contains("phase/batch-0001", blocked.State!.UncertainBatches);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveUncertainBatchMovesOnlyNamedBatchAfterFreshCornerConfirmation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-resolve-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            var target = "site-roads/road-edge-001/batch-0000";
            var state = new BuildJournalState(
                "session-1",
                "sha256:test",
                "minecraft:overworld",
                "worldedit",
                ["already/done/batch-0000"],
                [target, "other/uncertain/batch-0000"],
                "response lost",
                "target-1");
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var confirmation = Confirmation(snapshot!.State, target);

            var result = await journal.ResolveUncertainBatchAsync(
                snapshot,
                "sha256:test",
                confirmation);

            Assert.True(result.Resolved);
            Assert.Equal(
                ["already/done/batch-0000", target],
                result.State!.CompletedBatches);
            Assert.Equal(
                ["other/uncertain/batch-0000"],
                result.State.UncertainBatches);
            Assert.Null(result.State.LastError);
            Assert.Equal(state.SessionId, result.State.SessionId);
            Assert.Equal(state.Dimension, result.State.Dimension);
            Assert.Equal(state.BackendId, result.State.BackendId);
            Assert.Equal(state.TargetFingerprint, result.State.TargetFingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveUncertainBatchRejectsChangedRevisionWithoutRewritingBytes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-resolve-conflict-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            const string target = "site-roads/road-edge-001/batch-0000";
            var state = BuildJournalState.Create(
                "sha256:test",
                "worldedit",
                "target-1") with
            {
                UncertainBatches = [target],
                LastError = "response lost"
            };
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            await journal.SaveAsync(state with { LastError = "external update" });
            var before = await File.ReadAllBytesAsync(path);

            var result = await journal.ResolveUncertainBatchAsync(
                snapshot!,
                "sha256:test",
                Confirmation(snapshot!.State, target));

            Assert.Equal(
                JournalUncertainResolutionStatus.ChangedSinceSnapshot,
                result.Status);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveUncertainBatchRejectsCallerTamperedSnapshotState(
        bool tamperDimension)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-resolve-snapshot-state-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            const string target = "site-roads/road-edge-001/batch-0000";
            var state = BuildJournalState.Create(
                "sha256:test",
                "worldedit",
                "target-1") with
            {
                Dimension = "minecraft:overworld",
                UncertainBatches = [target],
                LastError = "response lost"
            };
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var original = await File.ReadAllBytesAsync(path);
            var tamperedState = tamperDimension
                ? snapshot!.State with { Dimension = "minecraft:the_nether" }
                : snapshot!.State with { LastError = "forged caller state" };
            var tamperedSnapshot = snapshot with { State = tamperedState };

            var result = await journal.ResolveUncertainBatchAsync(
                tamperedSnapshot,
                "sha256:test",
                Confirmation(tamperedState, target));

            Assert.Equal(
                JournalUncertainResolutionStatus.ChangedSinceSnapshot,
                result.Status);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveUncertainBatchRejectsIncompleteProofWithoutRewritingBytes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-resolve-proof-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            const string target = "site-roads/road-edge-001/batch-0000";
            var state = BuildJournalState.Create(
                "sha256:test",
                "worldedit",
                "target-1") with
            {
                UncertainBatches = [target],
                LastError = "response lost"
            };
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var before = await File.ReadAllBytesAsync(path);
            var valid = Confirmation(snapshot!.State, target);
            var invalid = valid with
            {
                Observations = valid.Observations.Skip(1).ToArray()
            };

            var result = await journal.ResolveUncertainBatchAsync(
                snapshot,
                "sha256:test",
                invalid);

            Assert.Equal(
                JournalUncertainResolutionStatus.ConfirmationMismatch,
                result.Status);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSameSnapshotResolutionAllowsOnlyOneCommit()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jdmc-resolve-concurrent-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            const string target = "site-roads/road-edge-001/batch-0000";
            var state = BuildJournalState.Create(
                "sha256:test",
                "worldedit",
                "target-1") with
            {
                UncertainBatches = [target],
                LastError = "response lost"
            };
            await journal.SaveAsync(state);
            var snapshot = await journal.ReadSnapshotAsync();
            var confirmation = Confirmation(snapshot!.State, target);

            var results = await Task.WhenAll(
                journal.ResolveUncertainBatchAsync(
                    snapshot,
                    "sha256:test",
                    confirmation),
                journal.ResolveUncertainBatchAsync(
                    snapshot,
                    "sha256:test",
                    confirmation));

            Assert.Single(results, result => result.Resolved);
            Assert.Single(
                results,
                result => result.Status
                    == JournalUncertainResolutionStatus.ChangedSinceSnapshot);
            var final = await journal.LoadAsync();
            Assert.Equal([target], final!.CompletedBatches);
            Assert.Empty(final.UncertainBatches);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveStaleAndResetHonorsCancellationBeforeMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jdmc-archive-cancel-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "build-journal.json");
        try
        {
            var journal = new BuildJournal(path);
            await journal.SaveAsync(BuildJournalState.Create("sha256:old", "worldedit"));
            var originalBytes = await File.ReadAllBytesAsync(path);
            var snapshot = await journal.ReadSnapshotAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                journal.ArchiveStaleAndResetAsync(snapshot!, "sha256:new", cancellation.Token));

            Assert.True(File.Exists(path));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.False(Directory.Exists(Path.Combine(directory, "archives")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FreshBatchConfirmation Confirmation(
        BuildJournalState state,
        string batchId)
    {
        var range = new BlockRange(
            new BlockPosition(0, 64, 205),
            new BlockPosition(48, 64, 211));
        var positions = new[]
        {
            new BlockPosition(0, 64, 205),
            new BlockPosition(0, 64, 211),
            new BlockPosition(48, 64, 205),
            new BlockPosition(48, 64, 211)
        };
        var started = DateTimeOffset.UtcNow;
        return new FreshBatchConfirmation(
            batchId,
            state.SessionId,
            state.BlueprintHash,
            state.BackendId,
            state.TargetFingerprint!,
            range,
            "minecraft:gray_concrete",
            started,
            started.AddSeconds(1),
            positions.Select(position => new FreshBatchSampleConfirmation(
                position,
                position,
                "minecraft:gray_concrete",
                "minecraft:gray_concrete",
                1,
                true)).ToArray());
    }
}
