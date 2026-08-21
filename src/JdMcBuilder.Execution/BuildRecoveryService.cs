using System.Text.Json;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Execution;

/// <summary>
/// Resolves one uncertain FillBatch using fresh, coordinate-bound corner reads.
/// It never invokes a backend, capability probe, chat tool, or mutation tool.
/// </summary>
public sealed class BuildRecoveryService
{
    private readonly BuildJournal _journal;
    private readonly MccToolClient _mcc;
    private readonly BlockRangeVerifier _verifier;

    public BuildRecoveryService(
        BuildJournal journal,
        MccToolClient mcc,
        BlockRangeVerificationOptions? verificationOptions = null,
        BlockReadbackVerifier? readback = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _verifier = readback is null
            ? new BlockRangeVerifier(mcc, verificationOptions)
            : new BlockRangeVerifier(readback, verificationOptions);
    }

    public async Task<BuildRecoveryResult> ResolveAsync(
        BuildRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsConnectionCurrent())
            {
                return Reject(BuildRecoveryStatus.ConnectionChanged, request.BatchId, "MCP 连接已替换，未修改 journal。 ");
            }

            var beforeHash = await request.ReadBlueprintHashAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(beforeHash, request.ImportedBlueprintHash, StringComparison.Ordinal)
                || !string.Equals(request.Snapshot.State.BlueprintHash, beforeHash, StringComparison.Ordinal))
            {
                return Reject(BuildRecoveryStatus.BlueprintChanged, request.BatchId, "reviewed blueprint hash 已变化，未采样且未修改 journal。 ");
            }

            if (!ValidateSnapshotAndPlan(request, out var fill, out var rejection))
            {
                return Reject(BuildRecoveryStatus.Rejected, request.BatchId, rejection);
            }

            if (!HasStrictWorldBlockAtSchema(_mcc, out var schemaError))
            {
                return Reject(
                    BuildRecoveryStatus.Rejected,
                    request.BatchId,
                    $"mcc_world_block_at schema 不兼容：{schemaError}未修改 journal。 ");
            }

            BlockRangeVerificationPlan plan;
            try
            {
                plan = BlockRangeVerificationPlan.Create(fill.Range, fill.Block);
            }
            catch (Exception exception)
            {
                return Reject(
                    BuildRecoveryStatus.Rejected,
                    request.BatchId,
                    $"当前 reviewed FillBatch 的范围或方块无效：{exception.Message}未执行世界读取且未修改 journal。 ");
            }

            if (!request.IsConnectionCurrent())
            {
                return Reject(
                    BuildRecoveryStatus.ConnectionChanged,
                    request.BatchId,
                    "目标身份读取前 MCP 连接已替换，未执行方块读取且未修改 journal。 ");
            }

            var initialFingerprint = await request.ReadTargetFingerprintAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsConnectionCurrent())
            {
                return Reject(
                    BuildRecoveryStatus.ConnectionChanged,
                    request.BatchId,
                    "采样前 MCP 连接已替换，未执行方块读取且未修改 journal。 ");
            }

            if (!string.Equals(
                    initialFingerprint,
                    request.Snapshot.State.TargetFingerprint,
                    StringComparison.Ordinal))
            {
                return new BuildRecoveryResult(
                    BuildRecoveryStatus.TargetChanged,
                    request.BatchId,
                    "采样前目标指纹与 journal 不一致，未修改 journal。 ",
                    InitialTargetFingerprint: initialFingerprint);
            }

            var startedAt = DateTimeOffset.UtcNow;
            BlockRangeVerificationResult verification;
            try
            {
                verification = await _verifier.VerifyAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BlockRangeVerificationException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                return new BuildRecoveryResult(
                    BuildRecoveryStatus.Cancelled,
                    request.BatchId,
                    "不确定批次核验已取消；journal 未改变。 ",
                    InitialTargetFingerprint: initialFingerprint,
                    Verification: exception.Result);
            }
            catch (BlockRangeVerificationException exception)
            {
                if (FindFailureKind(exception) == McpFailureKind.SessionExpired)
                {
                    return new BuildRecoveryResult(
                        BuildRecoveryStatus.ConnectionChanged,
                        request.BatchId,
                        $"MCP session/context 已失效；丢弃角点证据且 journal 未改变。{Environment.NewLine}{exception.Message}",
                        InitialTargetFingerprint: initialFingerprint,
                        Verification: exception.Result);
                }

                return new BuildRecoveryResult(
                    BuildRecoveryStatus.VerificationFailed,
                    request.BatchId,
                    $"只读角点抽样未证明批次完成；journal 未改变。{Environment.NewLine}{exception.Message}",
                    InitialTargetFingerprint: initialFingerprint,
                    Verification: exception.Result);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsConnectionCurrent())
            {
                return new BuildRecoveryResult(
                    BuildRecoveryStatus.ConnectionChanged,
                    request.BatchId,
                    "角点抽样后 MCP 连接已替换；丢弃证据且未修改 journal。 ",
                    InitialTargetFingerprint: initialFingerprint,
                    Verification: verification);
            }

            var afterHash = await request.ReadBlueprintHashAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(afterHash, beforeHash, StringComparison.Ordinal))
            {
                return new BuildRecoveryResult(
                    BuildRecoveryStatus.BlueprintChanged,
                    request.BatchId,
                    "角点抽样后 reviewed blueprint hash 已变化；丢弃证据且未修改 journal。 ",
                    InitialTargetFingerprint: initialFingerprint,
                    Verification: verification);
            }

            var finalFingerprint = await request.ReadTargetFingerprintAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var connectionCurrent = request.IsConnectionCurrent();
            if (!connectionCurrent
                || !string.Equals(finalFingerprint, initialFingerprint, StringComparison.Ordinal)
                || !string.Equals(
                    finalFingerprint,
                    request.Snapshot.State.TargetFingerprint,
                    StringComparison.Ordinal))
            {
                return new BuildRecoveryResult(
                    connectionCurrent
                        ? BuildRecoveryStatus.TargetChanged
                        : BuildRecoveryStatus.ConnectionChanged,
                    request.BatchId,
                    "角点抽样后连接或目标指纹已变化；丢弃证据且未修改 journal。 ",
                    InitialTargetFingerprint: initialFingerprint,
                    FinalTargetFingerprint: finalFingerprint,
                    Verification: verification);
            }

            var finalHash = await request.ReadBlueprintHashAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(finalHash, afterHash, StringComparison.Ordinal)
                || !request.IsConnectionCurrent())
            {
                return new BuildRecoveryResult(
                    string.Equals(finalHash, afterHash, StringComparison.Ordinal)
                        ? BuildRecoveryStatus.ConnectionChanged
                        : BuildRecoveryStatus.BlueprintChanged,
                    request.BatchId,
                    "提交前 reviewed blueprint 或连接已变化；丢弃证据且未修改 journal。 ",
                    InitialTargetFingerprint: initialFingerprint,
                    FinalTargetFingerprint: finalFingerprint,
                    Verification: verification);
            }

            var completedAt = DateTimeOffset.UtcNow;
            var confirmation = FreshBatchConfirmation.Create(
                request.BatchId,
                request.Snapshot.State,
                startedAt,
                completedAt,
                verification);
            var resolution = await _journal.ResolveUncertainBatchAsync(
                request.Snapshot,
                finalHash,
                confirmation,
                cancellationToken).ConfigureAwait(false);
            if (!resolution.Resolved)
            {
                return new BuildRecoveryResult(
                    BuildRecoveryStatus.Conflict,
                    request.BatchId,
                    $"journal CAS 拒绝确认：{resolution.Message}",
                    resolution.State,
                    initialFingerprint,
                    finalFingerprint,
                    verification);
            }

            return new BuildRecoveryResult(
                BuildRecoveryStatus.Resolved,
                request.BatchId,
                $"{resolution.Message}{Environment.NewLine}{verification.Diagnostic}",
                resolution.State,
                initialFingerprint,
                finalFingerprint,
                verification);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Reject(
                BuildRecoveryStatus.Cancelled,
                request.BatchId,
                "不确定批次核验已取消；journal 未改变。 ");
        }
        catch (McpException exception) when (exception.Kind == McpFailureKind.SessionExpired)
        {
            return Reject(
                BuildRecoveryStatus.ConnectionChanged,
                request.BatchId,
                $"MCP session/context 已失效；journal 未改变：{exception.Message}");
        }
        catch (BackendException exception)
        {
            if (FindFailureKind(exception) == McpFailureKind.SessionExpired)
            {
                return Reject(
                    BuildRecoveryStatus.ConnectionChanged,
                    request.BatchId,
                    $"MCP session/context 已失效；journal 未改变：{exception.Message}");
            }

            return Reject(
                BuildRecoveryStatus.VerificationFailed,
                request.BatchId,
                $"只读核验失败：{exception.Message} journal 未改变。 ");
        }
        catch (OperationCanceledException exception)
        {
            return Reject(
                cancellationToken.IsCancellationRequested
                    ? BuildRecoveryStatus.Cancelled
                    : BuildRecoveryStatus.VerificationFailed,
                request.BatchId,
                cancellationToken.IsCancellationRequested
                    ? "不确定批次核验已取消；journal 未改变。 "
                    : $"只读核验因异步操作取消而失败：{exception.Message} journal 未改变。 ");
        }
        catch (Exception exception)
        {
            return Reject(
                BuildRecoveryStatus.VerificationFailed,
                request.BatchId,
                $"只读核验读取失败：{exception.Message} journal 未改变。 ");
        }
    }

    private static void ValidateRequest(BuildRecoveryRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ImportedBlueprintHash);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        ArgumentNullException.ThrowIfNull(request.PlannedBatches);
        ArgumentNullException.ThrowIfNull(request.ReadBlueprintHashAsync);
        ArgumentNullException.ThrowIfNull(request.ReadTargetFingerprintAsync);
        ArgumentNullException.ThrowIfNull(request.IsConnectionCurrent);
    }

    private static bool ValidateSnapshotAndPlan(
        BuildRecoveryRequest request,
        out FillBatch fill,
        out string rejection)
    {
        fill = null!;
        var state = request.Snapshot.State;
        if (string.IsNullOrWhiteSpace(request.Snapshot.Revision)
            || string.IsNullOrWhiteSpace(state.SessionId)
            || string.IsNullOrWhiteSpace(state.BlueprintHash)
            || string.IsNullOrWhiteSpace(state.BackendId)
            || string.IsNullOrWhiteSpace(state.TargetFingerprint)
            || state.BackendId is not ("worldedit" or "native-fill"))
        {
            rejection = "journal 缺少 revision/session/hash/backend/target identity，或 uncertain batch 不是由支持范围核验的 WorldEdit/native-fill 后端记录；未修改 journal。 ";
            return false;
        }

        var planIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in request.PlannedBatches)
        {
            if (batch is null
                || string.IsNullOrWhiteSpace(batch.BatchId)
                || string.IsNullOrWhiteSpace(batch.PhaseId)
                || string.IsNullOrWhiteSpace(batch.OperationId)
                || !planIds.Add(batch.BatchId))
            {
                rejection = "当前 reviewed plan 包含 null、空 metadata 或重复 batch ID；未修改 journal。 ";
                return false;
            }
        }

        var completed = state.CompletedBatches ?? Array.Empty<string>();
        var uncertain = state.UncertainBatches ?? Array.Empty<string>();
        if (completed.Any(string.IsNullOrWhiteSpace)
            || uncertain.Any(string.IsNullOrWhiteSpace)
            || completed.Distinct(StringComparer.Ordinal).Count() != completed.Count
            || uncertain.Distinct(StringComparer.Ordinal).Count() != uncertain.Count
            || completed.Intersect(uncertain, StringComparer.Ordinal).Any()
            || completed.Concat(uncertain).Any(batchId => !planIds.Contains(batchId)))
        {
            rejection = "journal 批次状态包含空值、重复、矛盾或当前 plan 不存在的 ID；未修改 journal。 ";
            return false;
        }

        var matches = request.PlannedBatches
            .Where(batch => string.Equals(batch.BatchId, request.BatchId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || matches[0] is not FillBatch plannedFill)
        {
            rejection = "目标 batch 在当前 reviewed plan 中不唯一或不是 FillBatch；未修改 journal。 ";
            return false;
        }

        if (!uncertain.Contains(request.BatchId, StringComparer.Ordinal)
            || completed.Contains(request.BatchId, StringComparer.Ordinal))
        {
            rejection = "目标 batch 当前不是唯一 uncertain/not-completed 状态；未修改 journal。 ";
            return false;
        }

        fill = plannedFill;
        rejection = string.Empty;
        return true;
    }

    private static bool HasStrictWorldBlockAtSchema(
        MccToolClient mcc,
        out string error)
    {
        if (!mcc.Tools.TryGetValue("mcc_world_block_at", out var tool))
        {
            error = "未发现只读工具。 ";
            return false;
        }

        var schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var schemaType)
            || schemaType.ValueKind != JsonValueKind.String
            || !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal)
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            error = "inputSchema 必须是带 properties 的 object。 ";
            return false;
        }

        var coordinateNames = new[] { "x", "y", "z" };
        var propertyNames = properties.EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        if (propertyNames.Length != coordinateNames.Length
            || !coordinateNames.All(propertyNames.Contains))
        {
            error = "properties 必须精确且仅包含 x/y/z。 ";
            return false;
        }

        if (schema.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind != JsonValueKind.False)
        {
            error = "additionalProperties 必须省略或明确为 false。 ";
            return false;
        }

        foreach (var coordinate in coordinateNames)
        {
            if (!properties.TryGetProperty(coordinate, out var property)
                || property.ValueKind != JsonValueKind.Object
                || !property.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "integer", StringComparison.Ordinal))
            {
                error = $"{coordinate} 参数不是 integer。 ";
                return false;
            }
        }

        if (!schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            error = "inputSchema 缺少 required。 ";
            return false;
        }

        var requiredItems = required.EnumerateArray().ToArray();
        if (requiredItems.Any(item => item.ValueKind != JsonValueKind.String))
        {
            error = "required 包含非字符串项。 ";
            return false;
        }

        var requiredNames = requiredItems
            .Select(item => item.GetString()!)
            .ToArray();
        if (requiredNames.Length != coordinateNames.Length
            || requiredNames.Distinct(StringComparer.Ordinal).Count()
                != requiredNames.Length
            || !coordinateNames.All(requiredNames.Contains))
        {
            error = "required 必须精确且不重复地包含 x/y/z。 ";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static McpFailureKind? FindFailureKind(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is McpException mcp)
            {
                return mcp.Kind;
            }
        }

        return null;
    }

    private static BuildRecoveryResult Reject(
        BuildRecoveryStatus status,
        string batchId,
        string message) =>
        new(status, batchId, message);
}
