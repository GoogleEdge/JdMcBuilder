using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Execution;

public sealed record FreshBatchSampleConfirmation(
    BlockPosition RequestedPosition,
    BlockPosition? ReturnedPosition,
    string ExpectedBlock,
    string? ActualBlock,
    int Attempts,
    bool Verified);

public sealed record FreshBatchConfirmation(
    string BatchId,
    string SessionId,
    string BlueprintHash,
    string BackendId,
    string TargetFingerprint,
    BlockRange Range,
    string ExpectedBlock,
    DateTimeOffset SamplingStartedAtUtc,
    DateTimeOffset SamplingCompletedAtUtc,
    IReadOnlyList<FreshBatchSampleConfirmation> Observations)
{
    public static FreshBatchConfirmation Create(
        string batchId,
        BuildJournalState state,
        DateTimeOffset samplingStartedAtUtc,
        DateTimeOffset samplingCompletedAtUtc,
        BlockRangeVerificationResult verification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(verification);
        return new FreshBatchConfirmation(
            batchId,
            state.SessionId,
            state.BlueprintHash,
            state.BackendId,
            state.TargetFingerprint ?? string.Empty,
            verification.Plan.Range,
            verification.Plan.ExpectedBlock,
            samplingStartedAtUtc,
            samplingCompletedAtUtc,
            verification.Observations.Select(item => new FreshBatchSampleConfirmation(
                item.RequestedPosition,
                item.LastReturnedPosition,
                verification.Plan.ExpectedBlock,
                item.LastObservedBlock,
                item.Attempts,
                item.Verified)).ToArray());
    }
}

public enum JournalUncertainResolutionStatus
{
    Missing,
    Resolved,
    ChangedSinceSnapshot,
    BlueprintMismatch,
    ConfirmationMismatch,
    NotUncertain,
    InvalidState
}

public sealed record JournalUncertainResolutionResult(
    JournalUncertainResolutionStatus Status,
    BuildJournalState? State,
    string Message)
{
    public bool Resolved => Status == JournalUncertainResolutionStatus.Resolved;
}

public enum BuildRecoveryStatus
{
    Resolved,
    Rejected,
    VerificationFailed,
    TargetChanged,
    BlueprintChanged,
    ConnectionChanged,
    Conflict,
    Cancelled
}

public sealed record BuildRecoveryResult(
    BuildRecoveryStatus Status,
    string BatchId,
    string Message,
    BuildJournalState? State = null,
    string? InitialTargetFingerprint = null,
    string? FinalTargetFingerprint = null,
    BlockRangeVerificationResult? Verification = null)
{
    public bool Resolved => Status == BuildRecoveryStatus.Resolved;
}

public sealed record BuildRecoveryRequest(
    string BatchId,
    string ImportedBlueprintHash,
    BuildJournalSnapshot Snapshot,
    IReadOnlyList<BuildBatch> PlannedBatches,
    Func<CancellationToken, Task<string>> ReadBlueprintHashAsync,
    Func<CancellationToken, Task<string>> ReadTargetFingerprintAsync,
    Func<bool> IsConnectionCurrent);
