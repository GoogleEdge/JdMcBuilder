using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed class PlaceBlockBackend : IBuildBackend
{
    private readonly MccToolClient _mcc;
    private readonly Func<BlockPosition, string, CancellationToken, Task> _verify;
    private string? _selectedBlock;

    public PlaceBlockBackend(
        MccToolClient mcc,
        Func<BlockPosition, string, CancellationToken, Task> verify,
        BackendStatus status = BackendStatus.Unverified,
        BackendVerification? verification = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        Capabilities = new BackendCapabilities(
            "place-block",
            "逐块放置",
            status,
            false,
            true,
            "需要玩家库存中的材料、玩家状态确认和方块采样；速度慢，首版只用于小批量。",
            verification);
    }

    public BackendCapabilities Capabilities { get; }

    public async Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsVerified)
        {
            throw new BackendException("逐块后端尚未通过目标绑定能力验证，拒绝发送放置操作。" );
        }

        if (batch is not ExplicitBlocksBatch blocks)
        {
            throw new BackendException("逐块后端需要 ExplicitBlocksBatch。" );
        }

        var calls = new List<string>();
        var mutationDispatched = false;
        try
        {
            foreach (var group in blocks.Blocks.GroupBy(item => item.Block, StringComparer.Ordinal))
            {
                await _mcc.SelectItemAsync(
                    ToInventoryName(group.Key),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                calls.Add($"mcc_select_item({group.Key})");
                var stats = await _mcc.PlayerStatsAsync(cancellationToken).ConfigureAwait(false);
                if (!stats.TryGetItemId(out var heldItem)
                    || !ItemMatches(heldItem, group.Key))
                {
                    throw new BackendException(
                        $"无法确认逐块放置材料：期望 {group.Key}，实际 {heldItem ?? "未知"}。",
                        uncertain: false);
                }
                _selectedBlock = group.Key;

                foreach (var placement in group)
                {
                    mutationDispatched = true;
                    await _mcc.PlaceBlockAsync(
                        placement.Position.X,
                        placement.Position.Y,
                        placement.Position.Z,
                        lookAtBlock: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    calls.Add($"mcc_place_block({placement.Position})");
                    await _verify(placement.Position, placement.Block, cancellationToken).ConfigureAwait(false);
                }
            }

            return new BackendOperationResult(batch.BatchId, true, false, "逐块放置完成。", batch.BlockCount, calls);
        }
        catch (McpException exception)
        {
            throw BackendFailure.FromMcp("逐块放置", exception, mutationDispatched);
        }
        catch (OperationCanceledException exception) when (mutationDispatched)
        {
            throw BackendFailure.FromException("逐块放置", exception, mutationDispatched);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackendException exception) when (mutationDispatched && !exception.Uncertain)
        {
            throw BackendFailure.FromException("逐块放置", exception, mutationDispatched);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw BackendFailure.FromException("逐块放置", exception, mutationDispatched);
        }
    }

    private static bool ItemMatches(string actual, string expected)
    {
        static string Normalize(string value) => value.Trim().ToLowerInvariant() switch
        {
            var item when item.StartsWith("minecraft:", StringComparison.Ordinal) => item[10..],
            var item => item.Replace(' ', '_').Replace('-', '_')
        };

        return Normalize(actual) == Normalize(expected)
            || Normalize(actual) == Normalize(ToInventoryName(expected));
    }

    private static string ToInventoryName(string namespacedBlock)
    {
        var id = namespacedBlock.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)
            ? namespacedBlock[10..]
            : namespacedBlock;
        return string.Join('_', id.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
