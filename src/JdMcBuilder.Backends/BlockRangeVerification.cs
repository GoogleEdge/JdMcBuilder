using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record BlockRangeVerificationOptions
{
    public int MaxAttemptsPerSample { get; init; } = 6;
    public bool RequireReadyChunk { get; init; }
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    internal void Validate()
    {
        if (MaxAttemptsPerSample <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttemptsPerSample),
                "每个范围角点至少需要一次读取尝试。 ");
        }

        var maximumTaskDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        if (OverallTimeout <= TimeSpan.Zero || OverallTimeout > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverallTimeout),
                $"范围角点验证总超时必须在 1 毫秒到 {maximumTaskDelay} 之间。 ");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay),
                $"范围角点验证初始等待必须在零到 {maximumTaskDelay} 之间。 ");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDelay),
                $"范围角点验证最大等待必须在初始等待到 {maximumTaskDelay} 之间。 ");
        }

        ArgumentNullException.ThrowIfNull(DelayAsync);
    }
}

public sealed class BlockRangeVerificationPlan
{
    private BlockRangeVerificationPlan(
        BlockRange range,
        string expectedBlock,
        IReadOnlyList<BlockPosition> samplePositions)
    {
        Range = range;
        ExpectedBlock = expectedBlock;
        SamplePositions = samplePositions;
    }

    public BlockRange Range { get; }
    public string ExpectedBlock { get; }
    public IReadOnlyList<BlockPosition> SamplePositions { get; }

    public static BlockRangeVerificationPlan Create(
        BlockRange range,
        string expectedBlock,
        CommandSafety? safety = null)
    {
        var commandSafety = safety ?? new CommandSafety();
        var normalizedRange = BlockRange.FromUnordered(range.Min, range.Max);
        if (!normalizedRange.IsValid
            || !normalizedRange.TryGetVolume(out var volume)
            || volume <= 0)
        {
            throw new BackendException(
                $"范围角点验证范围无效：{range}。 ",
                uncertain: false);
        }

        var validatedBlock = commandSafety.ValidateBlock(expectedBlock);
        return new BlockRangeVerificationPlan(
            normalizedRange,
            validatedBlock,
            Array.AsReadOnly(CreateCornerSamples(normalizedRange).ToArray()));
    }

    public string Describe() =>
        $"范围 {Range}；期望 {ExpectedBlock}；角点采样点 "
        + $"[{string.Join(", ", SamplePositions.Select(item => item.ToString()))}]"
        + "（角点抽样，不是完整区域扫描）";

    public static IReadOnlyList<BlockPosition> CreateCornerSamples(BlockRange range)
    {
        if (!range.IsValid)
        {
            throw new BackendException(
                $"范围角点验证范围无效：{range}。 ",
                uncertain: false);
        }

        var samples = new List<BlockPosition>(8);
        var seen = new HashSet<BlockPosition>();
        var xs = new[] { range.Min.X, range.Max.X };
        var ys = new[] { range.Min.Y, range.Max.Y };
        var zs = new[] { range.Min.Z, range.Max.Z };
        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                foreach (var z in zs)
                {
                    var position = new BlockPosition(x, y, z);
                    if (seen.Add(position))
                    {
                        samples.Add(position);
                    }
                }
            }
        }

        return samples;
    }
}

public sealed record BlockRangeSampleObservation(
    BlockPosition RequestedPosition,
    int Attempts,
    string? LastObservedBlock,
    BlockPosition? LastReturnedPosition,
    string? FailureReason,
    bool Verified,
    BlockReadinessStatus? ReadinessStatus = null)
{
    public override string ToString()
    {
        var actual = LastObservedBlock ?? "未解析";
        var returned = LastReturnedPosition is { } position
            ? $"，返回坐标 {position}"
            : string.Empty;
        var failure = string.IsNullOrWhiteSpace(FailureReason)
            ? string.Empty
            : $"，原因 {FailureReason}";
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
        var status = Verified ? "通过" : "未通过";
        return $"请求 {RequestedPosition}：{status}，尝试 {Attempts} 次，最后实际 {actual}{returned}{readiness}{failure}";
    }
}

public sealed record BlockRangeVerificationResult(
    BlockRangeVerificationPlan Plan,
    IReadOnlyList<BlockRangeSampleObservation> Observations)
{
    public bool Verified =>
        Observations.Count == Plan.SamplePositions.Count
        && Observations.All(item => item.Verified);

    public string Diagnostic =>
        $"{Plan.Describe()}；观测：{string.Join("；", Observations.Select(item => item.ToString()))}";
}

public sealed class BlockRangeVerificationException : BackendException
{
    public BlockRangeVerificationException(
        string message,
        BlockRangeVerificationResult result,
        Exception? inner = null)
        : base(message, uncertain: true, inner)
    {
        Result = result;
    }

    public BlockRangeVerificationResult Result { get; }
}

/// <summary>
/// Verifies an already-dispatched range using bounded, coordinate-bound block reads.
/// This class only calls mcc_world_block_at and never dispatches a mutation.
/// </summary>
public sealed class BlockRangeVerifier
{
    private readonly BlockReadbackVerifier _readback;
    private readonly BlockRangeVerificationOptions _options;

    public BlockRangeVerifier(
        MccToolClient mcc,
        BlockRangeVerificationOptions? options = null,
        BlockReadbackVerifier? readback = null)
    {
        ArgumentNullException.ThrowIfNull(mcc);
        _readback = readback ?? new BlockReadbackVerifier(
            mcc,
            options?.RequireReadyChunk == true || mcc.HasTool("mcc_chunk_status"));
        _options = options ?? new BlockRangeVerificationOptions();
        _options.Validate();
        ValidateReadinessConfiguration();
    }

    public BlockRangeVerifier(
        BlockReadbackVerifier readback,
        BlockRangeVerificationOptions? options = null)
    {
        _readback = readback ?? throw new ArgumentNullException(nameof(readback));
        _options = options ?? new BlockRangeVerificationOptions();
        _options.Validate();
        ValidateReadinessConfiguration();
    }

    private void ValidateReadinessConfiguration()
    {
        if (_options.RequireReadyChunk && !_readback.RequiresReadyChunk)
        {
            throw new ArgumentException(
                "需要 chunk readiness 时，BlockReadbackVerifier 必须启用 RequireReadyChunk。",
                nameof(_readback));
        }
    }

    public async Task<BlockRangeVerificationResult> VerifyAsync(
        BlockRangeVerificationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        _options.Validate();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OverallTimeout);
        var verificationToken = timeout.Token;
        var states = plan.SamplePositions.ToDictionary(
            position => position,
            position => new SampleState(position));
        var pending = states.Values.ToList();

        try
        {
            for (var round = 1; round <= _options.MaxAttemptsPerSample; round++)
            {
                foreach (var state in pending.ToArray())
                {
                    verificationToken.ThrowIfCancellationRequested();
                    state.Attempts++;
                    var observation = await _readback.ReadOnceAsync(
                        state.RequestedPosition,
                        verificationToken).ConfigureAwait(false);
                    // A transport implementation may return data after cancellation.
                    // A late observation is never accepted as proof.
                    verificationToken.ThrowIfCancellationRequested();

                    state.LastObservedBlock = observation.BlockId;
                    state.LastReturnedPosition = observation.ReturnedPosition;
                    state.FailureReason = observation.FailureReason;
                    state.ReadinessStatus = observation.Readiness?.Status;
                    if (observation.Readiness?.Status == BlockReadinessStatus.Unknown
                        && state.FailureReason is not null)
                    {
                        state.FailureReason += "客户端缓存新鲜度未知；该读回不是服务器权威证明。";
                    }
                    else if (observation.Readiness?.Status == BlockReadinessStatus.Ready
                        && state.FailureReason is not null)
                    {
                        state.FailureReason += "chunk 已加载，但该读回仍是 MCC 客户端缓存观察，不是服务器权威 fresh read。";
                    }
                    if (!observation.IsValid)
                    {
                        if (observation.ReadinessUnavailable
                            && round < _options.MaxAttemptsPerSample)
                        {
                            continue;
                        }

                        throw Fail(
                            plan,
                            states.Values,
                            observation.FailureReason
                                ?? $"采样 {state.RequestedPosition} 结果无效。 ");
                    }

                    if (observation.Matches(plan.ExpectedBlock))
                    {
                        state.Verified = true;
                        state.FailureReason = null;
                        pending.Remove(state);
                    }
                }

                if (pending.Count == 0)
                {
                    verificationToken.ThrowIfCancellationRequested();
                    return CreateResult(plan, states.Values);
                }

                if (round < _options.MaxAttemptsPerSample)
                {
                    await _options.DelayAsync(
                        GetDelay(round),
                        verificationToken).ConfigureAwait(false);
                    verificationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            var reason = cancellationToken.IsCancellationRequested
                ? "验证被取消"
                : "验证超过总超时";
            throw Fail(plan, states.Values, $"{reason}。", exception);
        }
        catch (McpException exception)
        {
            throw Fail(
                plan,
                states.Values,
                $"MCP 只读调用失败（{exception.Kind}）：{exception.Message}",
                exception);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Fail(
                plan,
                states.Values,
                $"采样读取失败：{exception.Message}",
                exception);
        }

        throw Fail(
            plan,
            states.Values,
            "在有限的只读采样尝试后仍有角点不匹配。 ");
    }

    private static void ValidatePlan(BlockRangeVerificationPlan plan)
    {
        if (!plan.Range.IsValid
            || string.IsNullOrWhiteSpace(plan.ExpectedBlock)
            || plan.SamplePositions is null
            || plan.SamplePositions.Count == 0
            || plan.SamplePositions.Any(position => !plan.Range.Contains(position))
            || plan.SamplePositions.Distinct().Count() != plan.SamplePositions.Count
            || !plan.SamplePositions.SequenceEqual(
                BlockRangeVerificationPlan.CreateCornerSamples(plan.Range)))
        {
            throw new BackendException(
                "范围角点验证计划无效：必须包含有效范围、期望方块和完整去重角点。 ",
                uncertain: false);
        }
    }

    private TimeSpan GetDelay(int completedRound)
    {
        if (_options.InitialDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, completedRound - 1);
        var milliseconds = Math.Min(
            _options.MaximumDelay.TotalMilliseconds,
            _options.InitialDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static BlockRangeVerificationResult CreateResult(
        BlockRangeVerificationPlan plan,
        IEnumerable<SampleState> states) =>
        new(
            plan,
            states.Select(state => state.ToObservation()).ToArray());

    private static BackendException Fail(
        BlockRangeVerificationPlan plan,
        IEnumerable<SampleState> states,
        string reason,
        Exception? inner = null)
    {
        var result = CreateResult(plan, states);
        return new BlockRangeVerificationException(
            $"范围角点独立验证失败：{reason}{Environment.NewLine}{result.Diagnostic}",
            result,
            inner);
    }

    private sealed class SampleState
    {
        public SampleState(BlockPosition requestedPosition) =>
            RequestedPosition = requestedPosition;

        public BlockPosition RequestedPosition { get; }
        public int Attempts { get; set; }
        public string? LastObservedBlock { get; set; }
        public BlockPosition? LastReturnedPosition { get; set; }
        public string? FailureReason { get; set; }
        public bool Verified { get; set; }
        public BlockReadinessStatus? ReadinessStatus { get; set; }

        public BlockRangeSampleObservation ToObservation() =>
            new(
                RequestedPosition,
                Attempts,
                LastObservedBlock,
                LastReturnedPosition,
                FailureReason,
                Verified,
                ReadinessStatus);
    }
}
