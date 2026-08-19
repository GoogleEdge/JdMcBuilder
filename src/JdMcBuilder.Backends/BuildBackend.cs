using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.Backends;

public enum BackendStatus
{
    Available,
    Unverified,
    Unavailable
}

public sealed class BackendVerification
{
    private BackendVerification(
        string backendId,
        string targetFingerprint,
        DateTimeOffset verifiedAt,
        DateTimeOffset expiresAt)
    {
        BackendId = backendId;
        TargetFingerprint = targetFingerprint;
        VerifiedAt = verifiedAt;
        ExpiresAt = expiresAt;
    }

    public string BackendId { get; }
    public string TargetFingerprint { get; }
    public DateTimeOffset VerifiedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal bool IsValidFor(string backendId, string targetFingerprint)
    {
        return string.Equals(BackendId, backendId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(TargetFingerprint)
            && !string.IsNullOrWhiteSpace(targetFingerprint)
            && VerifiedAt <= DateTimeOffset.UtcNow
            && ExpiresAt > DateTimeOffset.UtcNow
            && string.Equals(TargetFingerprint, targetFingerprint, StringComparison.Ordinal);
    }

    internal static BackendVerification Create(
        string backendId,
        string targetFingerprint,
        DateTimeOffset verifiedAt,
        TimeSpan validity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFingerprint);
        if (validity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "验证有效期必须大于零。" );
        }

        var expiresAt = verifiedAt + validity;
        if (expiresAt <= verifiedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "验证有效期超出可表示的时间范围。" );
        }

        return new BackendVerification(backendId, targetFingerprint, verifiedAt, expiresAt);
    }

    // 仅供单元测试构造一个短期、明确标注为测试目标的证明；生产代码必须来自能力探测。
    internal static BackendVerification CreateForTesting(string backendId) =>
        Create(backendId, "test-target", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    internal static BackendVerification CreateForTesting(
        string backendId,
        string targetFingerprint) =>
        Create(backendId, targetFingerprint, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
}

public sealed record BackendCapabilities
{
    public BackendCapabilities(
        string id,
        string displayName,
        BackendStatus status,
        bool supportsFill,
        bool supportsExplicitBlocks,
        string reason,
        BackendVerification? verification = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (status == BackendStatus.Available && verification is null)
        {
            throw new ArgumentException(
                "Available 后端必须携带由目标绑定能力探测创建的验证证明。",
                nameof(verification));
        }

        if (verification is not null
            && !verification.IsValidFor(id, verification.TargetFingerprint))
        {
            throw new ArgumentException(
                "后端验证证明无效、已过期或与后端 ID 不匹配。",
                nameof(verification));
        }

        Id = id;
        DisplayName = displayName;
        SupportsFill = supportsFill;
        SupportsExplicitBlocks = supportsExplicitBlocks;
        Reason = reason;
        Verification = verification;
        Status = verification is not null
            ? BackendStatus.Available
            : status == BackendStatus.Unavailable
                ? BackendStatus.Unavailable
                : BackendStatus.Unverified;
    }

    internal static BackendCapabilities CreateVerified(
        string id,
        string displayName,
        bool supportsFill,
        bool supportsExplicitBlocks,
        string reason,
        string targetFingerprint,
        DateTimeOffset verifiedAt,
        TimeSpan validity) =>
        new(
            id,
            displayName,
            BackendStatus.Available,
            supportsFill,
            supportsExplicitBlocks,
            reason,
            BackendVerification.Create(id, targetFingerprint, verifiedAt, validity));

    internal static BackendCapabilities CreateVerifiedForTesting(
        string id,
        string displayName,
        bool supportsFill,
        bool supportsExplicitBlocks,
        string reason) =>
        new(
            id,
            displayName,
            BackendStatus.Available,
            supportsFill,
            supportsExplicitBlocks,
            reason,
            BackendVerification.CreateForTesting(id));

    internal static BackendCapabilities CreateVerifiedForTesting(
        string id,
        string displayName,
        bool supportsFill,
        bool supportsExplicitBlocks,
        string reason,
        string targetFingerprint) =>
        new(
            id,
            displayName,
            BackendStatus.Available,
            supportsFill,
            supportsExplicitBlocks,
            reason,
            BackendVerification.CreateForTesting(id, targetFingerprint));

    public string Id { get; }
    public string DisplayName { get; }
    public BackendStatus Status { get; }
    public bool SupportsFill { get; }
    public bool SupportsExplicitBlocks { get; }
    public string Reason { get; }
    public BackendVerification? Verification { get; }
    public bool IsVerified => IsVerifiedFor(Verification?.TargetFingerprint);

    public bool IsVerifiedFor(string? targetFingerprint) =>
        Status == BackendStatus.Available
        && Verification is not null
        && !string.IsNullOrWhiteSpace(targetFingerprint)
        && Verification.IsValidFor(Id, targetFingerprint);
}

public sealed record BackendOperationResult(
    string BatchId,
    bool Succeeded,
    bool Uncertain,
    string Summary,
    long BlocksChanged,
    IReadOnlyList<string> ToolCalls);

public interface IBuildBackend
{
    BackendCapabilities Capabilities { get; }
    Task<BackendOperationResult> ExecuteAsync(BuildBatch batch, CancellationToken cancellationToken = default);
}

public sealed class BackendException : Exception
{
    public BackendException(string message, bool uncertain = false, Exception? inner = null)
        : base(message, inner)
    {
        Uncertain = uncertain;
    }

    public bool Uncertain { get; }
}

internal static class BackendFailure
{
    public static BackendException FromMcp(
        string operation,
        McpException exception,
        bool mutationMayHaveBeenDispatched,
        bool priorMutationCompleted = false)
    {
        var uncertain = priorMutationCompleted || mutationMayHaveBeenDispatched;
        return new BackendException(
            $"{operation} MCP 调用失败：{exception.Message}",
            uncertain,
            exception);
    }

    public static BackendException FromException(
        string operation,
        Exception exception,
        bool mutationMayHaveBeenDispatched,
        bool priorMutationCompleted = false)
    {
        return new BackendException(
            $"{operation} 操作失败：{exception.Message}",
            mutationMayHaveBeenDispatched || priorMutationCompleted,
            exception);
    }

}

public sealed class BackendSelector
{
    public IBuildBackend? Select(
        IEnumerable<IBuildBackend> backends,
        BuildBatch batch,
        bool allowUnverified = false,
        string? targetFingerprint = null)
    {
        var candidates = backends
            .Where(backend =>
                (backend.Capabilities.SupportsFill && batch is FillBatch)
                || (backend.Capabilities.SupportsExplicitBlocks && batch is ExplicitBlocksBatch))
            .OrderBy(backend => backend.Capabilities.Id switch
            {
                "worldedit" => 0,
                "native-fill" => 1,
                "native-setblock" => 2,
                _ => 99
            });
        return candidates.FirstOrDefault(backend =>
                targetFingerprint is not null
                    ? backend.Capabilities.IsVerifiedFor(targetFingerprint)
                    : backend.Capabilities.IsVerified)
            ?? (allowUnverified
                ? candidates.FirstOrDefault(backend => backend.Capabilities.Status == BackendStatus.Unverified)
                : null);
    }
}
