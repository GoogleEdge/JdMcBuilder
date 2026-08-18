using System.Text.RegularExpressions;
using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Backends;

public sealed class CommandSafety
{
    private static readonly Regex BlockId = new(@"\Aminecraft:[a-z0-9_/.-]+\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // WorldEdit commands are sent through MCC's chat tool, not the internal
    // command tool. The target deployment accepts the normal double-slash
    // WorldEdit form and requires comma-separated block-vector arguments.
    public string BuildWorldEditSelection(BlockRange range)
    {
        ValidateRange(range);
        return $"//pos {ValidatePosition(range.Min, ',')} {ValidatePosition(range.Max, ',')}";
    }

    public string BuildWorldEditSet(string block) => $"//set {ValidateBlock(block)}";
    public string BuildWorldEditReplace(string from, string to) => $"//replace {ValidateBlock(from)} {ValidateBlock(to)}";
    public string BuildNativeFill(BlockRange range, string block)
    {
        ValidateRange(range);
        return $"/fill {range.Min.X} {range.Min.Y} {range.Min.Z} {range.Max.X} {range.Max.Y} {range.Max.Z} {ValidateBlock(block)}";
    }

    public string BuildInternalSend(string chatText)
    {
        if (string.IsNullOrWhiteSpace(chatText) || chatText.Any(char.IsControl))
        {
            throw new ArgumentException("命令文本为空或包含控制字符。", nameof(chatText));
        }

        var trimmed = chatText.Trim();
        if (trimmed.StartsWith("/", StringComparison.Ordinal)
            || trimmed.StartsWith("send ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed.Contains("exec", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("script", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("connect", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("reload", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("quit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("exit", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendException("内部命令文本不在施工白名单中。" );
        }

        return $"send {trimmed}";
    }

    public string ValidateBlock(string block)
    {
        if (!BlockId.IsMatch(block))
        {
            throw new BackendException($"非法命令方块 ID：{block}。" );
        }

        return block;
    }

    private static string ValidatePosition(BlockPosition position, char separator = ' ')
    {
        const long maxCoordinate = 30_000_000;
        if (Math.Abs((long)position.X) > maxCoordinate
            || Math.Abs((long)position.Y) > maxCoordinate
            || Math.Abs((long)position.Z) > maxCoordinate)
        {
            throw new BackendException($"命令坐标超出安全上限：{position}。" );
        }

        return $"{position.X}{separator}{position.Y}{separator}{position.Z}";
    }

    private static void ValidateRange(BlockRange range)
    {
        if (!range.IsValid)
        {
            throw new BackendException($"命令范围无效：{range}。" );
        }

        _ = ValidatePosition(range.Min);
        _ = ValidatePosition(range.Max);
        if (!range.TryGetVolume(out var volume) || volume <= 0)
        {
            throw new BackendException($"命令范围体积无效：{range}。" );
        }
    }
}
