using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record WorldEditVerificationOptions
{
    public int MaxAttempts { get; init; } = 6;
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    internal BlockRangeVerificationOptions ToRangeOptions()
    {
        var options = new BlockRangeVerificationOptions
        {
            MaxAttemptsPerSample = MaxAttempts,
            OverallTimeout = OverallTimeout,
            InitialDelay = InitialDelay,
            MaximumDelay = MaximumDelay,
            DelayAsync = DelayAsync
        };
        options.Validate();
        return options;
    }

    internal void Validate() => _ = ToRangeOptions();
}

/// <summary>
/// Verifies one already-dispatched WorldEdit selection using bounded, read-only
/// corner polling. This class never resends //pos or //set.
/// </summary>
public sealed class WorldEditVerifier
{
    private readonly BlockRangeVerifier _rangeVerifier;
    private readonly CommandSafety _safety;

    public WorldEditVerifier(
        MccToolClient mcc,
        WorldEditVerificationOptions? options = null,
        CommandSafety? safety = null)
        : this(
            new BlockReadbackVerifier(mcc),
            options,
            safety)
    {
    }

    public WorldEditVerifier(
        BlockReadbackVerifier readback,
        WorldEditVerificationOptions? options = null,
        CommandSafety? safety = null)
    {
        ArgumentNullException.ThrowIfNull(readback);
        var configured = options ?? new WorldEditVerificationOptions();
        _rangeVerifier = new BlockRangeVerifier(readback, configured.ToRangeOptions());
        _safety = safety ?? new CommandSafety();
    }

    public Task<BlockRangeVerificationResult> VerifyAsync(
        BlockRange range,
        string expectedBlock,
        CancellationToken cancellationToken = default) =>
        _rangeVerifier.VerifyAsync(
            BlockRangeVerificationPlan.Create(range, expectedBlock, _safety),
            cancellationToken);

    public async Task VerifyAsync(
        BlockPosition position,
        string expectedBlock,
        CancellationToken cancellationToken = default) =>
        _ = await VerifyAsync(
            new BlockRange(position, position),
            expectedBlock,
            cancellationToken).ConfigureAwait(false);
}
