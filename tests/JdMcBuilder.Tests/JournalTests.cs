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
}
