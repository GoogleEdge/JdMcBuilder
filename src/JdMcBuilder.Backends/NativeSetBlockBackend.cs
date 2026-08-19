using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public sealed class NativeSetBlockBackend : IBuildBackend
{
    private readonly MccToolClient _mcc;
    private readonly CommandSafety _safety;
    private readonly Func<BlockPosition, string, CancellationToken, Task> _verify;
    private readonly string _targetFingerprint;

    public NativeSetBlockBackend(
        MccToolClient mcc,
        Func<BlockPosition, string, CancellationToken, Task> verify,
        string targetFingerprint,
        BackendStatus status = BackendStatus.Unverified,
        CommandSafety? safety = null,
        BackendVerification? verification = null)
    {
        _mcc = mcc ?? throw new ArgumentNullException(nameof(mcc));
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFingerprint);
        _targetFingerprint = targetFingerprint;
        _safety = safety ?? new CommandSafety();
        if (verification is not null
            && !verification.IsValidFor("native-setblock", targetFingerprint))
        {
            throw new ArgumentException(
                "/setblock 能力证明与当前目标指纹不匹配或已过期。",
                nameof(verification));
        }

        Capabilities = new BackendCapabilities(
            "native-setblock",
            "/setblock",
            status,
            false,
            true,
            "通过 mcc_send_chat 发送逐点 /setblock，并使用 mcc_world_block_at 独立验证；不依赖库存或玩家视线。",
            verification);
    }

    public BackendCapabilities Capabilities { get; }

    public async Task<BackendOperationResult> ExecuteAsync(
        BuildBatch batch,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsVerifiedFor(_targetFingerprint))
        {
            throw new BackendException("/setblock 后端尚未通过当前目标绑定能力验证，拒绝发送写入命令。", uncertain: false);
        }

        if (batch is not ExplicitBlocksBatch blocks)
        {
            throw new BackendException("/setblock 后端需要 ExplicitBlocksBatch。", uncertain: false);
        }

        // Validate every command before the first mutation. A malformed later
        // placement must not leave an otherwise valid prefix partially sent.
        var commands = blocks.Blocks
            .Select(placement =>
            {
                if (placement.States is { Count: > 0 })
                {
                    throw new BackendException(
                        $"/setblock 暂不支持方块 states：{placement.Position}。",
                        uncertain: false);
                }

                return _safety.BuildNativeSetBlock(placement.Position, placement.Block);
            })
            .ToArray();
        var calls = new List<string>(commands.Length);
        var mutationDispatched = false;
        try
        {
            for (var index = 0; index < blocks.Blocks.Count; index++)
            {
                if (!Capabilities.IsVerifiedFor(_targetFingerprint))
                {
                    throw new BackendException(
                        "/setblock 能力证明在批次完成前已过期或失效，停止发送后续命令。",
                        uncertain: mutationDispatched);
                }

                var placement = blocks.Blocks[index];
                var command = commands[index];
                mutationDispatched = true;
                await _mcc.SendChatAsync(command, cancellationToken).ConfigureAwait(false);
                calls.Add($"mcc_send_chat({command})");
                // The send response, including a human-readable "changed block"
                // message, is diagnostic only. Fresh world sampling is proof.
                await _verify(placement.Position, placement.Block, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new BackendOperationResult(
                batch.BatchId,
                true,
                false,
                "/setblock 已逐点发送并完成独立方块验证。",
                batch.BlockCount,
                calls);
        }
        catch (McpException exception)
        {
            throw BackendFailure.FromMcp("/setblock", exception, mutationDispatched);
        }
        catch (OperationCanceledException exception) when (mutationDispatched)
        {
            throw BackendFailure.FromException("/setblock", exception, mutationDispatched);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackendException exception) when (mutationDispatched && !exception.Uncertain)
        {
            throw BackendFailure.FromException("/setblock", exception, mutationDispatched);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw BackendFailure.FromException("/setblock", exception, mutationDispatched);
        }
    }
}
