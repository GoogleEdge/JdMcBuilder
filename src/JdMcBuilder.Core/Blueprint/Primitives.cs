namespace JdMcBuilder.Core.Blueprint;

public readonly record struct BlockPosition(int X, int Y, int Z)
{
    public override string ToString() => $"{X} {Y} {Z}";

    public static BlockPosition Min(BlockPosition left, BlockPosition right) =>
        new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

    public static BlockPosition Max(BlockPosition left, BlockPosition right) =>
        new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));
}

public readonly record struct BlockRange(BlockPosition Min, BlockPosition Max)
{
    public bool IsValid =>
        Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

    public long Volume
    {
        get
        {
            if (TryGetVolume(out var volume))
            {
                return volume;
            }

            throw new OverflowException("方块范围体积超出 Int64 范围。" );
        }
    }

    public bool TryGetVolume(out long volume)
    {
        if (!IsValid)
        {
            volume = 0;
            return false;
        }

        try
        {
            checked
            {
                volume = ((long)Max.X - Min.X + 1)
                    * ((long)Max.Y - Min.Y + 1)
                    * ((long)Max.Z - Min.Z + 1);
            }

            return true;
        }
        catch (OverflowException)
        {
            volume = 0;
            return false;
        }
    }

    public bool Contains(BlockPosition position) =>
        IsValid
        && position.X >= Min.X && position.X <= Max.X
        && position.Y >= Min.Y && position.Y <= Max.Y
        && position.Z >= Min.Z && position.Z <= Max.Z;

    public bool Contains(BlockRange other) =>
        IsValid && other.IsValid && Contains(other.Min) && Contains(other.Max);

    public static BlockRange FromUnordered(BlockPosition first, BlockPosition second) =>
        new(BlockPosition.Min(first, second), BlockPosition.Max(first, second));

    public override string ToString() => $"[{Min}..{Max}]";
}

public sealed record BlockPlacement(
    BlockPosition Position,
    string Block,
    IReadOnlyDictionary<string, string>? States = null);
