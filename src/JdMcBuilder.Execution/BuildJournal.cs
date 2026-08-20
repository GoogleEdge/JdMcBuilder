using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JdMcBuilder.Execution;

public sealed record BuildJournalState(
    string SessionId,
    string BlueprintHash,
    string? Dimension,
    string BackendId,
    IReadOnlyList<string> CompletedBatches,
    IReadOnlyList<string> UncertainBatches,
    string? LastError,
    string? TargetFingerprint = null)
{
    public static BuildJournalState Create(
        string blueprintHash,
        string backendId,
        string? targetFingerprint = null) =>
        new(
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"),
            blueprintHash,
            null,
            backendId,
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            targetFingerprint);
}

public sealed record BuildJournalSnapshot(BuildJournalState State, string Revision);

public enum JournalArchiveStatus
{
    Missing,
    Archived,
    NotStale,
    ChangedSinceSnapshot,
    BlockedByUncertain
}

public sealed record JournalArchiveResult(
    JournalArchiveStatus Status,
    BuildJournalState? State,
    string? ArchivePath,
    string Message)
{
    public bool Archived => Status == JournalArchiveStatus.Archived;
}

public sealed class BuildJournal
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BuildJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public async Task<IAsyncDisposable> AcquireExecutionAsync(CancellationToken cancellationToken = default)
    {
        var gate = PathGates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }

    public async Task SaveAsync(BuildJournalState state, CancellationToken cancellationToken = default)
    {
        await using var journalGate = await AcquireExecutionAsync(cancellationToken).ConfigureAwait(false);
        await SaveUnderExecutionAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BuildJournalState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot?.State;
    }

    public async Task<BuildJournalSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var journalGate = await AcquireExecutionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSnapshotUnderExecutionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalArchiveResult> ArchiveStaleAndResetAsync(
        BuildJournalSnapshot expectedSnapshot,
        string currentBlueprintHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentBlueprintHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshot.Revision);

        await using var journalGate = await AcquireExecutionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_path))
        {
            return new JournalArchiveResult(
                JournalArchiveStatus.Missing,
                null,
                null,
                "活动 journal 不存在，未执行归档。 ");
        }

        var (rawBytes, state) = await ReadRawStateUnderExecutionAsync(cancellationToken).ConfigureAwait(false);
        var revision = ComputeRevision(rawBytes);
        if (!string.Equals(revision, expectedSnapshot.Revision, StringComparison.Ordinal)
            || !string.Equals(state.SessionId, expectedSnapshot.State.SessionId, StringComparison.Ordinal)
            || !string.Equals(state.BlueprintHash, expectedSnapshot.State.BlueprintHash, StringComparison.Ordinal))
        {
            return new JournalArchiveResult(
                JournalArchiveStatus.ChangedSinceSnapshot,
                state,
                null,
                "活动 journal 在确认后发生变化，未执行归档；请重新读取并确认。 ");
        }

        if (string.Equals(state.BlueprintHash, currentBlueprintHash, StringComparison.Ordinal))
        {
            return new JournalArchiveResult(
                JournalArchiveStatus.NotStale,
                state,
                null,
                "活动 journal 已对应当前蓝图，未执行归档。 ");
        }

        if (state.UncertainBatches.Count > 0)
        {
            return new JournalArchiveResult(
                JournalArchiveStatus.BlockedByUncertain,
                state,
                null,
                $"活动 journal 包含 {state.UncertainBatches.Count} 个不确定批次；必须先人工/新鲜采样确认，未执行归档。 ");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var archivePath = CreateArchivePath(cancellationToken);
        File.Move(_path, archivePath, overwrite: false);
        return new JournalArchiveResult(
            JournalArchiveStatus.Archived,
            state,
            archivePath,
            $"旧 journal 已归档为 {archivePath}；活动 journal 已留空，未发送任何世界写入。 ");
    }

    internal Task SaveUnderExecutionAsync(
        BuildJournalState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SaveCoreAsync(state, cancellationToken);
    }

    internal Task<BuildJournalState?> LoadUnderExecutionAsync(
        CancellationToken cancellationToken = default) =>
        LoadCoreAsync(cancellationToken);

    private async Task<BuildJournalSnapshot?> ReadSnapshotUnderExecutionAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var (rawBytes, state) = await ReadRawStateUnderExecutionAsync(cancellationToken).ConfigureAwait(false);
        return new BuildJournalSnapshot(state, ComputeRevision(rawBytes));
    }

    private async Task<(byte[] RawBytes, BuildJournalState State)> ReadRawStateUnderExecutionAsync(
        CancellationToken cancellationToken)
    {
        var rawBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        await using var stream = new MemoryStream(rawBytes, writable: false);
        var state = await JsonSerializer.DeserializeAsync<BuildJournalState>(stream, _options, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("journal 内容为空或不是有效的 BuildJournalState。 ");
        }

        return (rawBytes, NormalizeState(state));
    }

    private async Task<BuildJournalState?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var (_, state) = await ReadRawStateUnderExecutionAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task SaveCoreAsync(BuildJournalState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Preserve the original journal error; a stale temporary is harmless.
            }
        }
    }

    private string CreateArchivePath(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("journal 路径没有可用目录。 ");
        var archiveDirectory = Path.Combine(directory, "archives");
        Directory.CreateDirectory(archiveDirectory);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"{Path.GetFileNameWithoutExtension(_path)}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.archive{Path.GetExtension(_path)}";
            var candidate = Path.Combine(archiveDirectory, name);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法创建不冲突的 journal 归档路径。 ");
    }

    private static BuildJournalState NormalizeState(BuildJournalState state) => state with
    {
        CompletedBatches = state.CompletedBatches ?? Array.Empty<string>(),
        UncertainBatches = state.UncertainBatches ?? Array.Empty<string>()
    };

    private static string ComputeRevision(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class GateLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _released;

        public GateLease(SemaphoreSlim gate) => _gate = gate;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
