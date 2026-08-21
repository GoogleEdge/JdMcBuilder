using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record NativeSetBlockVerificationOptions
{
    public int MaxAttemptsPerPlacement { get; init; } = 6;
    public bool RequireReadyChunk { get; init; }
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    internal void Validate()
    {
        if (MaxAttemptsPerPlacement <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttemptsPerPlacement),
                "每个 /setblock placement 至少需要一次读取尝试。 ");
        }

        var maximumTaskDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        if (OverallTimeout <= TimeSpan.Zero || OverallTimeout > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverallTimeout),
                $"原生 /setblock 验证总超时必须在 1 毫秒到 {maximumTaskDelay} 之间。 ");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay),
                $"原生 /setblock 验证初始等待必须在零到 {maximumTaskDelay} 之间。 ");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDelay),
                $"原生 /setblock 验证最大等待必须在初始等待到 {maximumTaskDelay} 之间。 ");
        }

        ArgumentNullException.ThrowIfNull(DelayAsync);
    }
}

public sealed record NativeSetBlockVerificationResult(
    BlockPosition Position,
    string ExpectedBlock,
    int Attempts,
    string ActualBlock,
    BlockPosition? ReturnedPosition = null,
    BlockReadinessStatus? ReadinessStatus = null,
    string? FailureReason = null)
{
    public string Diagnostic
    {
        get
        {
            var readiness = ReadinessStatus switch
            {
                BlockReadinessStatus.Ready =>
                    "；目标 chunk 已加载，但方块值仍来自 MCC 客户端缓存，不是服务器权威 fresh read",
                BlockReadinessStatus.Unknown =>
                    "；未获得 chunk readiness，方块值来自 MCC 客户端缓存且新鲜度未知",
                BlockReadinessStatus.Unavailable =>
                    "；MCC 客户端缓存当前不可观测",
                BlockReadinessStatus.Invalid =>
                    "；chunk readiness 观测无效",
                _ => string.Empty
            };
            var returned = ReturnedPosition is { } position
                ? $"，返回坐标 {position}"
                : string.Empty;
            var failure = string.IsNullOrWhiteSpace(FailureReason)
                ? string.Empty
                : $"，原因 {FailureReason}";
            return $"请求 {Position}：通过，尝试 {Attempts} 次，实际 {ActualBlock}{returned}{readiness}{failure}。";
        }
    }
}

/// <summary>
/// Verifies one already-dispatched /setblock using read-only polling.
/// This class never sends a mutation command.
/// </summary>
public sealed class NativeSetBlockVerifier
{
    private readonly BlockReadbackVerifier _readback;
    private readonly NativeSetBlockVerificationOptions _options;
    private readonly CommandSafety _safety;

    public NativeSetBlockVerifier(
        MccToolClient mcc,
        NativeSetBlockVerificationOptions? options = null,
        CommandSafety? safety = null)
        : this(
            new BlockReadbackVerifier(
                mcc,
                options?.RequireReadyChunk == true
                    || mcc.HasTool("mcc_chunk_status")),
            options,
            safety)
    {
    }

    public NativeSetBlockVerifier(
        BlockReadbackVerifier readback,
        NativeSetBlockVerificationOptions? options = null,
        CommandSafety? safety = null)
    {
        _readback = readback ?? throw new ArgumentNullException(nameof(readback));
        _options = options ?? new NativeSetBlockVerificationOptions();
        _options.Validate();
        if (_options.RequireReadyChunk && !_readback.RequiresReadyChunk)
        {
            throw new ArgumentException(
                "需要 chunk readiness 时，BlockReadbackVerifier 必须启用 RequireReadyChunk。",
                nameof(readback));
        }

        _safety = safety ?? new CommandSafety();
    }

    public async Task<NativeSetBlockVerificationResult> VerifyAsync(
        BlockPosition position,
        string expectedBlock,
        CancellationToken cancellationToken = default)
    {
        var validatedBlock = _safety.ValidateBlock(expectedBlock);
        _options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OverallTimeout);
        var verificationToken = timeout.Token;
        var attempts = 0;
        string? lastObservedBlock = null;

        try
        {
            for (var attempt = 1; attempt <= _options.MaxAttemptsPerPlacement; attempt++)
            {
                verificationToken.ThrowIfCancellationRequested();
                attempts = attempt;
                var observation = await _readback.ReadOnceAsync(
                    position,
                    verificationToken).ConfigureAwait(false);
                // A transport or fake MCP implementation may complete a read
                // after its cancellation token has been signaled. Never accept
                // that late observation as proof of a successful mutation.
                verificationToken.ThrowIfCancellationRequested();
                lastObservedBlock = observation.BlockId;
                if (!observation.IsValid)
                {
                    if (observation.ReadinessUnavailable
                        && attempt < _options.MaxAttemptsPerPlacement)
                    {
                        await _options.DelayAsync(
                            GetDelay(attempt),
                            verificationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw Fail(
                        position,
                        validatedBlock,
                        attempts,
                        observation.FailureReason ?? "采样结果无效。 ",
                        lastObservedBlock,
                        observation.ReturnedPosition,
                        observation.Readiness?.Status);
                }

                if (observation.Matches(validatedBlock))
                {
                    return new NativeSetBlockVerificationResult(
                        position,
                        validatedBlock,
                        attempts,
                        observation.BlockId!,
                        observation.ReturnedPosition,
                        observation.Readiness?.Status,
                        observation.FailureReason);
                }

                if (attempt < _options.MaxAttemptsPerPlacement)
                {
                    await _options.DelayAsync(
                        GetDelay(attempt),
                        verificationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            var reason = cancellationToken.IsCancellationRequested
                ? "验证被取消"
                : "验证超过总超时";
            throw Fail(
                position,
                validatedBlock,
                attempts,
                reason,
                lastObservedBlock,
                inner: exception);
        }
        catch (McpException exception)
        {
            throw Fail(
                position,
                validatedBlock,
                attempts,
                $"MCP 只读调用失败（{exception.Kind}）：{exception.Message}",
                lastObservedBlock,
                inner: exception);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Fail(
                position,
                validatedBlock,
                attempts,
                $"采样读取失败：{exception.Message}",
                lastObservedBlock,
                inner: exception);
        }

        throw Fail(
            position,
            validatedBlock,
            attempts,
            "在有限的只读采样尝试后仍不匹配。 ",
            lastObservedBlock);
    }

    private TimeSpan GetDelay(int completedAttempt)
    {
        if (_options.InitialDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, completedAttempt - 1);
        var milliseconds = Math.Min(
            _options.MaximumDelay.TotalMilliseconds,
            _options.InitialDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static BackendException Fail(
        BlockPosition position,
        string expectedBlock,
        int attempts,
        string reason,
        string? lastObservedBlock = null,
        BlockPosition? returnedPosition = null,
        BlockReadinessStatus? readinessStatus = null,
        Exception? inner = null)
    {
        var observed = lastObservedBlock ?? "未解析";
        var returned = returnedPosition is { } actualPosition
            ? $"，返回坐标 {actualPosition}"
            : string.Empty;
        var readiness = readinessStatus switch
        {
            BlockReadinessStatus.Ready =>
                "；目标 chunk 已加载，但方块值仍来自 MCC 客户端缓存，不是服务器权威 fresh read",
            BlockReadinessStatus.Unknown =>
                "；未获得 chunk readiness，无法证明客户端缓存新鲜度",
            BlockReadinessStatus.Unavailable =>
                "；MCC 客户端缓存当前不可观测，未执行或未接受方块读取",
            BlockReadinessStatus.Invalid =>
                "；chunk readiness 观测无效，未接受方块读取",
            _ => string.Empty
        };
        var cacheCaveat = lastObservedBlock is null
            ? string.Empty
            : "（MCC mcc_world_block_at 是客户端缓存观察，不是服务器权威 fresh read）";
        return new BackendException(
            $"原生 /setblock 独立方块验证失败：{reason}请求 {position}，期望 {expectedBlock}，"
            + $"最后实际 {observed}{returned}{readiness}{cacheCaveat}，尝试 {attempts} 次。",
            uncertain: true,
            inner);
    }
}
