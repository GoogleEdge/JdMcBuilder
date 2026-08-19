using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record NativeFillVerificationOptions
{
    public int MaxAttemptsPerSample { get; init; } = 6;
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
                "每个采样点至少需要一次读取尝试。 ");
        }

        var maximumTaskDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        if (OverallTimeout <= TimeSpan.Zero
            || OverallTimeout > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverallTimeout),
                $"原生 /fill 验证总超时必须在 1 毫秒到 {maximumTaskDelay} 之间。 ");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay),
                $"原生 /fill 验证初始等待必须在零到 {maximumTaskDelay} 之间。 ");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > maximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDelay),
                $"原生 /fill 验证最大等待必须在初始等待到 {maximumTaskDelay} 之间。 ");
        }

        ArgumentNullException.ThrowIfNull(DelayAsync);
    }
}

public sealed class NativeFillVerificationPlan
{
    private NativeFillVerificationPlan(
        BlockRange range,
        string expectedBlock,
        string command,
        IReadOnlyList<BlockPosition> samplePositions)
    {
        Range = range;
        ExpectedBlock = expectedBlock;
        Command = command;
        SamplePositions = samplePositions;
    }

    public BlockRange Range { get; }
    public string ExpectedBlock { get; }
    public string Command { get; }
    public IReadOnlyList<BlockPosition> SamplePositions { get; }

    public static NativeFillVerificationPlan Create(
        BlockRange range,
        string expectedBlock,
        CommandSafety? safety = null)
    {
        var commandSafety = safety ?? new CommandSafety();
        var validatedBlock = commandSafety.ValidateBlock(expectedBlock);
        var normalizedRange = BlockRange.FromUnordered(range.Min, range.Max);
        var command = commandSafety.BuildNativeFill(normalizedRange, validatedBlock);
        var samples = CreateCornerSamples(normalizedRange);
        return new NativeFillVerificationPlan(
            normalizedRange,
            validatedBlock,
            command,
            Array.AsReadOnly(samples.ToArray()));
    }

    public string Describe() =>
        $"范围 {Range}；命令 {Command}；采样点 " +
        $"[{string.Join(", ", SamplePositions.Select(item => item.ToString()))}]";

    internal static IReadOnlyList<BlockPosition> CreateCornerSamples(BlockRange range)
    {
        if (!range.IsValid)
        {
            throw new BackendException($"原生 /fill 验证范围无效：{range}。 ");
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

public sealed record NativeFillSampleObservation(
    BlockPosition RequestedPosition,
    int Attempts,
    string? LastObservedBlock,
    BlockPosition? LastReturnedPosition,
    bool Verified)
{
    public override string ToString()
    {
        var actual = LastObservedBlock ?? "未解析";
        var returned = LastReturnedPosition is { } position
            ? $"，返回坐标 {position}"
            : string.Empty;
        var status = Verified ? "通过" : "未通过";
        return $"请求 {RequestedPosition}：{status}，尝试 {Attempts} 次，实际 {actual}{returned}";
    }
}

public sealed record NativeFillVerificationResult(
    NativeFillVerificationPlan Plan,
    IReadOnlyList<NativeFillSampleObservation> Observations)
{
    public string Diagnostic =>
        $"{Plan.Describe()}；" +
        $"观测：{string.Join("；", Observations.Select(item => item.ToString()))}";
}

public sealed class NativeFillVerifier
{
    private readonly MccToolClient _mcc;
    private readonly NativeFillVerificationOptions _options;

    public NativeFillVerifier(
        MccToolClient mcc,
        NativeFillVerificationOptions? options = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _options = options ?? new NativeFillVerificationOptions();
        _options.Validate();
    }

    public async Task<NativeFillVerificationResult> VerifyAsync(
        NativeFillVerificationPlan plan,
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
                    var result = await _mcc.WorldBlockAtAsync(
                        state.RequestedPosition.X,
                        state.RequestedPosition.Y,
                        state.RequestedPosition.Z,
                        verificationToken).ConfigureAwait(false);
                    if (!result.TryGetBlockSample(
                            out var actualBlock,
                            out var returnedPosition))
                    {
                        throw Fail(
                            plan,
                            states.Values,
                            $"采样 {state.RequestedPosition} 未返回可识别的文本方块 ID。 ");
                    }

                    state.LastObservedBlock = actualBlock;
                    state.LastReturnedPosition = returnedPosition;
                    if (returnedPosition is { } actualPosition
                        && actualPosition != state.RequestedPosition)
                    {
                        throw Fail(
                            plan,
                            states.Values,
                            $"采样返回坐标 {actualPosition}，但请求坐标为 {state.RequestedPosition}。 ");
                    }

                    if (string.Equals(
                            actualBlock,
                            plan.ExpectedBlock,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        state.Verified = true;
                        pending.Remove(state);
                    }
                }

                if (pending.Count == 0)
                {
                    return CreateResult(plan, states.Values);
                }

                if (pending.Count > 0
                    && round < _options.MaxAttemptsPerSample)
                {
                    await _options.DelayAsync(
                        GetDelay(round),
                        verificationToken).ConfigureAwait(false);
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
                $"mcc_world_block_at 调用失败：{exception.Message}",
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
            "在有限的只读采样尝试后仍有采样点不匹配。 ");
    }

    private static void ValidatePlan(NativeFillVerificationPlan plan)
    {
        if (!plan.Range.IsValid
            || string.IsNullOrWhiteSpace(plan.ExpectedBlock)
            || string.IsNullOrWhiteSpace(plan.Command)
            || plan.SamplePositions is null
            || plan.SamplePositions.Count == 0
            || plan.SamplePositions.Any(position => !plan.Range.Contains(position))
            || plan.SamplePositions.Distinct().Count() != plan.SamplePositions.Count
            || !plan.SamplePositions.SequenceEqual(
                NativeFillVerificationPlan.CreateCornerSamples(plan.Range)))
        {
            throw new BackendException(
                "原生 /fill 验证计划无效：必须包含有效范围、命令、期望方块和完整的范围角点采样。 ",
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

    private static NativeFillVerificationResult CreateResult(
        NativeFillVerificationPlan plan,
        IEnumerable<SampleState> states) =>
        new(
            plan,
            states.Select(state => state.ToObservation()).ToArray());

    private static BackendException Fail(
        NativeFillVerificationPlan plan,
        IEnumerable<SampleState> states,
        string reason,
        Exception? inner = null)
    {
        var diagnostic = new NativeFillVerificationResult(
            plan,
            states.Select(state => state.ToObservation()).ToArray()).Diagnostic;
        return new BackendException(
            $"原生 /fill 独立方块验证失败：{reason}{Environment.NewLine}{diagnostic}",
            uncertain: true,
            inner);
    }

    private sealed class SampleState
    {
        public SampleState(BlockPosition requestedPosition) => RequestedPosition = requestedPosition;

        public BlockPosition RequestedPosition { get; }
        public int Attempts { get; set; }
        public string? LastObservedBlock { get; set; }
        public BlockPosition? LastReturnedPosition { get; set; }
        public bool Verified { get; set; }

        public NativeFillSampleObservation ToObservation() =>
            new(
                RequestedPosition,
                Attempts,
                LastObservedBlock,
                LastReturnedPosition,
                Verified);
    }
}
