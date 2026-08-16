namespace JdMcBuilder.Mcp;

public sealed class MccToolClient
{
    private readonly IMcpToolInvoker _invoker;

    public MccToolClient(IMcpToolInvoker invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public IReadOnlyDictionary<string, McpToolDefinition> Tools => _invoker.Tools;
    public Uri? Endpoint => (_invoker as McpClient)?.Endpoint;
    public string? SessionId => (_invoker as McpClient)?.SessionId;

    public Task<McpToolResult> SessionStatusAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_session_status", new { }, cancellationToken);
    public Task<McpToolResult> WorldStateAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_world_state", new { }, cancellationToken);
    public Task<McpToolResult> ServerInfoAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_server_info", new { }, cancellationToken);
    public Task<McpToolResult> PlayerStatsAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_player_stats", new { }, cancellationToken);
    public Task<McpToolResult> ChunkStatusAsync(int x, int y, int z, CancellationToken cancellationToken = default) => CallAsync("mcc_chunk_status", new { x, y, z }, cancellationToken);
    public Task<McpToolResult> BlockTypesListAsync(string? filter = null, int maxCount = 500, CancellationToken cancellationToken = default) => CallAsync("mcc_block_types_list", new { filter, maxCount }, cancellationToken);
    public Task<McpToolResult> WorldBlockAtAsync(int x, int y, int z, CancellationToken cancellationToken = default) => CallAsync("mcc_world_block_at", new { x, y, z }, cancellationToken);
    public Task<McpToolResult> BlockScanAsync(int radius = 3, int maxCount = 200, string? materialFilter = null, CancellationToken cancellationToken = default) => CallAsync("mcc_block_scan", new { radius, maxCount, materialFilter }, cancellationToken);
    public Task<McpToolResult> ChatHistoryAsync(int maxCount = 50, bool includeJson = false, CancellationToken cancellationToken = default) => CallAsync("mcc_chat_history", new { maxCount, includeJson }, cancellationToken);
    public Task<McpToolResult> RecentEventsAsync(long afterId = 0, int maxCount = 50, string? typeFilter = null, CancellationToken cancellationToken = default) => CallAsync("mcc_recent_events", new { afterId, maxCount, typeFilter }, cancellationToken);
    public Task<McpToolResult> SendChatAsync(string text, CancellationToken cancellationToken = default) => CallAsync("mcc_send_chat", new { text }, cancellationToken);
    public Task<McpToolResult> RunInternalCommandAsync(string command, CancellationToken cancellationToken = default) => CallAsync("mcc_run_internal_command", new { command }, cancellationToken);
    public Task<McpToolResult> SelectItemAsync(string itemType, bool preferLowestSlot = true, CancellationToken cancellationToken = default) => CallAsync("mcc_select_item", new { itemType, preferLowestSlot }, cancellationToken);
    public Task<McpToolResult> PlayerStateAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_player_state", new { }, cancellationToken);
    public Task<McpToolResult> PlaceBlockAsync(int x, int y, int z, string face = "Up", string hand = "MainHand", bool lookAtBlock = false, CancellationToken cancellationToken = default) => CallAsync("mcc_place_block", new { x, y, z, face, hand, lookAtBlock }, cancellationToken);
    public Task<McpToolResult> QuitClientAsync(CancellationToken cancellationToken = default) => CallAsync("mcc_quit_client", new { }, cancellationToken);

    public bool HasTool(string name) => Tools.ContainsKey(name);

    private async Task<McpToolResult> CallAsync(string name, object arguments, CancellationToken cancellationToken)
    {
        if (!HasTool(name))
        {
            throw new McpException(McpFailureKind.ToolNotFound, $"未发现 MCC 工具：{name}。" );
        }

        var result = await _invoker.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
        if (McpToolResultInspector.ClassifyFailure(result) is { } failureKind)
        {
            throw new McpException(
                failureKind,
                $"MCC 工具 {name} 返回失败：{result.ToDiagnosticText()}");
        }

        return result;
    }
}
