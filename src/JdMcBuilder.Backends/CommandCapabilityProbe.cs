using System.Security.Cryptography;
using System.Text;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record BackendProbeRequest(
    BlockRange WorldEditRange,
    BlockRange NativeFillRange,
    BlockPosition SetBlockPosition,
    string TestBlock = "minecraft:stone",
    TimeSpan? VerificationValidity = null);

public sealed record BackendProbeResult(
    string BackendId,
    BackendStatus Status,
    string Reason,
    string TargetFingerprint,
    BackendVerification? Verification = null,
    BlockPosition? VerifiedPosition = null,
    bool WriteMayHaveBeenDispatched = false)
{
    public bool IsVerified => Status == BackendStatus.Available && Verification is not null;

    public bool IsVerifiedFor(string? targetFingerprint) =>
        Status == BackendStatus.Available
        && Verification is not null
        && !string.IsNullOrWhiteSpace(targetFingerprint)
        && Verification.IsValidFor(BackendId, targetFingerprint);
}

public sealed record BackendProbeReport(
    DateTimeOffset ProbedAt,
    string TargetFingerprint,
    IReadOnlyList<BackendProbeResult> Results)
{
    public BackendProbeResult? Find(string backendId) =>
        Results.FirstOrDefault(item =>
            string.Equals(item.BackendId, backendId, StringComparison.Ordinal));
}

public static class TargetFingerprintBuilder
{
    public static string Create(
        MccToolClient mcc,
        McpToolResult sessionStatus,
        McpToolResult worldState,
        McpToolResult? serverInfo = null)
    {
        ArgumentNullException.ThrowIfNull(mcc);
        ArgumentNullException.ThrowIfNull(sessionStatus);
        ArgumentNullException.ThrowIfNull(worldState);

        var identity = new List<string>
        {
            mcc.Endpoint?.GetLeftPart(UriPartial.Path) ?? "unknown-endpoint"
        };
        identity.AddRange(ExtractStableIdentity(sessionStatus, "session"));
        identity.AddRange(ExtractStableIdentity(worldState, "world"));
        if (serverInfo is not null)
        {
            identity.AddRange(ExtractStableIdentity(serverInfo, "server"));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", identity)));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static IEnumerable<string> ExtractStableIdentity(
        McpToolResult result,
        string prefix)
    {
        var names = prefix switch
        {
            "world" => new[]
            {
                "world",
                "worldName",
                "world_name",
                "worldId",
                "world_id",
                "dimension",
                "dimensionName",
                "dimension_name",
                "dimensionType",
                "dimension_type",
                "level",
                "levelName",
                "level_name"
            },
            "server" => new[]
            {
                "serverId",
                "server_id",
                "software",
                "version",
                "host",
                "port"
            },
            _ => new[]
            {
                "sessionId",
                "session_id",
                "world",
                "dimension",
                "host",
                "port"
            }
        };

        foreach (var name in names)
        {
            if (result.TryGetString(out var value, name))
            {
                yield return $"{prefix}:{name.ToLowerInvariant()}={value.Trim()}";
            }
        }

        var hasStableValue = false;
        foreach (var name in names)
        {
            if (result.TryGetString(out _, name))
            {
                hasStableValue = true;
                break;
            }
        }

        if (!hasStableValue)
        {
            var diagnostic = result.ToDiagnosticText().Trim();
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(diagnostic));
                yield return $"{prefix}:observation={Convert.ToHexString(digest).ToLowerInvariant()}";
            }
        }
    }
}

public sealed class CommandCapabilityProbe
{
    private const long MaxProbeVolume = 4096;
    private readonly MccToolClient _mcc;
    private readonly CommandSafety _safety;
    private readonly BlockReadbackVerifier _readback;
    private readonly WorldEditVerifier _worldEditVerifier;
    private readonly NativeFillVerifier _nativeFillVerifier;
    private readonly NativeSetBlockVerifier _nativeSetBlockVerifier;

    public CommandCapabilityProbe(
        MccToolClient mcc,
        CommandSafety? safety = null,
        NativeFillVerificationOptions? nativeFillVerificationOptions = null,
        NativeSetBlockVerificationOptions? nativeSetBlockVerificationOptions = null,
        WorldEditVerificationOptions? worldEditVerificationOptions = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _safety = safety ?? new CommandSafety();
        _readback = new BlockReadbackVerifier(_mcc);
        _worldEditVerifier = new WorldEditVerifier(
            _readback,
            worldEditVerificationOptions,
            _safety);
        _nativeFillVerifier = new NativeFillVerifier(
            _mcc,
            nativeFillVerificationOptions,
            _readback);
        _nativeSetBlockVerifier = new NativeSetBlockVerifier(
            _readback,
            nativeSetBlockVerificationOptions,
            _safety);
    }

    public async Task<BackendProbeReport> ProbeApprovedAsync(
        BackendProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        if (!HasAll("mcc_session_status", "mcc_world_state"))
        {
            var missing = new[] { "mcc_session_status", "mcc_world_state" }
                .Where(name => !_mcc.HasTool(name));
            throw new BackendException(
                $"能力探测缺少目标绑定预检工具：{string.Join(", ", missing)}。",
                uncertain: false);
        }

        var session = await _mcc.SessionStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        var world = await _mcc.WorldStateAsync(cancellationToken)
            .ConfigureAwait(false);
        McpToolResult? server = null;
        if (_mcc.HasTool("mcc_server_info"))
        {
            server = await _mcc.ServerInfoAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var probedAt = DateTimeOffset.UtcNow;
        var targetFingerprint = TargetFingerprintBuilder.Create(
            _mcc,
            session,
            world,
            server);
        var validity = request.VerificationValidity ?? TimeSpan.FromMinutes(10);
        if (validity <= TimeSpan.Zero || validity > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.VerificationValidity),
                "验证有效期必须大于零且不超过一天。 ");
        }

        var results = new List<BackendProbeResult>();
        var worldEdit = await ProbeWorldEditCoreAsync(
            request,
            targetFingerprint,
            probedAt,
            validity,
            cancellationToken).ConfigureAwait(false);
        results.Add(worldEdit);
        if (worldEdit.WriteMayHaveBeenDispatched)
        {
            return new BackendProbeReport(probedAt, targetFingerprint, results);
        }

        var nativeFill = await ProbeNativeFillCoreAsync(
            request,
            targetFingerprint,
            probedAt,
            validity,
            cancellationToken).ConfigureAwait(false);
        results.Add(nativeFill);
        if (nativeFill.WriteMayHaveBeenDispatched)
        {
            return new BackendProbeReport(probedAt, targetFingerprint, results);
        }

        results.Add(await ProbeSetBlockCoreAsync(
            request,
            targetFingerprint,
            probedAt,
            validity,
            cancellationToken).ConfigureAwait(false));

        return new BackendProbeReport(probedAt, targetFingerprint, results);
    }

    private async Task<BackendProbeResult> ProbeWorldEditCoreAsync(
        BackendProbeRequest request,
        string targetFingerprint,
        DateTimeOffset probedAt,
        TimeSpan validity,
        CancellationToken cancellationToken)
    {
        const string backendId = "worldedit";
        if (!HasAll("mcc_send_chat", "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少 WorldEdit 探测所需的写入或方块读取工具。");
        }

        var mutationDispatched = false;
        try
        {
            await _mcc.SendChatAsync(
                _safety.BuildWorldEditSelection(request.WorldEditRange),
                cancellationToken).ConfigureAwait(false);
            mutationDispatched = true;
            await _mcc.SendChatAsync(
                _safety.BuildWorldEditSet(request.TestBlock),
                cancellationToken).ConfigureAwait(false);
            await _worldEditVerifier.VerifyAsync(
                request.WorldEditRange.Min,
                request.TestBlock,
                cancellationToken).ConfigureAwait(false);

            return Available(
                backendId,
                targetFingerprint,
                probedAt,
                validity,
                request.WorldEditRange.Min,
                "WorldEdit 选区、set 命令返回和方块采样均通过。",
                true);
        }
        catch (OperationCanceledException exception)
        {
            if (mutationDispatched)
            {
                return Failed(
                    backendId,
                    targetFingerprint,
                    exception,
                    mutationDispatched,
                    "WorldEdit 探测取消");
            }

            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                backendId,
                targetFingerprint,
                exception,
                mutationDispatched,
                "WorldEdit 探测失败");
        }
    }

    private async Task<BackendProbeResult> ProbeNativeFillCoreAsync(
        BackendProbeRequest request,
        string targetFingerprint,
        DateTimeOffset probedAt,
        TimeSpan validity,
        CancellationToken cancellationToken)
    {
        const string backendId = "native-fill";
        if (!HasAll("mcc_send_chat", "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少 /fill 探测所需的写入或方块读取工具。");
        }

        var mutationDispatched = false;
        try
        {
            var plan = NativeFillVerificationPlan.Create(
                request.NativeFillRange,
                request.TestBlock,
                _safety);
            mutationDispatched = true;
            await _mcc.SendChatAsync(plan.Command, cancellationToken)
                .ConfigureAwait(false);
            var verification = await _nativeFillVerifier.VerifyAsync(
                plan,
                cancellationToken).ConfigureAwait(false);

            return Available(
                backendId,
                targetFingerprint,
                probedAt,
                validity,
                request.NativeFillRange.Min,
                $"/fill 命令已发送并通过独立方块采样。{Environment.NewLine}{verification.Diagnostic}",
                true);
        }
        catch (OperationCanceledException exception)
        {
            if (mutationDispatched)
            {
                return Failed(
                    backendId,
                    targetFingerprint,
                    exception,
                    mutationDispatched,
                    "/fill 探测取消");
            }

            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                backendId,
                targetFingerprint,
                exception,
                mutationDispatched,
                "/fill 探测失败");
        }
    }

    private async Task<BackendProbeResult> ProbeSetBlockCoreAsync(
        BackendProbeRequest request,
        string targetFingerprint,
        DateTimeOffset probedAt,
        TimeSpan validity,
        CancellationToken cancellationToken)
    {
        const string backendId = "native-setblock";
        if (!HasAll("mcc_send_chat", "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少 /setblock 探测所需的 mcc_send_chat 或独立方块读取工具。");
        }

        var mutationDispatched = false;
        try
        {
            var command = _safety.BuildNativeSetBlock(
                request.SetBlockPosition,
                request.TestBlock);
            mutationDispatched = true;
            var response = await _mcc.SendChatAsync(command, cancellationToken)
                .ConfigureAwait(false);
            var verification = await _nativeSetBlockVerifier.VerifyAsync(
                request.SetBlockPosition,
                request.TestBlock,
                cancellationToken).ConfigureAwait(false);

            return Available(
                backendId,
                targetFingerprint,
                probedAt,
                validity,
                request.SetBlockPosition,
                $"/setblock 命令已发送；MCP 返回仅作诊断（{response.ToDiagnosticText()}）；{verification.Diagnostic}",
                true);
        }
        catch (OperationCanceledException exception)
        {
            if (mutationDispatched)
            {
                return Failed(
                    backendId,
                    targetFingerprint,
                    exception,
                    mutationDispatched,
                    "/setblock 探测取消");
            }

            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                backendId,
                targetFingerprint,
                exception,
                mutationDispatched,
                "/setblock 探测失败");
        }
    }

    private bool HasAll(params string[] names) =>
        names.All(_mcc.HasTool);

    private static void ValidateRequest(BackendProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var safety = new CommandSafety();
        safety.ValidateBlock(request.TestBlock);
        ValidateProbeRange(request.WorldEditRange, nameof(request.WorldEditRange));
        ValidateProbeRange(request.NativeFillRange, nameof(request.NativeFillRange));
        if (Math.Abs((long)request.SetBlockPosition.X) > 30_000_000
            || Math.Abs((long)request.SetBlockPosition.Y) > 30_000_000
            || Math.Abs((long)request.SetBlockPosition.Z) > 30_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.SetBlockPosition),
                "/setblock 探针坐标超出安全坐标上限。 ");
        }
        safety.ValidateBlock(request.TestBlock);
        if (RangesOverlap(request.WorldEditRange, request.NativeFillRange)
            || request.WorldEditRange.Contains(request.SetBlockPosition)
            || request.NativeFillRange.Contains(request.SetBlockPosition))
        {
            throw new ArgumentException("三个探针区域/点必须互不重叠，以便独立验证。", nameof(request));
        }

        if (request.VerificationValidity is { } validity)
        {
            if (validity <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request.VerificationValidity),
                    "验证有效期必须大于零。 ");
            }

            if (validity > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request.VerificationValidity),
                    "验证有效期不能超过一天。 ");
            }
        }
    }

    private static bool RangesOverlap(BlockRange left, BlockRange right) =>
        left.IsValid
        && right.IsValid
        && left.Min.X <= right.Max.X
        && right.Min.X <= left.Max.X
        && left.Min.Y <= right.Max.Y
        && right.Min.Y <= left.Max.Y
        && left.Min.Z <= right.Max.Z
        && right.Min.Z <= left.Max.Z;

    private static void ValidateProbeRange(BlockRange range, string parameterName)
    {
        if (!range.IsValid
            || !range.TryGetVolume(out var volume)
            || volume <= 0
            || volume > MaxProbeVolume)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "能力探针范围必须有效且体积不超过 4096 个方块。 ");
        }
    }

    private static BackendProbeResult Available(
        string backendId,
        string targetFingerprint,
        DateTimeOffset probedAt,
        TimeSpan validity,
        BlockPosition position,
        string reason,
        bool verified)
    {
        var verification = verified
            ? BackendVerification.Create(backendId, targetFingerprint, probedAt, validity)
            : null;
        return new BackendProbeResult(
            backendId,
            verification is null ? BackendStatus.Unverified : BackendStatus.Available,
            reason,
            targetFingerprint,
            verification,
            position);
    }

    private static BackendProbeResult Unavailable(
        string backendId,
        string targetFingerprint,
        string reason) =>
        new(backendId, BackendStatus.Unavailable, reason, targetFingerprint);

    private static BackendProbeResult Failed(
        string backendId,
        string targetFingerprint,
        Exception exception,
        bool mutationDispatched,
        string operation)
    {
        var uncertain = mutationDispatched
            || exception is BackendException { Uncertain: true };
        var status = uncertain
            ? BackendStatus.Unverified
            : BackendStatus.Unavailable;
        var suffix = uncertain
            ? "写入结果不确定；请先检查探针坐标，未生成能力证明。"
            : "未生成能力证明。";
        return new BackendProbeResult(
            backendId,
            status,
            $"{operation}：{exception.Message} {suffix}",
            targetFingerprint,
            Verification: null,
            VerifiedPosition: null,
            WriteMayHaveBeenDispatched: uncertain);
    }

}
