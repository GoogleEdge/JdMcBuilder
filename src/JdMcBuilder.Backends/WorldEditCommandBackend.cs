using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed class WorldEditCommandBackend : IBuildBackend
{
    private readonly MccToolClient _mcc;
    private readonly CommandSafety _safety;
    private readonly Func<BlockPosition, string, CancellationToken, Task> _verifySample;

    public WorldEditCommandBackend(
        MccToolClient mcc,
        Func<BlockPosition, string, CancellationToken, Task> verifySample,
        BackendStatus status = BackendStatus.Unverified,
        CommandSafety? safety = null,
        BackendVerification? verification = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _safety = safety ?? new CommandSafety();
        _verifySample = verifySample ?? throw new ArgumentNullException(nameof(verifySample));
        Capabilities = new BackendCapabilities(
            "worldedit",
            "WorldEdit 命令",
            status,
            true,
            false,
            "需要独立验证 mcc_send_chat、WorldEdit 权限、命令返回结果和方块采样。",
            verification);
    }

    public BackendCapabilities Capabilities { get; }

    public async Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsVerified)
        {
            throw new BackendException("WorldEdit 后端尚未通过目标绑定能力验证，拒绝发送写入命令。" );
        }

        if (batch is not FillBatch fill)
        {
            throw new BackendException("WorldEdit 首版只支持 FillBatch；显式方块请使用 /setblock 后端或未来 schematic 后端。", uncertain: false);
        }

        var calls = new List<string>();
        var mutationDispatched = false;
        try
        {
            await SendAsync(_safety.BuildWorldEditSelection(fill.Range), calls, cancellationToken).ConfigureAwait(false);
            mutationDispatched = true;
            await SendAsync(_safety.BuildWorldEditSet(fill.Block), calls, cancellationToken).ConfigureAwait(false);
            await _verifySample(fill.Range.Min, fill.Block, cancellationToken).ConfigureAwait(false);

            return new BackendOperationResult(batch.BatchId, true, false, "WorldEdit 区域填充命令已发送并完成验证。", fill.BlockCount, calls);
        }
        catch (McpException exception)
        {
            throw BackendFailure.FromMcp("WorldEdit", exception, mutationDispatched);
        }
        catch (OperationCanceledException exception) when (mutationDispatched)
        {
            throw BackendFailure.FromException("WorldEdit", exception, mutationDispatched);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackendException exception) when (mutationDispatched && !exception.Uncertain)
        {
            throw BackendFailure.FromException("WorldEdit", exception, mutationDispatched);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw BackendFailure.FromException("WorldEdit", exception, mutationDispatched);
        }
    }

    private async Task SendAsync(string command, ICollection<string> calls, CancellationToken cancellationToken)
    {
        await _mcc.SendChatAsync(command, cancellationToken).ConfigureAwait(false);
        calls.Add($"mcc_send_chat({command})");
    }
}
