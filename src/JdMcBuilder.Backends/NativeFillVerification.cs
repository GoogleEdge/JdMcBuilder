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

    internal BlockRangeVerificationOptions ToRangeOptions()
    {
        var options = new BlockRangeVerificationOptions
        {
            MaxAttemptsPerSample = MaxAttemptsPerSample,
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

public sealed class NativeFillVerificationPlan
{
    private NativeFillVerificationPlan(
        BlockRangeVerificationPlan rangePlan,
        string command)
    {
        RangePlan = rangePlan;
        Command = command;
    }

    internal BlockRangeVerificationPlan RangePlan { get; }
    public BlockRange Range => RangePlan.Range;
    public string ExpectedBlock => RangePlan.ExpectedBlock;
    public string Command { get; }
    public IReadOnlyList<BlockPosition> SamplePositions => RangePlan.SamplePositions;

    public static NativeFillVerificationPlan Create(
        BlockRange range,
        string expectedBlock,
        CommandSafety? safety = null)
    {
        var commandSafety = safety ?? new CommandSafety();
        var rangePlan = BlockRangeVerificationPlan.Create(
            range,
            expectedBlock,
            commandSafety);
        var command = commandSafety.BuildNativeFill(
            rangePlan.Range,
            rangePlan.ExpectedBlock);
        return new NativeFillVerificationPlan(rangePlan, command);
    }

    public string Describe() =>
        $"{RangePlan.Describe()}；命令 {Command}";

    internal static IReadOnlyList<BlockPosition> CreateCornerSamples(BlockRange range) =>
        BlockRangeVerificationPlan.CreateCornerSamples(range);
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
        $"{Plan.Describe()}；观测：{string.Join("；", Observations.Select(item => item.ToString()))}";
}

public sealed class NativeFillVerifier
{
    private readonly BlockRangeVerifier _rangeVerifier;

    public NativeFillVerifier(
        MccToolClient mcc,
        NativeFillVerificationOptions? options = null,
        BlockReadbackVerifier? readback = null)
    {
        ArgumentNullException.ThrowIfNull(mcc);
        var configured = options ?? new NativeFillVerificationOptions();
        _rangeVerifier = readback is null
            ? new BlockRangeVerifier(mcc, configured.ToRangeOptions())
            : new BlockRangeVerifier(readback, configured.ToRangeOptions());
    }

    public async Task<NativeFillVerificationResult> VerifyAsync(
        NativeFillVerificationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.Command))
        {
            throw new BackendException(
                "原生 /fill 验证计划无效：缺少命令。 ",
                uncertain: false);
        }

        try
        {
            var result = await _rangeVerifier.VerifyAsync(
                plan.RangePlan,
                cancellationToken).ConfigureAwait(false);
            return new NativeFillVerificationResult(
                plan,
                result.Observations.Select(item => new NativeFillSampleObservation(
                    item.RequestedPosition,
                    item.Attempts,
                    item.LastObservedBlock,
                    item.LastReturnedPosition,
                    item.Verified)).ToArray());
        }
        catch (BackendException exception) when (exception.Uncertain)
        {
            throw new BackendException(
                $"原生 /fill 独立方块验证失败：{exception.Message}{Environment.NewLine}{plan.Describe()}",
                uncertain: true,
                exception);
        }
    }
}
