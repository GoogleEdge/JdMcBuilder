using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Core.Safety;
using JdMcBuilder.Execution;
using JdMcBuilder.Mcp;

namespace JdMcBuilder.App;

public partial class MainWindow : Window
{
    private McpClient? _mcp;
    private MccToolClient? _mcc;
    private BlueprintDocument? _blueprint;
    private IReadOnlyList<BuildBatch>? _batches;
    private BuildExecutor? _executor;
    private CancellationTokenSource? _buildCancellation;
    private bool _isBuilding;
    private bool _isProbing;
    private int _connectionGeneration;
    private Task? _buildTask;
    private BackendProbeReport? _backendProbeReport;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing)
        {
            FooterStatus.Text = "施工或能力探针运行期间不能重连";
            return;
        }

        var generation = Interlocked.Increment(ref _connectionGeneration);
        try
        {
            if (!Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https"))
            {
                throw new UriFormatException("MCP endpoint 必须是 http 或 https URL。" );
            }

            var options = new McpConnectionOptions { Endpoint = endpoint };
            var transport = new HttpMcpTransport(options);
            var client = new McpClient(transport, options);
            await client.ConnectAsync();
            if (generation != Volatile.Read(ref _connectionGeneration))
            {
                await client.DisposeAsync();
                return;
            }

            var previousMcp = _mcp;
            _mcp = client;
            _mcc = new MccToolClient(client);
            _backendProbeReport = null;
            if (previousMcp is not null)
            {
                await previousMcp.DisposeAsync();
            }
            BackendCapabilityStatus.Text = "WorldEdit：未探测\n/fill：未探测\n逐块放置：未探测";
            ToolsList.ItemsSource = client.Tools.Values.OrderBy(tool => tool.Name).ToArray();
            var report = MccCapabilityDetector.Detect(client.Tools);
            ConnectionStatus.Text = $"已连接：发现 {client.Tools.Count} 个工具";
            AppendLog("MCP initialize + tools/list 完成。WorldEdit 与 /fill 仍需在测试世界验证权限。\n" + string.Join("\n", report.Capabilities.Select(item => $"{item.Capability}: {item.Status} — {item.Reason}")));
            await RunPreflightAsync();
            FooterStatus.Text = "已连接；尚未执行任何写入";
        }
        catch (Exception exception)
        {
            ConnectionStatus.Text = "连接失败";
            AppendLog($"连接失败：{exception.Message}");
            FooterStatus.Text = "连接失败；请检查 MCC 是否已进入游戏和 endpoint/token";
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing)
        {
            FooterStatus.Text = "施工或能力探针运行期间不能更换蓝图";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Minecraft blueprint|*.json;*.jsonl|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var blueprint = await BlueprintParser.LoadAsync(dialog.FileName);
            _blueprint = blueprint;
            // “全世界可用”不等于无限制写入；导入文件声明的 bounds 是首版的空间护栏。
            var safetyOptions = new BuildSafetyOptions { AllowedRegion = blueprint.Bounds };
            var validation = BlueprintValidator.Validate(blueprint, safetyOptions);
            BlueprintPath.Text = dialog.FileName;
            BlueprintSummary.Text = $"格式：{blueprint.Format}\n阶段：{validation.Statistics.PhaseCount} · 操作：{validation.Statistics.OperationCount}\n方块：{validation.Statistics.TotalBlocks:N0} · 错误：{validation.Issues.Count(issue => issue.Severity == ValidationSeverity.Error)} · 警告：{validation.Issues.Count(issue => issue.Severity == ValidationSeverity.Warning)}";
            AppendLog(string.Join("\n", validation.Issues.Select(issue => $"[{issue.Severity}] {issue.Code}: {issue.Message}")));
            if (!validation.IsValid)
            {
                _blueprint = null;
                _batches = null;
                FooterStatus.Text = "蓝图校验失败，未准备施工";
                return;
            }

            var plannedBatches = new BatchPlanner(new BatchPlannerOptions(
                MaxBlocksPerBatch: safetyOptions.MaxBlocksPerOperation,
                MaxPayloadBytes: safetyOptions.MaxPayloadBytes)).Plan(blueprint);
            _batches = plannedBatches;
            BlueprintSummary.Text = $"格式：{blueprint.Format}\n阶段：{validation.Statistics.PhaseCount} · 操作：{validation.Statistics.OperationCount}\n方块：{validation.Statistics.TotalBlocks:N0} · 预计批次：{plannedBatches.Count} · 错误：0 · 警告：{validation.Issues.Count(issue => issue.Severity == ValidationSeverity.Warning)}";
            FooterStatus.Text = "蓝图已通过离线校验；请先 Dry Run";
        }
        catch (Exception exception)
        {
            _blueprint = null;
            _batches = null;
            BlueprintPath.Text = "尚未导入蓝图";
            BlueprintSummary.Text = "导入失败。";
            AppendLog($"导入失败：{exception.Message}");
            FooterStatus.Text = "蓝图导入失败";
        }
    }

    private async void DryRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing)
        {
            FooterStatus.Text = "已有施工或能力探针任务正在运行";
            return;
        }

        if (_batches is not { } batches || _blueprint is null)
        {
            FooterStatus.Text = "请先导入蓝图";
            return;
        }

        try
        {
            var journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JdMcBuilder", "dry-run.json");
            var executor = new BuildExecutor([], new BackendSelector(), new BuildJournal(journalPath), new BuildExecutionOptions(DryRun: true));
            executor.Progress += (_, progress) => _ = Dispatcher.InvokeAsync(() => AppendLog($"Dry Run {progress.BatchId}: {progress.Message} ({progress.CompletedBlocks}/{progress.TotalBlocks})"));
            var hash = await ComputeBlueprintHashAsync();
            await executor.ExecuteAsync(hash, batches);
            FooterStatus.Text = "Dry Run 完成；没有发送世界写入调用";
        }
        catch (Exception exception)
        {
            AppendLog($"Dry Run 失败：{exception.Message}");
            FooterStatus.Text = "Dry Run 失败";
        }
    }

    private async void StartBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding)
        {
            FooterStatus.Text = "已有施工任务正在运行";
            return;
        }

        if (_batches is not { } batches
            || _blueprint is null
            || _mcc is not { } mcc
            || _mcp is not { } mcp)
        {
            FooterStatus.Text = "请先连接 MCP 并导入有效蓝图";
            return;
        }

        var worldEditStatus = _backendProbeReport?.Find("worldedit") is { } worldEditProbe
            ? worldEditProbe.Status
            : BackendStatus.Unverified;
        var nativeFillStatus = _backendProbeReport?.Find("native-fill") is { } nativeFillProbe
            ? nativeFillProbe.Status
            : BackendStatus.Unverified;
        var placeBlockStatus = _backendProbeReport?.Find("place-block") is { } placeBlockProbe
            ? placeBlockProbe.Status
            : BackendStatus.Unverified;
        var hasFill = batches.Any(batch => batch is FillBatch);
        var hasExplicitBlocks = batches.Any(batch => batch is ExplicitBlocksBatch);
        var fillVerified = _backendProbeReport is { } probeReport
            && (probeReport.Find("worldedit")?.IsVerified == true
                || probeReport.Find("native-fill")?.IsVerified == true);
        var targetFingerprint = _backendProbeReport?.TargetFingerprint;
        if ((hasFill && !fillVerified)
            || (hasExplicitBlocks
                && _backendProbeReport?.Find("place-block")?.IsVerified != true))
        {
            AppendLog("施工已阻止：当前没有覆盖全部批次的已验证后端。仅发现工具名称不能证明写入权限；请先在测试世界完成各后端的独立能力验证。\n" +
                $"WorldEdit：{worldEditStatus}；/fill：{nativeFillStatus}；逐块放置：{placeBlockStatus}");
            FooterStatus.Text = "没有覆盖全部批次的已验证后端；施工已阻止";
            return;
        }

        var confirmation = MessageBox.Show("将向 Leaf 1.21.11 世界发送写入操作。请确认当前是测试/备份世界，并已完成 Dry Run。继续？", "确认施工", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var sampleVerifier = new Func<BlockPosition, string, CancellationToken, Task>(async (position, expectedBlock, cancellationToken) =>
        {
            var result = await mcc.WorldBlockAtAsync(position.X, position.Y, position.Z, cancellationToken);
            if (!result.TryGetBlockSample(out var actualBlock, out var returnedPosition))
            {
                throw new BackendException(
                    $"施工后方块验证无法解析：{position}，期望 {expectedBlock}，mcc_world_block_at 未返回可识别的文本方块 ID。",
                    uncertain: true);
            }

            if (returnedPosition is { } actualPosition && actualPosition != position)
            {
                throw new BackendException(
                    $"施工后方块验证返回坐标不匹配：请求 {position}，实际返回 {actualPosition}。",
                    uncertain: true);
            }

            if (!string.Equals(actualBlock, expectedBlock, StringComparison.OrdinalIgnoreCase))
            {
                throw new BackendException(
                    $"施工后方块验证不匹配：{position}，期望 {expectedBlock}，实际 {actualBlock}。",
                    uncertain: true);
            }
        });
        var worldEditVerification = _backendProbeReport?.Find("worldedit")?.Verification;
        var nativeFillVerification = _backendProbeReport?.Find("native-fill")?.Verification;
        var placeBlockVerification = _backendProbeReport?.Find("place-block")?.Verification;
        var worldEdit = new WorldEditCommandBackend(
            mcc,
            sampleVerifier,
            worldEditStatus,
            verification: worldEditVerification);
        var nativeFill = new NativeFillBackend(
            mcc,
            status: nativeFillStatus,
            verification: nativeFillVerification);
        var placeBlock = new PlaceBlockBackend(
            mcc,
            sampleVerifier,
            placeBlockStatus,
            verification: placeBlockVerification);
        var journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JdMcBuilder", "build-journal.json");
        var executor = new BuildExecutor([worldEdit, nativeFill, placeBlock], new BackendSelector(), new BuildJournal(journalPath), new BuildExecutionOptions(
            DryRun: false,
            AllowUnverifiedBackend: false,
            TargetFingerprint: targetFingerprint));
        executor.Progress += (_, progress) => _ = Dispatcher.InvokeAsync(() => AppendLog($"{progress.BatchId}: {progress.Message}"));
        _executor = executor;
        _buildCancellation?.Dispose();
        using var buildCancellation = new CancellationTokenSource();
        _buildCancellation = buildCancellation;
        _isBuilding = true;
        var buildTask = Task.CompletedTask;
        try
        {
            var blueprintPath = BlueprintPath.Text;
            if (string.IsNullOrWhiteSpace(blueprintPath) || !File.Exists(blueprintPath))
            {
                throw new InvalidOperationException("尚未导入可读取的蓝图文件。 ");
            }

            var hash = await BlueprintHash.ComputeFileSha256Async(blueprintPath, buildCancellation.Token);
            buildTask = executor.ExecuteAsync(hash, batches, buildCancellation.Token);
            _buildTask = buildTask;
            await buildTask;
            FooterStatus.Text = "施工完成";
            MessageBox.Show("所有批次已完成。请按日志中的采样结果检查世界。", "施工完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("施工已取消。");
            FooterStatus.Text = "施工已取消";
        }
        catch (Exception exception)
        {
            AppendLog($"施工停止：{exception.Message}");
            FooterStatus.Text = "施工已停止；请查看日志和 journal";
        }
        finally
        {
            _isBuilding = false;
            if (ReferenceEquals(_buildCancellation, buildCancellation))
            {
                _buildCancellation = null;
            }

            if (ReferenceEquals(_buildTask, buildTask))
            {
                _buildTask = null;
            }
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing)
        {
            FooterStatus.Text = "施工期间不能执行能力探针";
            return;
        }

        if (_mcc is not { } mcc)
        {
            FooterStatus.Text = "请先连接 MCP";
            return;
        }

        if (!mcc.HasTool("mcc_session_status")
            || !mcc.HasTool("mcc_world_state"))
        {
            FooterStatus.Text = "缺少会话/世界预检工具；能力探针已阻止";
            return;
        }

        if (!TryParseRange(WorldEditProbeBox.Text, out var worldEditRange)
            || !TryParseRange(NativeFillProbeBox.Text, out var nativeFillRange)
            || !TryParsePosition(PlaceProbeBox.Text, out var placePosition))
        {
            FooterStatus.Text = "探针坐标格式无效；请使用 x,y,z;x,y,z 或 x,y,z";
            return;
        }

        var probeBlock = ProbeBlockBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(probeBlock))
        {
            FooterStatus.Text = "探针方块不能为空。";
            return;
        }

        try
        {
            var nativePlan = NativeFillVerificationPlan.Create(
                nativeFillRange,
                probeBlock);
            AppendLog(
                $"/fill 探针计划：原始输入「{NativeFillProbeBox.Text}」；标准化范围 {nativePlan.Range}；"
                + $"命令 {nativePlan.Command}；采样点 [{string.Join(", ", nativePlan.SamplePositions)}]");
        }
        catch (Exception exception)
        {
            FooterStatus.Text = $"/fill 探针计划无效：{exception.Message}";
            return;
        }

        var confirmation = MessageBox.Show(
            "能力验证会在当前测试世界的三个指定位置写入探针方块。请确认坐标安全、世界可恢复，并且你明确授权此次写入。继续？",
            "确认能力探针写入",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var generation = Volatile.Read(ref _connectionGeneration);
        _isProbing = true;
        _backendProbeReport = null;
        try
        {
            var probe = new CommandCapabilityProbe(mcc);
            var testBlock = probeBlock;
            var report = await probe.ProbeApprovedAsync(
                new BackendProbeRequest(
                    worldEditRange,
                    nativeFillRange,
                    placePosition,
                    testBlock));
            if (generation != Volatile.Read(ref _connectionGeneration)
                || !ReferenceEquals(_mcc, mcc))
            {
                AppendLog("能力探针结果来自已替换的 MCP 连接，已丢弃。 ");
                return;
            }

            _backendProbeReport = report;
            BackendCapabilityStatus.Text =
                $"目标指纹：{report.TargetFingerprint}\n"
                + string.Join(
                    "\n",
                    report.Results.Select(item =>
                        $"{DisplayBackendName(item.BackendId)}：{item.Status}"
                        + (item.Verification is { } verification
                            ? $"（至 {verification.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC）"
                            : string.Empty)));
            foreach (var result in report.Results)
            {
                AppendLog($"能力探针 {result.BackendId}：{result.Status} — {result.Reason}");
            }

            FooterStatus.Text = "能力探针完成；只有带当前目标证明的后端可用于施工";
        }
        catch (Exception exception)
        {
            _backendProbeReport = null;
            BackendCapabilityStatus.Text = "WorldEdit：未探测\n/fill：未探测\n逐块放置：未探测";
            AppendLog($"能力探针失败：{exception.Message}");
            FooterStatus.Text = "能力探针失败；施工仍保持阻止";
        }
        finally
        {
            _isProbing = false;
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _executor?.Pause();
        FooterStatus.Text = "施工已暂停";
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        _executor?.Resume();
        FooterStatus.Text = "施工继续";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _buildCancellation?.Cancel();
        FooterStatus.Text = "正在取消施工";
    }

    private static bool TryParsePosition(string text, out BlockPosition position)
    {
        var parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
        {
            position = new BlockPosition(x, y, z);
            return true;
        }

        position = default;
        return false;
    }

    private static bool TryParseRange(string text, out BlockRange range)
    {
        var parts = (text ?? string.Empty).Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && TryParsePosition(parts[0], out var first)
            && TryParsePosition(parts[1], out var second))
        {
            range = BlockRange.FromUnordered(first, second);
            return true;
        }

        range = default;
        return false;
    }

    private static string DisplayBackendName(string backendId) => backendId switch
    {
        "worldedit" => "WorldEdit",
        "native-fill" => "/fill",
        "place-block" => "逐块放置",
        _ => backendId
    };

    private async Task RunPreflightAsync()
    {
        if (_mcc is not { } mcc)
        {
            return;
        }

        var checks = new List<(string Name, Func<Task<McpToolResult>> Action)>
        {
            ("会话", () => mcc.SessionStatusAsync()),
            ("世界", () => mcc.WorldStateAsync()),
            ("服务器", () => mcc.ServerInfoAsync()),
            ("玩家", () => mcc.PlayerStatsAsync())
        };
        foreach (var check in checks)
        {
            try
            {
                await check.Action();
                AppendLog($"预检通过：{check.Name}");
            }
            catch (Exception exception)
            {
                AppendLog($"预检失败：{check.Name}：{exception.Message}");
            }
        }
    }

    private async Task<string> ComputeBlueprintHashAsync()
    {
        if (string.IsNullOrWhiteSpace(BlueprintPath.Text) || !File.Exists(BlueprintPath.Text))
        {
            throw new InvalidOperationException("尚未导入可读取的蓝图文件。" );
        }

        return await BlueprintHash.ComputeFileSha256Async(BlueprintPath.Text);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _buildCancellation?.Cancel();
        var buildTask = _buildTask;
        if (buildTask is not null)
        {
            try
            {
                await buildTask;
            }
            catch (Exception exception)
            {
                AppendLog($"关闭时施工任务结束：{exception.Message}");
            }
        }

        _buildCancellation?.Dispose();
        _buildCancellation = null;
        var mcp = _mcp;
        if (mcp is not null)
        {
            await mcp.DisposeAsync();
            _mcp = null;
            _mcc = null;
        }

        base.OnClosed(e);
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }
}
