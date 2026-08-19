using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Execution;

public sealed record BuildProgress(
    string BatchId,
    string PhaseId,
    long CompletedBlocks,
    long TotalBlocks,
    string Message,
    bool IsUncertain = false);

public sealed record BuildExecutionOptions(
    bool DryRun = true,
    bool AllowUnverifiedBackend = false,
    int MaxRetries = 2,
    TimeSpan? RetryDelay = null,
    string? TargetFingerprint = null);

public sealed class BuildExecutor
{
    private readonly IReadOnlyList<IBuildBackend> _backends;
    private readonly BackendSelector _selector;
    private readonly BuildJournal _journal;
    private readonly BuildExecutionOptions _options;
    private readonly object _pauseLock = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private TaskCompletionSource<bool> _resumeSignal = CompletedSignal();
    private int _paused;

    public BuildExecutor(
        IEnumerable<IBuildBackend> backends,
        BackendSelector selector,
        BuildJournal journal,
        BuildExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToArray();
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _options = options ?? new BuildExecutionOptions();
        if (_options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries cannot be negative.");
        }

        if (!_options.DryRun && _options.AllowUnverifiedBackend)
        {
            throw new ArgumentException(
                "真实施工不能启用未验证后端；必须使用目标绑定的能力验证证明。",
                nameof(options));
        }

        if (!_options.DryRun && string.IsNullOrWhiteSpace(_options.TargetFingerprint))
        {
            throw new ArgumentException(
                "真实施工必须提供当前目标的能力探测指纹。",
                nameof(options));
        }
    }

    public event EventHandler<BuildProgress>? Progress;

    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public void Pause()
    {
        lock (_pauseLock)
        {
            if (Volatile.Read(ref _paused) == 1)
            {
                return;
            }

            Volatile.Write(ref _paused, 1);
            _resumeSignal = NewSignal();
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool> signal;
        lock (_pauseLock)
        {
            if (Interlocked.Exchange(ref _paused, 0) == 0)
            {
                return;
            }

            signal = _resumeSignal;
        }

        signal.TrySetResult(true);
    }

    public async Task<BuildJournalState> ExecuteAsync(
        string blueprintHash,
        IReadOnlyList<BuildBatch> batches,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintHash);
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Any(batch => batch is null))
        {
            throw new ArgumentException("batches 不能包含 null。", nameof(batches));
        }

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var journalGate = await _journal.AcquireExecutionAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteCoreAsync(blueprintHash, batches, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<BuildJournalState> ExecuteCoreAsync(
        string blueprintHash,
        IReadOnlyList<BuildBatch> batches,
        CancellationToken cancellationToken)
    {
        var total = batches.Aggregate(0L, (current, batch) => checked(current + batch.BlockCount));

        if (_options.DryRun)
        {
            var dryRun = BuildJournalState.Create(blueprintHash, "dry-run", _options.TargetFingerprint);
            await _journal.SaveAsync(dryRun, cancellationToken).ConfigureAwait(false);
            var dryRunCompleted = 0L;
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                dryRunCompleted = checked(dryRunCompleted + batch.BlockCount);
                Progress?.Invoke(this, new BuildProgress(batch.BatchId, batch.PhaseId, dryRunCompleted, total, "Dry Run：未发送写入调用。"));
            }

            return dryRun;
        }

        var state = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = BuildJournalState.Create(
                blueprintHash,
                "unselected",
                _options.TargetFingerprint);
        }
        else if (!string.Equals(state.BlueprintHash, blueprintHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("journal 对应的蓝图 hash 与当前蓝图不一致。" );
        }
        else if (!string.Equals(
                     state.TargetFingerprint,
                     _options.TargetFingerprint,
                     StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "journal 对应的目标指纹与当前能力探测不一致，拒绝恢复以避免跨目标重复写入。 ");
        }

        var batchById = batches.ToDictionary(item => item.BatchId, StringComparer.Ordinal);
        var completedIds = state.CompletedBatches.ToHashSet(StringComparer.Ordinal);
        var uncertainIds = state.UncertainBatches.ToHashSet(StringComparer.Ordinal);
        if (completedIds.Count != state.CompletedBatches.Count
            || uncertainIds.Count != state.UncertainBatches.Count)
        {
            throw new InvalidOperationException("journal 的完成或不确定批次列表包含重复 ID，拒绝自动恢复。 ");
        }

        var unknownCompleted = completedIds.Where(batchId => !batchById.ContainsKey(batchId)).ToArray();
        var unknownUncertain = uncertainIds.Where(batchId => !batchById.ContainsKey(batchId)).ToArray();
        if (unknownCompleted.Length > 0 || unknownUncertain.Length > 0)
        {
            throw new InvalidOperationException("journal 包含当前蓝图不存在的批次，拒绝继续以避免误判恢复状态。" );
        }

        var contradictory = completedIds.Intersect(uncertainIds, StringComparer.Ordinal).ToArray();
        if (contradictory.Length > 0)
        {
            throw new InvalidOperationException(
                $"journal 同时将批次标记为已完成和不确定：{string.Join(", ", contradictory)}；拒绝自动恢复。 ");
        }

        var completed = completedIds.Aggregate(
            0L,
            (current, batchId) => checked(current + batchById[batchId].BlockCount));
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uncertainIds.Contains(batch.BatchId))
            {
                throw new InvalidOperationException($"批次 {batch.BatchId} 处于不确定状态，必须先人工/采样确认。" );
            }

            if (completedIds.Contains(batch.BatchId))
            {
                continue;
            }

            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            if (uncertainIds.Contains(batch.BatchId))
            {
                throw new InvalidOperationException($"批次 {batch.BatchId} 处于不确定状态，必须先人工/采样确认。" );
            }

            var selected = _selector.Select(
                _backends,
                batch,
                _options.AllowUnverifiedBackend,
                _options.TargetFingerprint);
            if (selected is null)
            {
                throw new InvalidOperationException($"没有可执行批次 {batch.BatchId} 的后端；请先完成能力验证，或明确启用未验证后端。" );
            }

            if (state.BackendId is not ("unselected" or "")
                && !string.Equals(state.BackendId, selected.Capabilities.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"journal 使用后端 {state.BackendId}，当前选择为 {selected.Capabilities.Id}；为避免重复写入，请先确认后端一致。" );
            }

            var inFlightState = state with
            {
                BackendId = selected.Capabilities.Id,
                TargetFingerprint = _options.TargetFingerprint,
                UncertainBatches = AddUnique(state.UncertainBatches, batch.BatchId),
                LastError = $"批次 {batch.BatchId} 已准备发送，等待结果确认。"
            };
            await SaveMutationStateAsync(inFlightState, inFlightState.LastError!).ConfigureAwait(false);
            state = inFlightState;
            uncertainIds.Add(batch.BatchId);

            BackendOperationResult result;
            try
            {
                result = await ExecuteWithRetryAsync(selected, batch, cancellationToken).ConfigureAwait(false);
            }
            catch (BackendException exception) when (exception.Uncertain)
            {
                state = state with
                {
                    BackendId = selected.Capabilities.Id,
                    TargetFingerprint = _options.TargetFingerprint,
                    UncertainBatches = AddUnique(state.UncertainBatches, batch.BatchId),
                    LastError = exception.Message
                };
                await SaveMutationStateAsync(state, exception.Message).ConfigureAwait(false);
                Progress?.Invoke(this, new BuildProgress(batch.BatchId, batch.PhaseId, completed, total, exception.Message, true));
                throw;
            }

            if (!result.Succeeded || result.Uncertain)
            {
                var message = string.IsNullOrWhiteSpace(result.Summary)
                    ? $"批次 {batch.BatchId} 未成功完成。"
                    : result.Summary;
                state = state with
                {
                    BackendId = selected.Capabilities.Id,
                    TargetFingerprint = _options.TargetFingerprint,
                    UncertainBatches = AddUnique(state.UncertainBatches, batch.BatchId),
                    LastError = message
                };
                await SaveMutationStateAsync(state, message).ConfigureAwait(false);
                Progress?.Invoke(this, new BuildProgress(batch.BatchId, batch.PhaseId, completed, total, message, true));
                // The journal was already marked in-flight before execution. Any
                // unsuccessful result therefore remains uncertain, even if a
                // backend accidentally omitted its uncertainty flag.
                throw new BackendException(message, uncertain: true);
            }

            if (!string.Equals(result.BatchId, batch.BatchId, StringComparison.Ordinal)
                || result.BlocksChanged != batch.BlockCount)
            {
                var message = $"批次返回值非法：BatchId={result.BatchId}、BlocksChanged={result.BlocksChanged}；期望 BatchId={batch.BatchId}、BlocksChanged 范围 0..{batch.BlockCount}。";
                state = state with
                {
                    BackendId = selected.Capabilities.Id,
                    TargetFingerprint = _options.TargetFingerprint,
                    UncertainBatches = AddUnique(state.UncertainBatches, batch.BatchId),
                    LastError = message
                };
                await SaveMutationStateAsync(state, message).ConfigureAwait(false);
                Progress?.Invoke(this, new BuildProgress(batch.BatchId, batch.PhaseId, completed, total, message, true));
                throw new BackendException(message, uncertain: true);
            }

            completed = checked(completed + result.BlocksChanged);
            state = state with
            {
                BackendId = selected.Capabilities.Id,
                TargetFingerprint = _options.TargetFingerprint,
                CompletedBatches = AddUnique(state.CompletedBatches, batch.BatchId),
                UncertainBatches = state.UncertainBatches
                    .Where(batchId => !string.Equals(batchId, batch.BatchId, StringComparison.Ordinal))
                    .ToArray(),
                LastError = null
            };
            try
            {
                await _journal.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception journalException)
            {
                var uncertainState = state with
                {
                    CompletedBatches = state.CompletedBatches
                        .Where(batchId => !string.Equals(batchId, batch.BatchId, StringComparison.Ordinal))
                        .ToArray(),
                    UncertainBatches = AddUnique(state.UncertainBatches, batch.BatchId),
                    LastError = $"批次已发送但完成 checkpoint 失败：{journalException.Message}"
                };
                try
                {
                    await _journal.SaveAsync(uncertainState, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception fallbackException)
                {
                    throw new BackendException(
                        $"批次 {batch.BatchId} 已执行，且完成/不确定 journal 均无法保存：{fallbackException.Message}",
                        uncertain: true,
                        fallbackException);
                }

                throw new BackendException(
                    $"批次 {batch.BatchId} 已执行但无法保存完成状态；已记录为不确定：{journalException.Message}",
                    uncertain: true,
                    journalException);
            }

            Progress?.Invoke(this, new BuildProgress(batch.BatchId, batch.PhaseId, completed, total, result.Summary));
        }

        return state;
    }

    private async Task<BackendOperationResult> ExecuteWithRetryAsync(IBuildBackend backend, BuildBatch batch, CancellationToken cancellationToken)
    {
        var delay = _options.RetryDelay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await backend.ExecuteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (BackendException exception) when (!exception.Uncertain && attempt < _options.MaxRetries)
            {
                // Only backend exceptions explicitly marked certain may be retried.
                // A mutating timeout/transport/result-verification failure must stop and await inspection.
                var multiplier = Math.Pow(2, attempt);
                var milliseconds = Math.Min(TimeSpan.FromMinutes(1).TotalMilliseconds, delay.TotalMilliseconds * multiplier);
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SaveMutationStateAsync(BuildJournalState state, string cause)
    {
        try
        {
            await _journal.SaveAsync(state).ConfigureAwait(false);
        }
        catch (Exception journalException)
        {
            throw new BackendException(
                $"施工结果不确定，且无法保存 journal：{cause}；journal 错误：{journalException.Message}",
                uncertain: true,
                journalException);
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _paused) == 1)
        {
            Task waitTask;
            lock (_pauseLock)
            {
                waitTask = _resumeSignal.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.TrySetResult(true);
        return signal;
    }

    private static IReadOnlyList<string> AddUnique(IReadOnlyList<string> values, string value) =>
        values.Contains(value, StringComparer.Ordinal)
            ? values
            : values.Append(value).ToArray();
}
