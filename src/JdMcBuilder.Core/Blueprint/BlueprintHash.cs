using System.Security.Cryptography;

namespace JdMcBuilder.Core.Blueprint;

public static class BlueprintHash
{
    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
