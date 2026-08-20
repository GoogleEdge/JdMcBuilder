using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record BlockReadbackObservation(
    BlockPosition RequestedPosition,
    string? BlockId,
    BlockPosition? ReturnedPosition,
    string? FailureReason)
{
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

    public BlockReadbackVerifier(MccToolClient mcc)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
    }

    public async Task<BlockReadbackObservation> ReadOnceAsync(
        BlockPosition position,
        CancellationToken cancellationToken = default)
    {
        var result = await _mcc.WorldBlockAtAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken).ConfigureAwait(false);

        if (!result.TryGetBlockSample(out var actualBlock, out var returnedPosition))
        {
            return new BlockReadbackObservation(
                position,
                null,
                null,
                $"请求 {position} 方块验证无法解析：mcc_world_block_at 未返回可识别的文本方块 ID。");
        }

        if (returnedPosition is not { } actualPosition)
        {
            return new BlockReadbackObservation(
                position,
                actualBlock,
                null,
                $"请求 {position} 的方块采样未返回坐标。");
        }

        if (actualPosition != position)
        {
            return new BlockReadbackObservation(
                position,
                actualBlock,
                actualPosition,
                $"采样返回坐标 {actualPosition}，但请求坐标为 {position}。");
        }

        return new BlockReadbackObservation(
            position,
            actualBlock,
            actualPosition,
            null);
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
