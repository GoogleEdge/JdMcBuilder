using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed class WorldEditCommandBackend : IBuildBackend
{
    private readonly MccToolClient _mcc;
    private readonly CommandSafety _safety;
    private readonly Func<BlockRange, string, CancellationToken, Task<BlockRangeVerificationResult>> _verifyRange;

    public WorldEditCommandBackend(
        MccToolClient mcc,
        Func<BlockRange, string, CancellationToken, Task<BlockRangeVerificationResult>> verifyRange,
        BackendStatus status = BackendStatus.Unverified,
        CommandSafety? safety = null,
        BackendVerification? verification = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _safety = safety ?? new CommandSafety();
        _verifyRange = verifyRange ?? throw new ArgumentNullException(nameof(verifyRange));
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

        // Validate every deterministic local input before the first mutation.
        var verificationPlan = BlockRangeVerificationPlan.Create(
            fill.Range,
            fill.Block,
            _safety);
        var selectionCommand = _safety.BuildWorldEditSelection(verificationPlan.Range);
        var setCommand = _safety.BuildWorldEditSet(verificationPlan.ExpectedBlock);
        var calls = new List<string>();
        var mutationDispatched = false;
        try
        {
            await SendAsync(selectionCommand, calls, cancellationToken).ConfigureAwait(false);
            mutationDispatched = true;
            await SendAsync(setCommand, calls, cancellationToken).ConfigureAwait(false);
            var verification = await _verifyRange(
                verificationPlan.Range,
                verificationPlan.ExpectedBlock,
                cancellationToken).ConfigureAwait(false);

            return new BackendOperationResult(
                batch.BatchId,
                true,
                false,
                $"WorldEdit 区域填充命令已发送并完成角点抽样验证。{Environment.NewLine}{verification.Diagnostic}",
                fill.BlockCount,
                calls);
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
