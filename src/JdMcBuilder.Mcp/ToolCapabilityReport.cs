namespace JdMcBuilder.Mcp;

public enum CapabilityStatus
{
    Available,
    Unverified,
    Unavailable
}

public sealed record ToolCapability(string Capability, CapabilityStatus Status, string? ToolName, string Reason);

public sealed record MccCapabilityReport(IReadOnlyList<ToolCapability> Capabilities)
{
    public ToolCapability? Find(string capability) => Capabilities.FirstOrDefault(item => item.Capability == capability);
}

public static class MccCapabilityDetector
{
    public static MccCapabilityReport Detect(IReadOnlyDictionary<string, McpToolDefinition> tools)
    {
        var capabilities = new List<ToolCapability>
        {
            DetectCommandCapability(tools, "worldedit", "WorldEdit 命令后端"),
            DetectCommandCapability(tools, "native-fill", "Minecraft /fill 命令后端"),
            DetectCommandCapability(tools, "native-setblock", "Minecraft /setblock 命令后端"),
            new("world-sampling", tools.ContainsKey("mcc_world_block_at")
                ? CapabilityStatus.Available
                : CapabilityStatus.Unavailable, "mcc_world_block_at", "单点采样工具。"),
            new("session-preflight", tools.ContainsKey("mcc_session_status")
                ? CapabilityStatus.Available
                : CapabilityStatus.Unavailable, "mcc_session_status", "施工前会话检查。")
        };
        return new MccCapabilityReport(capabilities);
    }

    private static ToolCapability DetectCommandCapability(IReadOnlyDictionary<string, McpToolDefinition> tools, string id, string label)
    {
        var missing = new[] { "mcc_send_chat", "mcc_world_block_at" }
            .Where(name => !tools.ContainsKey(name))
            .ToArray();
        if (missing.Length > 0)
        {
            return new(id, CapabilityStatus.Unavailable, null, $"缺少 {string.Join(", ", missing)}，无法尝试 {label}。" );
        }

        var historyNote = tools.ContainsKey("mcc_chat_history")
            ? "聊天历史仅作可选诊断"
            : "未发现 mcc_chat_history（不影响独立方块采样验证）";
        return new(id, CapabilityStatus.Unverified, "mcc_send_chat", $"发现写入和独立方块读取工具；{historyNote}；需要在 Leaf 1.21.11 测试世界独立验证 {label} 权限和返回结果。" );
    }
}
