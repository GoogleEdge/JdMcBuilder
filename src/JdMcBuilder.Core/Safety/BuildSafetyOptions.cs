using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Core.Safety;

public sealed record BuildSafetyOptions
{
    public BlockRange? AllowedRegion { get; init; }
    public long MaxBlocksPerOperation { get; init; } = 100_000;
    public int MaxPayloadBytes { get; init; } = 512 * 1024;
    public int MaxCoordinateAbsoluteValue { get; init; } = 30_000_000;
    public bool RequireDryRun { get; init; } = true;
    public bool RequireConfirmationForLargePhase { get; init; } = true;
    public long LargePhaseThreshold { get; init; } = 20_000;
}
