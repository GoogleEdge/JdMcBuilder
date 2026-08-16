using System.Security.Cryptography;
using System.Text;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed record BackendProbeRequest(
    BlockRange WorldEditRange,
    BlockRange NativeFillRange,
    BlockPosition PlaceBlockPosition,
    string TestBlock = "minecraft:stone",
    string? PlaceItemType = null,
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

    public CommandCapabilityProbe(MccToolClient mcc, CommandSafety? safety = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _safety = safety ?? new CommandSafety();
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

        results.Add(await ProbePlaceBlockCoreAsync(
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
        if (!HasAll("mcc_send_chat", "mcc_chat_history", "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少 WorldEdit 探测所需的写入、聊天观察或方块读取工具。");
        }

        var mutationDispatched = false;
        try
        {
            await _mcc.SendChatAsync(
                _safety.BuildWorldEditSelectionFirst(request.WorldEditRange.Min),
                cancellationToken).ConfigureAwait(false);
            await _mcc.SendChatAsync(
                _safety.BuildWorldEditSelectionSecond(request.WorldEditRange.Max),
                cancellationToken).ConfigureAwait(false);
            mutationDispatched = true;
            await _mcc.SendChatAsync(
                _safety.BuildWorldEditSet(request.TestBlock),
                cancellationToken).ConfigureAwait(false);
            await _mcc.ChatHistoryAsync(
                maxCount: 5,
                includeJson: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await VerifyBlockAsync(
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
        if (!HasAll("mcc_send_chat", "mcc_chat_history", "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少 /fill 探测所需的写入、聊天观察或方块读取工具。");
        }

        var mutationDispatched = false;
        try
        {
            var command = _safety.BuildNativeFill(
                request.NativeFillRange,
                request.TestBlock);
            mutationDispatched = true;
            await _mcc.SendChatAsync(command, cancellationToken)
                .ConfigureAwait(false);
            await _mcc.ChatHistoryAsync(
                maxCount: 5,
                includeJson: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await VerifyBlockAsync(
                request.NativeFillRange.Min,
                request.TestBlock,
                cancellationToken).ConfigureAwait(false);

            return Available(
                backendId,
                targetFingerprint,
                probedAt,
                validity,
                request.NativeFillRange.Min,
                "/fill 命令返回和方块采样均通过。",
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

    private async Task<BackendProbeResult> ProbePlaceBlockCoreAsync(
        BackendProbeRequest request,
        string targetFingerprint,
        DateTimeOffset probedAt,
        TimeSpan validity,
        CancellationToken cancellationToken)
    {
        const string backendId = "place-block";
        if (!HasAll(
                "mcc_place_block",
                "mcc_select_item",
                "mcc_player_stats",
                "mcc_world_block_at"))
        {
            return Unavailable(backendId, targetFingerprint, "缺少逐块探测所需的放置、选物品、玩家状态或方块读取工具。");
        }

        var itemType = string.IsNullOrWhiteSpace(request.PlaceItemType)
            ? ToInventoryName(request.TestBlock)
            : request.PlaceItemType.Trim();
        if (itemType.Any(char.IsControl)
            || itemType.Any(char.IsWhiteSpace))
        {
            return Unavailable(
                backendId,
                targetFingerprint,
                "逐块探针物品名称包含空白或控制字符。");
        }
        var mutationDispatched = false;
        try
        {
            await _mcc.SelectItemAsync(
                itemType,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var stats = await _mcc.PlayerStatsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!stats.TryGetItemId(out var heldItem)
                || !ItemMatches(heldItem, itemType, request.TestBlock))
            {
                throw new BackendException(
                    $"无法从 mcc_player_stats 确认手持探针物品：期望 {itemType}，实际 {heldItem ?? "未知"}。",
                    uncertain: false);
            }

            mutationDispatched = true;
            await _mcc.PlaceBlockAsync(
                request.PlaceBlockPosition.X,
                request.PlaceBlockPosition.Y,
                request.PlaceBlockPosition.Z,
                lookAtBlock: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await VerifyBlockAsync(
                request.PlaceBlockPosition,
                request.TestBlock,
                cancellationToken).ConfigureAwait(false);

            return Available(
                backendId,
                targetFingerprint,
                probedAt,
                validity,
                request.PlaceBlockPosition,
                "物品选择、手持状态、放置返回和方块采样均通过。",
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
                    "逐块放置探测取消");
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
                "逐块放置探测失败");
        }
    }

    private async Task VerifyBlockAsync(
        BlockPosition position,
        string expectedBlock,
        CancellationToken cancellationToken)
    {
        var result = await _mcc.WorldBlockAtAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetBlockId(out var actualBlock)
            || !string.Equals(actualBlock, expectedBlock, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendException(
                $"探针方块验证不匹配：{position}，期望 {expectedBlock}，实际 {actualBlock ?? "未知"}。",
                uncertain: true);
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
        if (Math.Abs((long)request.PlaceBlockPosition.X) > 30_000_000
            || Math.Abs((long)request.PlaceBlockPosition.Y) > 30_000_000
            || Math.Abs((long)request.PlaceBlockPosition.Z) > 30_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.PlaceBlockPosition),
                "逐块探针坐标超出安全坐标上限。 ");
        }
        safety.ValidateBlock(request.TestBlock);
        if (RangesOverlap(request.WorldEditRange, request.NativeFillRange)
            || request.WorldEditRange.Contains(request.PlaceBlockPosition)
            || request.NativeFillRange.Contains(request.PlaceBlockPosition))
        {
            throw new ArgumentException("三个探针区域/点必须互不重叠，以便独立验证。", nameof(request));
        }

        if (request.VerificationValidity is { } validity
            && validity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.VerificationValidity),
                "验证有效期必须大于零。 ");
        }

        if (request.VerificationValidity is { } validity
            && validity > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.VerificationValidity),
                "验证有效期不能超过一天。 ");
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

    private static bool ItemMatches(
        string actual,
        string requested,
        string expectedBlock)
    {
        static string Normalize(string value) =>
            value.Trim().ToLowerInvariant() switch
            {
                var item when item.StartsWith("minecraft:", StringComparison.Ordinal)
                    => item[10..],
                var item => item.Replace(' ', '_').Replace('-', '_')
            };

        var actualNormalized = Normalize(actual);
        return actualNormalized == Normalize(requested)
            || actualNormalized == Normalize(expectedBlock);
    }

    private static string ToInventoryName(string namespacedBlock)
    {
        var id = namespacedBlock.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)
            ? namespacedBlock[10..]
            : namespacedBlock;
        return string.Join(
            '_',
            id.Split('_').Select(part =>
                part.Length == 0
                    ? part
                    : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
