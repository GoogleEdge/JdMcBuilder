namespace JdMcBuilder.Core.Blueprint;

public sealed record CoordinateSystem(
    BlockPosition Origin,
    string North,
    string Unit);

public sealed record BlueprintDocument(
    string Format,
    string? Project,
    CoordinateSystem CoordinateSystem,
    BlockRange Bounds,
    IReadOnlyList<BlueprintPhase> Phases);

public sealed record BlueprintPhase(
    string Id,
    string Name,
    int Order,
    IReadOnlyList<BlueprintOperation> Operations);

public abstract record BlueprintOperation(string Id, string Type);

public sealed record FillOperation(
    string Id,
    BlockRange Range,
    string Block,
    IReadOnlyDictionary<string, string>? States = null)
    : BlueprintOperation(Id, "fill");

public sealed record BlocksOperation(
    string Id,
    IReadOnlyList<BlockPlacement> Blocks)
    : BlueprintOperation(Id, "blocks");

public sealed record BlueprintParseError(
    string Code,
    string Message,
    int? Line = null,
    string? Path = null);

public sealed class BlueprintParseException : Exception
{
    public BlueprintParseException(BlueprintParseError error, Exception? inner = null)
        : base(error.Message, inner)
    {
        Error = error;
    }

    public BlueprintParseError Error { get; }
}
