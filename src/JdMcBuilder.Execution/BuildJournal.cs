using System.Collections.Concurrent;
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

    public async Task<IAsyncDisposable> AcquireExecutionAsync(CancellationToken cancellationToken = default)
    {
        var gate = PathGates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }

    public async Task SaveAsync(BuildJournalState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
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

    public async Task<BuildJournalState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var state = await JsonSerializer.DeserializeAsync<BuildJournalState>(stream, _options, cancellationToken).ConfigureAwait(false);
        return state is null
            ? null
            : state with
            {
                CompletedBatches = state.CompletedBatches ?? Array.Empty<string>(),
                UncertainBatches = state.UncertainBatches ?? Array.Empty<string>()
            };
    }

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
