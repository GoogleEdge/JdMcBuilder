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
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
