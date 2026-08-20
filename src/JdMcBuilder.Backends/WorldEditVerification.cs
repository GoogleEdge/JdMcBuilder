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

    internal void Validate()
    {
        if (MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts),
                "WorldEdit 验证至少需要一次读取尝试。 ");
        }

        var maximumTaskDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        if (OverallTimeout <= TimeSpan.Zero || OverallTimeout > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverallTimeout),
                $"WorldEdit 验证总超时必须在 1 毫秒到 {maximumTaskDelay} 之间。 ");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay),
                $"WorldEdit 验证初始等待必须在零到 {maximumTaskDelay} 之间。 ");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDelay),
                $"WorldEdit 验证最大等待必须在初始等待到 {maximumTaskDelay} 之间。 ");
        }

        ArgumentNullException.ThrowIfNull(DelayAsync);
    }
}

/// <summary>
/// Verifies one already-dispatched WorldEdit selection using bounded, read-only polling.
/// This class never resends //pos or //set.
/// </summary>
public sealed class WorldEditVerifier
{
    private readonly BlockReadbackVerifier _readback;
    private readonly WorldEditVerificationOptions _options;
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
        _readback = readback ?? throw new ArgumentNullException(nameof(readback));
        _options = options ?? new WorldEditVerificationOptions();
        _options.Validate();
        _safety = safety ?? new CommandSafety();
    }

    public async Task VerifyAsync(
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
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                verificationToken.ThrowIfCancellationRequested();
                attempts = attempt;
                var observation = await _readback.ReadOnceAsync(
                    position,
                    verificationToken).ConfigureAwait(false);
                // A transport or fake MCP implementation may complete a read
                // after cancellation. A late observation is never proof.
                verificationToken.ThrowIfCancellationRequested();
                if (!observation.IsValid)
                {
                    throw Fail(
                        position,
                        validatedBlock,
                        attempts,
                        observation.FailureReason ?? "采样结果无效。 ",
                        lastObservedBlock);
                }

                lastObservedBlock = observation.BlockId;
                if (observation.Matches(validatedBlock))
                {
                    return;
                }

                if (attempt < _options.MaxAttempts)
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
                exception);
        }
        catch (McpException exception)
        {
            throw Fail(
                position,
                validatedBlock,
                attempts,
                $"mcc_world_block_at 调用失败：{exception.Message}",
                lastObservedBlock,
                exception);
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
                exception);
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
        Exception? inner = null)
    {
        var observed = lastObservedBlock ?? "未解析";
        return new BackendException(
            $"WorldEdit 独立方块验证失败：{reason}请求 {position}，期望 {expectedBlock}，最后实际 {observed}，尝试 {attempts} 次。",
            uncertain: true,
            inner);
    }
}
