using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed class NativeFillBackend : IBuildBackend
{
    private readonly MccToolClient _mcc;
    private readonly CommandSafety _safety;
    private readonly NativeFillVerifier _nativeFillVerifier;

    public NativeFillBackend(
        MccToolClient mcc,
        NativeFillVerificationOptions? nativeFillVerificationOptions = null,
        BackendStatus status = BackendStatus.Unverified,
        CommandSafety? safety = null,
        BackendVerification? verification = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _safety = safety ?? new CommandSafety();
        _nativeFillVerifier = new NativeFillVerifier(
            _mcc,
            nativeFillVerificationOptions);
        Capabilities = new BackendCapabilities(
            "native-fill",
            "Minecraft /fill",
            status,
            true,
            false,
            "需要独立验证玩家/服务器权限、mcc_send_chat 的 /fill 结果和方块采样。",
            verification);
    }

    public BackendCapabilities Capabilities { get; }

    public async Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsVerified)
        {
            throw new BackendException("原生 /fill 后端尚未通过目标绑定能力验证，拒绝发送写入命令。" );
        }

        if (batch is not FillBatch fill)
        {
            throw new BackendException("原生 /fill 后端只支持 FillBatch。" );
        }

        var plan = NativeFillVerificationPlan.Create(
            fill.Range,
            fill.Block,
            _safety);
        var mutationDispatched = false;
        try
        {
            mutationDispatched = true;
            await _mcc.SendChatAsync(plan.Command, cancellationToken).ConfigureAwait(false);
            var verification = await _nativeFillVerifier.VerifyAsync(
                plan,
                cancellationToken).ConfigureAwait(false);

            return new BackendOperationResult(
                batch.BatchId,
                true,
                false,
                $"Minecraft /fill 命令已发送并完成独立验证。{Environment.NewLine}{verification.Diagnostic}",
                fill.BlockCount,
                [$"mcc_send_chat({plan.Command})"]);
        }
        catch (McpException exception)
        {
            throw BackendFailure.FromMcp("/fill", exception, mutationDispatched);
        }
        catch (OperationCanceledException exception)
        {
            throw BackendFailure.FromException("/fill", exception, mutationDispatched);
        }
        catch (BackendException exception) when (mutationDispatched && !exception.Uncertain)
        {
            throw BackendFailure.FromException("/fill", exception, mutationDispatched);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw BackendFailure.FromException("/fill", exception, mutationDispatched);
        }
    }
}
