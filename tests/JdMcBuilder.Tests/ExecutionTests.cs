using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Execution;

namespace JdMcBuilder.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public async Task DryRunDoesNotInvokeBackendsAndReportsProgress()
    {
        var backend = new RecordingBackend(BackendCapabilities.CreateVerifiedForTesting("worldedit", "WorldEdit", true, false, "test", "test-target"));
        var journalPath = Path.Combine(Path.GetTempPath(), $"jdmc-dry-{Guid.NewGuid():N}.json");
        try
        {
            var executor = new BuildExecutor(
                [backend],
                new BackendSelector(),
                new BuildJournal(journalPath),
                new BuildExecutionOptions(DryRun: true));
            var batches = new BuildBatch[]
            {
                new FillBatch("p/o/batch-0000", "p", "o", new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(1, 0, 1)), "minecraft:stone")
            };

            var progress = new List<BuildProgress>();
            executor.Progress += (_, item) => progress.Add(item);
            var state = await executor.ExecuteAsync("sha256:test", batches);

            Assert.Empty(backend.Calls);
            Assert.Single(progress);
            Assert.Equal("dry-run", state.BackendId);
            Assert.Equal(4, progress[0].CompletedBlocks);
        }
        finally
        {
            File.Delete(journalPath);
        }
    }

    [Fact]
    public void RealExecutionRequiresTargetFingerprint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jdmc-options-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Throws<ArgumentException>(() => new BuildExecutor(
                [],
                new BackendSelector(),
                new BuildJournal(path),
                new BuildExecutionOptions(DryRun: false)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecutorPersistsCompletedBatchAndResumesWithoutRepeatingIt()
    {
        var backend = new RecordingBackend(BackendCapabilities.CreateVerifiedForTesting("worldedit", "WorldEdit", true, false, "test", "test-target"));
        var journalPath = Path.Combine(Path.GetTempPath(), $"jdmc-build-{Guid.NewGuid():N}.json");
        try
        {
            var batches = new BuildBatch[]
            {
                new FillBatch("p/o/batch-0000", "p", "o", new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)), "minecraft:stone"),
                new FillBatch("p/o/batch-0001", "p", "o", new BlockRange(new BlockPosition(1, 0, 0), new BlockPosition(1, 0, 0)), "minecraft:stone")
            };
            var executor = new BuildExecutor(
                [backend],
                new BackendSelector(),
                new BuildJournal(journalPath),
                new BuildExecutionOptions(
                    DryRun: false,
                    TargetFingerprint: "test-target"));

            await executor.ExecuteAsync("sha256:test", batches);
            await executor.ExecuteAsync("sha256:test", batches);

            Assert.Equal(2, backend.Calls.Count);
        }
        finally
        {
            File.Delete(journalPath);
        }
    }

    [Fact]
    public void AvailableCapabilityRequiresTargetBoundVerification()
    {
        Assert.Throws<ArgumentException>(() => new BackendCapabilities(
            "worldedit",
            "WorldEdit",
            BackendStatus.Available,
            true,
            false,
            "test"));
    }

    [Fact]
    public void SelectorDoesNotTreatUnverifiedCapabilityAsAvailable()
    {
        var backend = new RecordingBackend(new BackendCapabilities(
            "worldedit",
            "WorldEdit",
            BackendStatus.Unverified,
            true,
            false,
            "test"));
        var batch = new FillBatch(
            "p/o/batch-0000",
            "p",
            "o",
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
            "minecraft:stone");

        Assert.Null(new BackendSelector().Select([backend], batch));
        Assert.Same(
            backend,
            new BackendSelector().Select(
                [backend],
                batch,
                allowUnverified: true,
                targetFingerprint: null));
    }

    [Fact]
    public async Task ExecutorMarksPostDispatchFailureAsUncertainAndDoesNotRetry()
    {
        var backend = new FailingBackend(
            BackendCapabilities.CreateVerifiedForTesting("worldedit", "WorldEdit", true, false, "test", "test-target"));
        var journalPath = Path.Combine(Path.GetTempPath(), $"jdmc-uncertain-{Guid.NewGuid():N}.json");
        try
        {
            var executor = new BuildExecutor(
                [backend],
                new BackendSelector(),
                new BuildJournal(journalPath),
                new BuildExecutionOptions(
                    DryRun: false,
                    MaxRetries: 2,
                    TargetFingerprint: "test-target"));
            var batch = new FillBatch(
                "p/o/batch-0000",
                "p",
                "o",
                new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
                "minecraft:stone");

            var exception = await Assert.ThrowsAsync<BackendException>(() =>
                executor.ExecuteAsync("sha256:test", [batch]));

            Assert.True(exception.Uncertain);
            Assert.Equal(1, backend.Calls);
            var journal = await new BuildJournal(journalPath).LoadAsync();
            Assert.Contains(batch.BatchId, journal!.UncertainBatches);
        }
        finally
        {
            File.Delete(journalPath);
        }
    }

    private sealed class FailingBackend : IBuildBackend
    {
        public FailingBackend(BackendCapabilities capabilities) => Capabilities = capabilities;
        public BackendCapabilities Capabilities { get; }
        public int Calls { get; private set; }

        public Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new BackendException("write response lost", uncertain: true);
        }
    }

    private sealed class RecordingBackend : IBuildBackend
    {
        public RecordingBackend(BackendCapabilities capabilities) => Capabilities = capabilities;
        public BackendCapabilities Capabilities { get; }
        public List<string> Calls { get; } = [];

        public Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default)
        {
            Calls.Add(batch.BatchId);
            return Task.FromResult(new BackendOperationResult(batch.BatchId, true, false, "ok", batch.BlockCount, []));
        }
    }
}
