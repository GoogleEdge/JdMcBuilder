using System.Text.Json;

namespace JdMcBuilder.Core.Blueprint;

public static class BlueprintParser
{
    public static async Task<BlueprintDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadJsonLinesAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseDocument(document.RootElement);
    }

    public static async IAsyncEnumerable<BlueprintBlockRecord> ReadJsonLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            BlueprintBlockRecord record;
            try
            {
                using var json = JsonDocument.Parse(line);
                record = ParseJsonLine(json.RootElement, lineNumber);
            }
            catch (JsonException exception)
            {
                throw new BlueprintParseException(new("json.invalid", $"JSONL 第 {lineNumber} 行格式无效。", lineNumber), exception);
            }

            yield return record;
        }
    }

    public static async Task<BlueprintDocument> LoadJsonLinesAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var recordsByPhase = new Dictionary<string, List<BlockPlacement>>(StringComparer.Ordinal);
        var orderByPhase = new Dictionary<string, int>(StringComparer.Ordinal);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var record = ParseJsonLine(json.RootElement, lineNumber);
                if (!recordsByPhase.TryGetValue(record.Phase, out var blocks))
                {
                    blocks = [];
                    recordsByPhase[record.Phase] = blocks;
                    orderByPhase[record.Phase] = orderByPhase.Count * 10;
                }

                blocks.Add(new BlockPlacement(record.Position, record.Block, record.States));
            }
            catch (BlueprintParseException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new BlueprintParseException(new("json.invalid", $"JSONL 第 {lineNumber} 行格式无效。", lineNumber), exception);
            }
        }

        if (recordsByPhase.Count == 0)
        {
            throw new BlueprintParseException(new("jsonl.empty", "JSONL 文件没有任何方块记录。"));
        }

        var allPositions = recordsByPhase.Values.SelectMany(items => items).Select(item => item.Position).ToArray();
        var bounds = new BlockRange(
            allPositions.Aggregate(BlockPosition.Min),
            allPositions.Aggregate(BlockPosition.Max));
        var phases = recordsByPhase.Select(pair => new BlueprintPhase(
            pair.Key,
            pair.Key,
            orderByPhase[pair.Key],
            new BlueprintOperation[] { new BlocksOperation($"{pair.Key}-blocks", pair.Value) }))
            .OrderBy(phase => phase.Order)
            .ToArray();
        return new BlueprintDocument(
            "mc-blueprint/v1",
            Path.GetFileNameWithoutExtension("blueprint.jsonl"),
            new CoordinateSystem(bounds.Min, "+z", "minecraft-block"),
            bounds,
            phases);
    }

    public static BlueprintDocument ParseDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new BlueprintParseException(new("document.not_object", "蓝图根节点必须是 JSON 对象。"));
        }

        var format = RequiredString(root, "format", "format");
        var project = OptionalString(root, "project");
        var coordinateSystem = ParseCoordinateSystem(root.TryGetProperty("coordinateSystem", out var coordinate) ? coordinate : default);
        var bounds = ParseRange(RequiredProperty(root, "bounds", "bounds"), "bounds");
        var phaseElements = RequiredProperty(root, "phases", "phases");
        if (phaseElements.ValueKind != JsonValueKind.Array)
        {
            throw new BlueprintParseException(new("phases.not_array", "phases 必须是数组。", Path: "phases"));
        }

        var phases = new List<BlueprintPhase>();
        foreach (var phaseElement in phaseElements.EnumerateArray())
        {
            phases.Add(ParsePhase(phaseElement));
        }

        return new BlueprintDocument(format, project, coordinateSystem, bounds, phases);
    }

    private static BlueprintPhase ParsePhase(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new BlueprintParseException(new("phase.not_object", "phase 必须是 JSON 对象。", Path: "phase"));
        }

        var id = RequiredString(element, "id", "phase.id");
        var name = OptionalString(element, "name") ?? id;
        var order = OptionalInt(element, "order") ?? 0;
        var operationElements = RequiredProperty(element, "operations", $"phase[{id}].operations");
        if (operationElements.ValueKind != JsonValueKind.Array)
        {
            throw new BlueprintParseException(new("operations.not_array", "operations 必须是数组。", Path: $"phase[{id}].operations"));
        }

        var operations = operationElements.EnumerateArray().Select(operation => ParseOperation(operation, id)).ToArray();
        return new BlueprintPhase(id, name, order, operations);
    }

    private static BlueprintOperation ParseOperation(JsonElement element, string phaseId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new BlueprintParseException(new("operation.not_object", "operation 必须是 JSON 对象。", Path: $"phase[{phaseId}].operation"));
        }

        var id = RequiredString(element, "id", $"phase[{phaseId}].operation.id");
        var type = RequiredString(element, "type", $"phase[{phaseId}].operation.type");
        return type switch
        {
            "fill" => new FillOperation(
                id,
                ParseRange(RequiredProperty(element, "from", $"operation[{id}].from"), $"operation[{id}].from", RequiredProperty(element, "to", $"operation[{id}].to")),
                NormalizeBlock(RequiredString(element, "block", $"operation[{id}].block")),
                ParseStates(element)),
            "blocks" => new BlocksOperation(id, ParseBlocks(element, id)),
            _ => throw new BlueprintParseException(new("operation.type_unknown", $"未知操作类型：{type}。", Path: $"operation[{id}].type"))
        };
    }

    private static IReadOnlyList<BlockPlacement> ParseBlocks(JsonElement element, string id)
    {
        var blocks = RequiredProperty(element, "blocks", $"operation[{id}].blocks");
        if (blocks.ValueKind != JsonValueKind.Array)
        {
            throw new BlueprintParseException(new("blocks.not_array", "blocks 必须是数组。", Path: $"operation[{id}].blocks"));
        }

        var result = new List<BlockPlacement>();
        foreach (var item in blocks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new BlueprintParseException(new("block.not_object", "blocks 中的每项必须是 JSON 对象。", Path: $"operation[{id}].blocks"));
            }

            var position = ParsePosition(RequiredProperty(item, "pos", $"operation[{id}].blocks.pos"), $"operation[{id}].blocks.pos");
            var block = NormalizeBlock(RequiredString(item, "block", $"operation[{id}].blocks.block"));
            result.Add(new BlockPlacement(position, block, ParseStates(item)));
        }

        return result;
    }

    private static BlueprintBlockRecord ParseJsonLine(JsonElement root, int lineNumber)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BlueprintParseException(new("record.not_object", "JSONL 记录必须是 JSON 对象。"));
            }

            var phase = OptionalString(root, "phase") ?? "default";
            var position = ParsePosition(RequiredProperty(root, "pos", "pos", allowMissingArrayFallback: true), "pos", root);
            var block = NormalizeBlock(RequiredString(root, "block", "block"));
            return new BlueprintBlockRecord(phase, position, block, ParseStates(root));
        }
        catch (BlueprintParseException exception)
        {
            throw new BlueprintParseException(exception.Error with { Line = lineNumber }, exception);
        }
    }

    private static CoordinateSystem ParseCoordinateSystem(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block");
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new BlueprintParseException(new("coordinateSystem.not_object", "coordinateSystem 必须是 JSON 对象。", Path: "coordinateSystem"));
        }

        return new CoordinateSystem(
            element.TryGetProperty("origin", out var origin) ? ParsePosition(origin, "coordinateSystem.origin") : new BlockPosition(0, 0, 0),
            OptionalString(element, "north") ?? "+z",
            OptionalString(element, "unit") ?? "minecraft-block");
    }

    private static BlockRange ParseRange(JsonElement from, string path, JsonElement? to = null)
    {
        if (!to.HasValue && from.ValueKind == JsonValueKind.Object)
        {
            var firstName = from.TryGetProperty("from", out var nestedFrom)
                ? "from"
                : from.TryGetProperty("min", out nestedFrom)
                    ? "min"
                    : null;
            var secondName = from.TryGetProperty("to", out var nestedTo)
                ? "to"
                : from.TryGetProperty("max", out nestedTo)
                    ? "max"
                    : null;
            if (firstName is not null && secondName is not null)
            {
                return ParseRange(nestedFrom, $"{path}.{firstName}", nestedTo);
            }
        }

        var first = ParsePosition(from, path);
        var second = to.HasValue
            ? ParsePosition(to.Value, path.Replace("from", "to", StringComparison.Ordinal).Replace("min", "max", StringComparison.Ordinal))
            : first;
        return new BlockRange(first, second);
    }

    private static BlockPosition ParsePosition(JsonElement element, string path, JsonElement? parent = null)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = element.EnumerateArray().ToArray();
            if (values.Length != 3 || values.Any(item => item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out _)))
            {
                throw new BlueprintParseException(new("position.invalid", "坐标必须是包含三个整数的数组。", Path: path));
            }

            return new BlockPosition(values[0].GetInt32(), values[1].GetInt32(), values[2].GetInt32());
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("x", out var x)
            && element.TryGetProperty("y", out var y)
            && element.TryGetProperty("z", out var z))
        {
            return new BlockPosition(
                ParseRequiredInt(x, "x", $"{path}.x"),
                ParseRequiredInt(y, "y", $"{path}.y"),
                ParseRequiredInt(z, "z", $"{path}.z"));
        }

        if (element.ValueKind == JsonValueKind.Undefined
            && parent is { } root
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("x", out _))
        {
            return new BlockPosition(RequiredInt(root, "x", "x"), RequiredInt(root, "y", "y"), RequiredInt(root, "z", "z"));
        }

        throw new BlueprintParseException(new("position.invalid", "坐标必须是包含三个整数的数组或对象。", Path: path));
    }

    private static IReadOnlyDictionary<string, string>? ParseStates(JsonElement element)
    {
        if (!element.TryGetProperty("states", out var states))
        {
            return null;
        }

        if (states.ValueKind != JsonValueKind.Object)
        {
            throw new BlueprintParseException(new("states.invalid", "states 必须是对象，且其值必须是字符串。", Path: "states"));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in states.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || property.Name.Any(char.IsControl)
                || property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString())
                || property.Value.GetString()!.Any(char.IsControl))
            {
                throw new BlueprintParseException(new("states.invalid", "states 的键和值必须是非空、无控制字符的字符串。", Path: $"states.{property.Name}"));
            }

            result[property.Name] = property.Value.GetString()!;
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonElement RequiredProperty(JsonElement element, string property, string path, bool allowMissingArrayFallback = false)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value))
        {
            return value;
        }

        if (allowMissingArrayFallback && property == "pos" && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("x", out _))
        {
            return default;
        }

        throw new BlueprintParseException(new("property.missing", $"缺少必填字段：{property}。", Path: path));
    }

    private static string RequiredString(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BlueprintParseException(new("property.string_required", $"字段 {property} 必须是非空字符串。", Path: path));
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static int RequiredInt(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw new BlueprintParseException(new("property.integer_required", $"字段 {property} 必须是整数。", Path: path));
        }

        return ParseRequiredInt(value, property, path);
    }

    private static int ParseRequiredInt(JsonElement value, string property, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new BlueprintParseException(new("property.integer_required", $"字段 {property} 必须是整数。", Path: path));
        }

        return result;
    }

    private static string NormalizeBlock(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator < 0)
        {
            return $"minecraft:{ToMinecraftPath(trimmed)}";
        }

        var namespaceName = trimmed[..separator].ToLowerInvariant();
        var path = ToMinecraftPath(trimmed[(separator + 1)..]);
        return $"{namespaceName}:{path}";
    }

    private static string ToMinecraftPath(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var previous = index > 0 ? value[index - 1] : '\0';
            var next = index + 1 < value.Length ? value[index + 1] : '\0';
            var startsWord = index > 0
                && char.IsUpper(character)
                && (char.IsLower(previous)
                    || char.IsDigit(previous)
                    || (char.IsUpper(previous) && char.IsLower(next)));
            if (startsWord && builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }

            if (character == '_')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

public sealed record BlueprintBlockRecord(
    string Phase,
    BlockPosition Position,
    string Block,
    IReadOnlyDictionary<string, string>? States = null);
