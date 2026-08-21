using System.Text.Json;
using System.Text.Json.Serialization;
using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Mcp;

public sealed record McpJsonRpcError(
    int Code,
    string Message,
    JsonElement? Data = null);

public sealed record McpJsonRpcResponse(
    JsonElement? Result,
    McpJsonRpcError? Error,
    JsonElement? Id)
{
    public bool IsError => Error is not null;
}

public sealed record McpToolDefinition(
    string Name,
    string? Description,
    JsonElement InputSchema);

public sealed record McpToolResult(
    IReadOnlyList<JsonElement> Content,
    bool IsError,
    JsonElement? StructuredContent = null,
    JsonElement? RawResult = null)
{
    public string ToSummary()
    {
        var text = Content
            .Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("text").GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item));
        return string.Join("\n", text);
    }

    public string ToDiagnosticText()
    {
        var content = string.Join("\n", Content.Select(item => item.ToString()));
        var structured = StructuredContent?.ToString() ?? string.Empty;
        var raw = RawResult?.ToString() ?? string.Empty;
        return string.Join("\n", new[] { ToSummary(), content, structured, raw }
            .Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    public bool TryGetBlockId(out string blockId)
    {
        foreach (var item in EnumeratePayloads())
        {
            if (TryFindBlockId(item, out blockId))
            {
                return true;
            }
        }

        blockId = string.Empty;
        return false;
    }

    public bool TryGetBlockSample(
        out string blockId,
        out BlockPosition? returnedPosition)
    {
        // A world sample is authoritative only when the response binds the
        // material to the requested coordinates. Keep TryGetBlockId lenient
        // for generic tool payloads, but never let a coordinate-less material
        // become proof for mcc_world_block_at.
        //
        // Machine-readable fields take precedence over content text. A stale
        // JSON diagnostic must not override (or be used to reconcile) a real
        // tool result merely because it happens to contain the same block.
        var machineCandidates =
            new List<(string BlockId, BlockPosition Position)>();
        var machineSawUnboundBlock = false;
        if (StructuredContent is { } structured)
        {
            CollectBlockSamples(
                structured,
                null,
                machineCandidates,
                false,
                true,
                ref machineSawUnboundBlock);
        }

        if (RawResult is { } raw)
        {
            CollectBlockSamples(
                raw,
                null,
                machineCandidates,
                false,
                true,
                ref machineSawUnboundBlock);
        }

        if (machineCandidates.Count > 0 || machineSawUnboundBlock)
        {
            return TryResolveBlockSamples(
                machineCandidates,
                out blockId,
                out returnedPosition);
        }

        // Some streamable HTTP deployments expose the only structured value
        // as JSON inside content[].text. Use that as a last-resort fallback,
        // but never combine it with machine-readable candidates above.
        var contentCandidates =
            new List<(string BlockId, BlockPosition Position)>();
        foreach (var item in Content)
        {
            var contentSawUnboundBlock = false;
            CollectBlockSamples(
                item,
                null,
                contentCandidates,
                true,
                false,
                ref contentSawUnboundBlock);
        }

        return TryResolveBlockSamples(
            contentCandidates,
            out blockId,
            out returnedPosition);
    }

    private static bool TryResolveBlockSamples(
        IReadOnlyList<(string BlockId, BlockPosition Position)> candidates,
        out string blockId,
        out BlockPosition? returnedPosition)
    {
        if (candidates.Count == 0)
        {
            blockId = string.Empty;
            returnedPosition = null;
            return false;
        }

        var first = candidates[0];
        if (candidates.Any(candidate =>
                candidate.Position != first.Position
                || !string.Equals(
                    candidate.BlockId,
                    first.BlockId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            // Conflicting machine-readable payloads are ambiguous. Do not
            // allow a stale or unrelated response to become proof.
            blockId = string.Empty;
            returnedPosition = null;
            return false;
        }

        blockId = first.BlockId;
        returnedPosition = first.Position;
        return true;
    }

    public bool TryGetString(out string value, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        if (propertyNames.Length == 0)
        {
            value = string.Empty;
            return false;
        }

        var names = propertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in EnumeratePayloads())
        {
            if (TryFindString(item, names, out value))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private IEnumerable<JsonElement> EnumeratePayloads()
    {
        // Prefer machine-readable result fields over human-readable content;
        // chat-style text can be stale or merely summarize an operation.
        if (StructuredContent is { } structured)
        {
            yield return structured;
        }

        if (RawResult is { } raw)
        {
            yield return raw;
        }

        foreach (var item in Content)
        {
            yield return item;
        }
    }

    public bool TryGetItemId(out string itemId) =>
        TryGetString(
            out itemId,
            "itemType",
            "item_type",
            "heldItem",
            "held_item",
            "mainHand",
            "main_hand",
            "selectedItem",
            "selected_item",
            "handItem",
            "hand_item");

    private static bool TryFindString(
        JsonElement element,
        IReadOnlySet<string> propertyNames,
        out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyNames.Contains(property.Name)
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        value = property.Value.GetString()!;
                        return true;
                    }

                    if (TryFindString(property.Value, propertyNames, out value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindString(item, propertyNames, out value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        return TryFindString(document.RootElement, propertyNames, out value);
                    }
                    catch (JsonException)
                    {
                        // Text content is not necessarily JSON; keep searching other fields.
                    }
                }

                break;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryFindBlockId(JsonElement element, out string blockId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryFindDirectBlockId(element, out blockId))
                {
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindBlockId(property.Value, out blockId))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindBlockId(item, out blockId))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        return TryFindBlockId(document.RootElement, out blockId);
                    }
                    catch (JsonException)
                    {
                        // Text content is not necessarily JSON; keep searching.
                    }
                }

                break;
        }

        blockId = string.Empty;
        return false;
    }

    private static void CollectBlockSamples(
        JsonElement element,
        BlockPosition? inheritedPosition,
        ICollection<(string BlockId, BlockPosition Position)> candidates,
        bool allowJsonText,
        bool skipHumanReadableProperties,
        ref bool sawUnboundBlock)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var effectivePosition = TryGetPosition(element, out var position)
                    ? position
                    : inheritedPosition;
                if (TryFindDirectBlockId(element, out var directBlockId))
                {
                    if (effectivePosition is { } boundPosition)
                    {
                        candidates.Add((directBlockId, boundPosition));
                    }
                    else
                    {
                        sawUnboundBlock = true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (skipHumanReadableProperties
                        && IsHumanReadableProperty(property.Name))
                    {
                        continue;
                    }

                    CollectBlockSamples(
                        property.Value,
                        effectivePosition,
                        candidates,
                        allowJsonText,
                        skipHumanReadableProperties,
                        ref sawUnboundBlock);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectBlockSamples(
                        item,
                        inheritedPosition,
                        candidates,
                        allowJsonText,
                        skipHumanReadableProperties,
                        ref sawUnboundBlock);
                }

                break;
            case JsonValueKind.String:
                if (!allowJsonText)
                {
                    break;
                }

                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        // JSON text is a human-readable content channel. It
                        // must carry its own coordinates; never bind a bare
                        // material summary to coordinates from an outer wrapper.
                        CollectBlockSamples(
                            document.RootElement,
                            null,
                            candidates,
                            true,
                            false,
                            ref sawUnboundBlock);
                    }
                    catch (JsonException)
                    {
                        // Text content is not necessarily JSON; keep searching.
                    }
                }

                break;
        }
    }

    private static bool IsHumanReadableProperty(string name) =>
        name.Equals("content", StringComparison.OrdinalIgnoreCase)
        || name.Equals("text", StringComparison.OrdinalIgnoreCase)
        || name.Equals("message", StringComparison.OrdinalIgnoreCase)
        || name.Equals("summary", StringComparison.OrdinalIgnoreCase)
        || name.Equals("description", StringComparison.OrdinalIgnoreCase)
        || name.Equals("detail", StringComparison.OrdinalIgnoreCase)
        || name.Equals("diagnostic", StringComparison.OrdinalIgnoreCase)
        || name.Equals("reason", StringComparison.OrdinalIgnoreCase)
        || name.Equals("chat", StringComparison.OrdinalIgnoreCase)
        || name.Equals("history", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindDirectBlockId(JsonElement element, out string blockId)
    {
        string? candidate = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!IsBlockProperty(property.Name)
                || property.Value.ValueKind != JsonValueKind.String
                || !TryNormalizeBlockId(property.Value.GetString(), out var normalized))
            {
                continue;
            }

            if (candidate is not null
                && !string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
            {
                blockId = string.Empty;
                return false;
            }

            candidate = normalized;
        }

        blockId = candidate ?? string.Empty;
        return candidate is not null;
    }

    private static bool TryGetPosition(JsonElement element, out BlockPosition position)
    {
        if (TryGetInt32(element, "x", out var x)
            && TryGetInt32(element, "y", out var y)
            && TryGetInt32(element, "z", out var z))
        {
            position = new BlockPosition(x, y, z);
            return true;
        }

        position = default;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && candidate.Value.ValueKind == JsonValueKind.Number
                && candidate.Value.TryGetInt32(out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsBlockProperty(string name) =>
        name.Equals("block", StringComparison.OrdinalIgnoreCase)
        || name.Equals("blockId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("block_id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("blockType", StringComparison.OrdinalIgnoreCase)
        || name.Equals("block_type", StringComparison.OrdinalIgnoreCase)
        // MCC's mcc_world_block_at uses the textual material name; the
        // numeric blockId is metadata and cannot safely identify a block.
        || name.Equals("material", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeBlockId(string? value, out string blockId)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Any(char.IsWhiteSpace)
            || normalized.Any(char.IsControl))
        {
            blockId = string.Empty;
            return false;
        }

        normalized = normalized.Contains(':', StringComparison.Ordinal)
            ? NormalizeNamespacedBlockId(normalized)
            : ToMinecraftMaterialId(normalized);
        blockId = normalized.Contains(':', StringComparison.Ordinal)
            ? normalized
            : $"minecraft:{normalized}";
        var separator = blockId.IndexOf(':');
        if (separator <= 0 || separator == blockId.Length - 1
            || !string.Equals(blockId[..separator], "minecraft", StringComparison.Ordinal)
            || blockId[(separator + 1)..].Any(character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_'
                    or '/'
                    or '.'
                    or '-')))
        {
            blockId = string.Empty;
            return false;
        }

        return true;
    }

    private static string NormalizeNamespacedBlockId(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return value.ToLowerInvariant();
        }

        var namespaceName = value[..separator].ToLowerInvariant();
        var path = ToMinecraftPath(value[(separator + 1)..]);
        return $"{namespaceName}:{path}";
    }

    private static string ToMinecraftMaterialId(string value) =>
        $"minecraft:{ToMinecraftPath(value)}";

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

public static class McpToolResultInspector
{
    public static McpFailureKind? ClassifyFailure(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var diagnostic = result.ToDiagnosticText();
        var normalized = diagnostic.ToLowerInvariant();

        if (ContainsAny(normalized, "invalid_args", "invalid argument", "invalid arguments"))
        {
            return McpFailureKind.InvalidArguments;
        }

        if (ContainsAny(normalized, "permission_denied", "permission denied", "forbidden", "unauthorized", "not permitted"))
        {
            return McpFailureKind.PermissionDenied;
        }

        if (ContainsAny(normalized, "capability_disabled", "feature_disabled"))
        {
            return McpFailureKind.CapabilityDisabled;
        }

        if (ContainsAny(normalized, "action_incomplete", "action incomplete"))
        {
            return McpFailureKind.RemoteFailure;
        }

        if (ContainsAny(normalized, "success=false", "\"success\":false", "ok=false", "\"ok\":false"))
        {
            return McpFailureKind.RemoteFailure;
        }

        if (result.StructuredContent is { } structured
                && HasFalseStatus(structured)
            || result.RawResult is { } raw
                && HasFalseStatus(raw)
            || result.Content.Any(HasFalseStatus))
        {
            return McpFailureKind.RemoteFailure;
        }

        return result.IsError ? McpFailureKind.RemoteFailure : null;
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));

    private static bool HasFalseStatus(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined
            || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.Name.Equals("success", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("ok", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.False)
                    {
                        return true;
                    }
                    else if (property.Name.Equals("action_incomplete", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (HasFalseStatus(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(HasFalseStatus);
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        return HasFalseStatus(document.RootElement);
                    }
                    catch (JsonException)
                    {
                        // Text content may be ordinary diagnostic text.
                    }
                }

                return false;
            default:
                return false;
        }
    }
}

public sealed record McpServerCapabilities(
    JsonElement? RawCapabilities,
    IReadOnlyList<McpToolDefinition> Tools);

public enum McpFailureKind
{
    Unknown,
    Transport,
    Timeout,
    SessionExpired,
    Http,
    Protocol,
    ToolNotFound,
    InvalidArguments,
    PermissionDenied,
    CapabilityDisabled,
    RemoteFailure
}

public sealed class McpException : Exception
{
    public McpException(McpFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public McpFailureKind Kind { get; }
    public int? HttpStatusCode { get; init; }
    public int? RpcErrorCode { get; init; }
}

public static class McpJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
