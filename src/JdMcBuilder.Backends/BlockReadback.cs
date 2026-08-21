using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public enum BlockReadinessStatus
{
    Unknown,
    Ready,
    Unavailable,
    Invalid
}

public sealed record BlockReadinessObservation(
    BlockReadinessStatus Status,
    McpChunkStatusSample? Sample,
    string? FailureReason)
{
    public static BlockReadinessObservation Ready(McpChunkStatusSample sample) =>
        new(BlockReadinessStatus.Ready, sample, null);

    public static BlockReadinessObservation Unavailable(string reason, McpChunkStatusSample? sample = null) =>
        new(BlockReadinessStatus.Unavailable, sample, reason);

    public static BlockReadinessObservation Invalid(string reason) =>
        new(BlockReadinessStatus.Invalid, null, reason);
}

public sealed record BlockReadbackObservation(
    BlockPosition RequestedPosition,
    string? BlockId,
    BlockPosition? ReturnedPosition,
    string? FailureReason,
    BlockReadinessObservation? Readiness = null)
{
    public bool ReadinessUnavailable =>
        Readiness?.Status == BlockReadinessStatus.Unavailable;

    public bool IsValid =>
        FailureReason is null
        && BlockId is not null
        && ReturnedPosition is { } position
        && position == RequestedPosition;

    public bool Matches(string expectedBlock) =>
        IsValid
        && string.Equals(BlockId, expectedBlock, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Performs one coordinate-bound, read-only MCC block observation.
/// Retry policy deliberately belongs to the caller so /fill and /setblock
/// can keep different verification contracts.
/// </summary>
public sealed class BlockReadbackVerifier
{
    private readonly MccToolClient _mcc;
    private readonly bool _requireReadyChunk;
    private readonly string? _boundContextFingerprint;

    public BlockReadbackVerifier(
        MccToolClient mcc,
        bool requireReadyChunk = false)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        // Presence of the readiness tool is itself a safety signal: callers
        // must not accidentally bypass the loaded/fullyLoaded gate by passing
        // an externally constructed default verifier.
        _requireReadyChunk = requireReadyChunk || mcc.HasTool("mcc_chunk_status");
        _boundContextFingerprint = mcc.ContextFingerprint;
    }

    public bool RequiresReadyChunk => _requireReadyChunk;

    private void ValidateSessionBinding()
    {
        if (_mcc.Endpoint is null)
        {
            return;
        }

        if (_boundContextFingerprint is null)
        {
            throw new McpException(
                McpFailureKind.SessionExpired,
                "MCP transport 缺少稳定 session context，无法绑定方块读回证据；不重发写入。 ");
        }

        if (!string.Equals(
                _boundContextFingerprint,
                _mcc.ContextFingerprint,
                StringComparison.Ordinal))
        {
            throw new McpException(
                McpFailureKind.SessionExpired,
                "MCP transport session 或 endpoint context 在方块读取期间发生变化；丢弃读回证据，不重发写入。 ");
        }
    }

    public async Task<BlockReadbackObservation> ReadOnceAsync(
        BlockPosition position,
        CancellationToken cancellationToken = default)
    {
        // Bind every observation, including readiness-only observations, to the
        // same live MCP transport context. A real MCP client without a session
        // ID cannot provide evidence that is safe to associate with a target.
        ValidateSessionBinding();

        BlockReadinessObservation? readiness = null;
        if (_mcc.HasTool("mcc_chunk_status"))
        {
            readiness = await ReadinessOnceAsync(position, cancellationToken)
                .ConfigureAwait(false);
            ValidateSessionBinding();
            if (readiness.Status != BlockReadinessStatus.Ready)
            {
                return new BlockReadbackObservation(
                    position,
                    null,
                    null,
                    readiness.FailureReason,
                    readiness);
            }
        }
        else
        {
            readiness = new BlockReadinessObservation(
                BlockReadinessStatus.Unknown,
                null,
                $"请求 {position} 的 MCC chunk readiness 工具未发现；客户端缓存新鲜度未知，将保持兼容地读取方块。 ");
            // A caller can explicitly request readiness even when the server
            // does not expose the optional tool; fail closed rather than claim
            // that the cache is observable.
            if (_requireReadyChunk)
            {
                return new BlockReadbackObservation(
                    position,
                    null,
                    null,
                    readiness.FailureReason,
                    readiness);
            }
        }

        var result = await _mcc.WorldBlockAtAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken).ConfigureAwait(false);
        ValidateSessionBinding();

        if (!result.TryGetBlockSample(out var actualBlock, out var returnedPosition))
        {
            return new BlockReadbackObservation(
                position,
                null,
                null,
                $"请求 {position} 方块验证无法解析：mcc_world_block_at 未返回可识别的文本方块 ID。",
                readiness);
        }

        if (returnedPosition is not { } actualPosition)
        {
            return new BlockReadbackObservation(
                position,
                actualBlock,
                null,
                $"请求 {position} 的方块采样未返回坐标。",
                readiness);
        }

        if (actualPosition != position)
        {
            return new BlockReadbackObservation(
                position,
                actualBlock,
                actualPosition,
                $"采样返回坐标 {actualPosition}，但请求坐标为 {position}。",
                readiness);
        }

        return new BlockReadbackObservation(
            position,
            actualBlock,
            actualPosition,
            null,
            readiness);
    }

    private async Task<BlockReadinessObservation> ReadinessOnceAsync(
        BlockPosition position,
        CancellationToken cancellationToken)
    {
        if (!_mcc.HasTool("mcc_chunk_status"))
        {
            return new BlockReadinessObservation(
                BlockReadinessStatus.Unknown,
                null,
                $"请求 {position} 的 MCC chunk readiness 工具未发现；客户端缓存新鲜度未知，将保持兼容地读取方块。 ");
        }

        var result = await _mcc.ChunkStatusAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetChunkStatus(position, out var sample))
        {
            return BlockReadinessObservation.Invalid(
                $"请求 {position} 的 mcc_chunk_status 返回无法解析或坐标/chunk 不匹配的 readiness。 ");
        }

        if (!sample.Loaded || !sample.FullyLoaded)
        {
            return BlockReadinessObservation.Unavailable(
                $"请求 {position} 的目标 chunk 尚未完全加载（loaded={sample.Loaded}, fullyLoaded={sample.FullyLoaded}）；MCC 客户端缓存不可作为当前写入证明。 ",
                sample);
        }

        return BlockReadinessObservation.Ready(sample);
    }

    public async Task VerifyOnceAsync(
        BlockPosition position,
        string expectedBlock,
        CancellationToken cancellationToken = default)
    {
        var validatedBlock = new CommandSafety().ValidateBlock(expectedBlock);
        var observation = await ReadOnceAsync(position, cancellationToken)
            .ConfigureAwait(false);
        if (!observation.IsValid)
        {
            throw new BackendException(
                $"独立方块验证失败：{observation.FailureReason ?? $"请求 {position} 的采样无效。"}",
                uncertain: true);
        }

        if (!observation.Matches(validatedBlock))
        {
            throw new BackendException(
                $"独立方块验证不匹配：{position}，期望 {validatedBlock}，实际 {observation.BlockId ?? "未解析"}。",
                uncertain: true);
        }
    }
}
