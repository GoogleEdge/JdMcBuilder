using System.Text.Json;
using System.Text.Json.Serialization;

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
    JsonElement? StructuredContent = null)
{
    public string ToSummary()
    {
        var text = Content
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item));
        return string.Join("\n", text);
    }

    public string ToDiagnosticText()
    {
        var content = string.Join("\n", Content.Select(item => item.ToString()));
        var structured = StructuredContent?.ToString() ?? string.Empty;
        return string.Join("\n", new[] { ToSummary(), content, structured }
            .Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    public bool TryGetBlockId(out string blockId)
    {
        foreach (var item in Content)
        {
            if (TryFindBlockId(item, out blockId))
            {
                return true;
            }
        }

        if (StructuredContent is { } structured
            && TryFindBlockId(structured, out blockId))
        {
            return true;
        }

        blockId = string.Empty;
        return false;
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
        foreach (var item in Content)
        {
            if (TryFindString(item, names, out value))
            {
                return true;
            }
        }

        if (StructuredContent is { } structured
            && TryFindString(structured, names, out value))
        {
            return true;
        }

        value = string.Empty;
        return false;
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
                foreach (var property in element.EnumerateObject())
                {
                    if (IsBlockProperty(property.Name)
                        && property.Value.ValueKind == JsonValueKind.String
                        && TryNormalizeBlockId(property.Value.GetString(), out blockId))
                    {
                        return true;
                    }

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
                        // Text content is not necessarily JSON; keep searching other fields.
                    }
                }

                break;
        }

        blockId = string.Empty;
        return false;
    }

    private static bool IsBlockProperty(string name) =>
        name.Equals("block", StringComparison.OrdinalIgnoreCase)
        || name.Equals("blockId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("block_id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("blockType", StringComparison.OrdinalIgnoreCase)
        || name.Equals("block_type", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeBlockId(string? value, out string blockId)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Any(char.IsWhiteSpace)
            || normalized.Any(char.IsControl))
        {
            blockId = string.Empty;
            return false;
        }

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

        if (ContainsAny(normalized, "permission", "forbidden", "unauthorized", "not permitted"))
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

        if ((result.StructuredContent is { } structured
                && HasFalseStatus(structured))
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
                    if ((property.NameEquals("success") || property.NameEquals("ok"))
                        && property.Value.ValueKind == JsonValueKind.False)
                    {
                        return true;
                    }
                    else if (property.NameEquals("action_incomplete")
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
