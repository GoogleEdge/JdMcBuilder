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
    private bool _isJournalAction;
    private string? _importedBlueprintHash;
    private int _workflowBusy;
    private int _connectionGeneration;
    private Task? _buildTask;
    private BackendProbeReport? _backendProbeReport;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing || Volatile.Read(ref _workflowBusy) != 0)
        {
            FooterStatus.Text = "施工、能力探针或其他操作运行期间不能重连";
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
            BackendCapabilityStatus.Text = "WorldEdit：未探测\n/fill：未探测\n/setblock：未探测";
            ToolsList.ItemsSource = client.Tools.Values.OrderBy(tool => tool.Name).ToArray();
            var report = MccCapabilityDetector.Detect(client.Tools);
            ConnectionStatus.Text = $"已连接：发现 {client.Tools.Count} 个工具";
            AppendLog("MCP initialize + tools/list 完成。WorldEdit、/fill 与 /setblock 仍需在测试世界分别验证权限和独立采样。\n" + string.Join("\n", report.Capabilities.Select(item => $"{item.Capability}: {item.Status} — {item.Reason}")));
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
        if (_isBuilding || _isProbing || _isJournalAction || Volatile.Read(ref _workflowBusy) != 0)
        {
            FooterStatus.Text = "施工、能力探针或 journal 操作运行期间不能更换蓝图";
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
            var importedHash = await BlueprintHash.ComputeFileSha256Async(dialog.FileName);
            _blueprint = blueprint;
            _importedBlueprintHash = importedHash;
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
                _importedBlueprintHash = null;
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
            _importedBlueprintHash = null;
            BlueprintPath.Text = "尚未导入蓝图";
            BlueprintSummary.Text = "导入失败。";
            AppendLog($"导入失败：{exception.Message}");
            FooterStatus.Text = "蓝图导入失败";
        }
    }

    private async void DryRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing || _isJournalAction)
        {
            FooterStatus.Text = "已有施工、能力探针或 journal 任务正在运行";
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
        if (Interlocked.CompareExchange(ref _workflowBusy, 1, 0) != 0)
        {
            FooterStatus.Text = "已有施工、能力探针或 journal 任务正在运行";
            return;
        }

        try
        {
            await StartBuildCoreAsync();
        }
        finally
        {
            Volatile.Write(ref _workflowBusy, 0);
        }
    }

    private async Task StartBuildCoreAsync()
    {
        if (_isBuilding || _isProbing || _isJournalAction)
        {
            FooterStatus.Text = "已有施工、能力探针或 journal 任务正在运行";
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
        var setBlockStatus = _backendProbeReport?.Find("native-setblock") is { } setBlockProbe
            ? setBlockProbe.Status
            : BackendStatus.Unverified;
        var hasFill = batches.Any(batch => batch is FillBatch);
        var hasExplicitBlocks = batches.Any(batch => batch is ExplicitBlocksBatch);
        var probeTargetFingerprint = _backendProbeReport?.TargetFingerprint;
        if (string.IsNullOrWhiteSpace(probeTargetFingerprint))
        {
            AppendLog("施工已阻止：尚未完成带目标指纹的能力探针。 ");
            FooterStatus.Text = "缺少目标指纹；施工已阻止";
            return;
        }

        string currentTargetFingerprint;
        try
        {
            currentTargetFingerprint = await ReadCurrentTargetFingerprintAsync(mcc);
        }
        catch (Exception exception)
        {
            AppendLog($"施工已阻止：无法重新确认当前目标世界指纹：{exception.Message}");
            FooterStatus.Text = "无法确认当前目标世界；施工已阻止";
            return;
        }

        if (!string.Equals(
                probeTargetFingerprint,
                currentTargetFingerprint,
                StringComparison.Ordinal))
        {
            AppendLog(
                $"施工已阻止：能力探针指纹与当前目标世界不一致。探针={probeTargetFingerprint}；当前={currentTargetFingerprint}。请重新执行能力探针。 ");
            FooterStatus.Text = "目标世界已变化；请重新探针";
            return;
        }

        var targetFingerprint = currentTargetFingerprint;
        var worldEditVerified = _backendProbeReport?.Find("worldedit")?.IsVerifiedFor(targetFingerprint) == true;
        var nativeFillVerified = _backendProbeReport?.Find("native-fill")?.IsVerifiedFor(targetFingerprint) == true;
        var setBlockVerified = _backendProbeReport?.Find("native-setblock")?.IsVerifiedFor(targetFingerprint) == true;
        var fillVerified = worldEditVerified || nativeFillVerified;
        if ((hasFill && !fillVerified)
            || (hasExplicitBlocks && !setBlockVerified))
        {
            AppendLog("施工已阻止：当前没有覆盖全部批次的已验证后端。仅发现工具名称不能证明写入权限；请先在测试世界完成各后端的独立能力验证。\n" +
                $"WorldEdit：{worldEditStatus}；/fill：{nativeFillStatus}；/setblock：{setBlockStatus}");
            FooterStatus.Text = "没有覆盖全部批次的已验证后端；施工已阻止";
            return;
        }

        var confirmation = MessageBox.Show("将向 Leaf 1.21.11 世界发送写入操作。请确认当前是测试/备份世界，并已完成 Dry Run。继续？", "确认施工", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var readback = new BlockReadbackVerifier(mcc);
        var sampleVerifier = new Func<BlockPosition, string, CancellationToken, Task>(
            (position, expectedBlock, cancellationToken) =>
                readback.VerifyOnceAsync(position, expectedBlock, cancellationToken));
        var nativeSetBlockVerifier = new NativeSetBlockVerifier(mcc);
        var worldEditVerification = _backendProbeReport?.Find("worldedit")?.Verification;
        var nativeFillVerification = _backendProbeReport?.Find("native-fill")?.Verification;
        var setBlockVerification = _backendProbeReport?.Find("native-setblock")?.Verification;
        var worldEdit = new WorldEditCommandBackend(
            mcc,
            sampleVerifier,
            worldEditStatus,
            verification: worldEditVerification);
        var nativeFill = new NativeFillBackend(
            mcc,
            status: nativeFillStatus,
            verification: nativeFillVerification);
        var setBlock = new NativeSetBlockBackend(
            mcc,
            nativeSetBlockVerifier,
            targetFingerprint!,
            setBlockStatus,
            verification: setBlockVerification);
        var journalPath = GetBuildJournalPath();
        var executor = new BuildExecutor([worldEdit, nativeFill, setBlock], new BackendSelector(), new BuildJournal(journalPath), new BuildExecutionOptions(
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
            if (!string.Equals(hash, _importedBlueprintHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("导入后的蓝图文件内容已变化；请重新导入并重新规划批次。 ");
            }

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

            Volatile.Write(ref _workflowBusy, 0);
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing || Volatile.Read(ref _workflowBusy) != 0)
        {
            FooterStatus.Text = "施工或其他操作运行期间不能执行能力探针";
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
            || !TryParsePosition(SetBlockProbeBox.Text, out var setBlockPosition))
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

        if (Interlocked.CompareExchange(ref _workflowBusy, 1, 0) != 0)
        {
            FooterStatus.Text = "已有其他操作正在准备；能力探针已阻止";
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
                    setBlockPosition,
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
            BackendCapabilityStatus.Text = "WorldEdit：未探测\n/fill：未探测\n/setblock：未探测";
            AppendLog($"能力探针失败：{exception.Message}");
            FooterStatus.Text = "能力探针失败；施工仍保持阻止";
        }
        finally
        {
            _isProbing = false;
            Volatile.Write(ref _workflowBusy, 0);
        }
    }

    private async void ArchiveJournalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isProbing || _isJournalAction || Interlocked.CompareExchange(ref _workflowBusy, 1, 0) != 0)
        {
            FooterStatus.Text = "施工、能力探针或其他 journal 操作运行期间不能归档";
            return;
        }

        if (_blueprint is null || _batches is null || string.IsNullOrWhiteSpace(BlueprintPath.Text) || !File.Exists(BlueprintPath.Text))
        {
            FooterStatus.Text = "请先导入可读取的蓝图，再处理 journal";
            Volatile.Write(ref _workflowBusy, 0);
            return;
        }

        _isJournalAction = true;
        try
        {
            var currentHash = await ComputeBlueprintHashAsync();
            if (!string.Equals(currentHash, _importedBlueprintHash, StringComparison.Ordinal))
            {
                AppendLog("journal 归档已阻止：导入后的蓝图文件内容已变化，请重新导入。 ");
                FooterStatus.Text = "蓝图文件已变化；请重新导入后再处理 journal";
                return;
            }

            var journal = new BuildJournal(GetBuildJournalPath());
            var snapshot = await journal.ReadSnapshotAsync();
            if (snapshot is null)
            {
                AppendLog("没有活动 build journal，未执行归档。 ");
                FooterStatus.Text = "没有活动 build journal；未执行归档";
                return;
            }

            var state = snapshot.State;
            var warning =
                $"活动 journal 属于旧蓝图。\n\n"
                + $"Session：{state.SessionId}\n"
                + $"旧蓝图 hash：{state.BlueprintHash}\n"
                + $"当前蓝图 hash：{currentHash}\n"
                + $"已完成批次：{state.CompletedBatches.Count}\n"
                + $"不确定批次：{state.UncertainBatches.Count}\n\n"
                + "归档只会移动本地 journal，不会回滚 Minecraft 世界，也不会自动重放或验证旧批次。"
                + (state.UncertainBatches.Count > 0
                    ? "存在不确定批次，必须先人工/新鲜采样确认；当前操作不会归档它。"
                    : "确认后需要重新执行 Dry Run，并在必要时重新进行能力探针。")
                + "\n\n是否继续？";
            if (MessageBox.Show(warning, "归档旧 journal 并开始新 session", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                FooterStatus.Text = "已取消 journal 归档";
                return;
            }

            var result = await journal.ArchiveStaleAndResetAsync(snapshot, currentHash);
            AppendLog(result.Message);
            FooterStatus.Text = result.Status switch
            {
                JournalArchiveStatus.Archived => "旧 journal 已归档；请重新 Dry Run 后再开始施工",
                JournalArchiveStatus.BlockedByUncertain => "journal 含不确定批次；归档已阻止",
                JournalArchiveStatus.ChangedSinceSnapshot => "journal 已变化；请重新读取后再确认",
                JournalArchiveStatus.NotStale => "journal 已对应当前蓝图；未归档",
                _ => "没有活动 journal；未归档"
            };
        }
        catch (OperationCanceledException)
        {
            FooterStatus.Text = "journal 归档已取消";
        }
        catch (Exception exception)
        {
            AppendLog($"journal 归档失败：{exception.Message}");
            FooterStatus.Text = "journal 归档失败；原文件未被应用主动删除";
        }
        finally
        {
            _isJournalAction = false;
            Volatile.Write(ref _workflowBusy, 0);
        }
    }

    private static string GetBuildJournalPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JdMcBuilder", "build-journal.json");

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
        "native-setblock" => "/setblock",
        _ => backendId
    };

    private async Task<string> ReadCurrentTargetFingerprintAsync(MccToolClient mcc)
    {
        var session = await mcc.SessionStatusAsync().ConfigureAwait(true);
        var world = await mcc.WorldStateAsync().ConfigureAwait(true);
        McpToolResult? server = null;
        if (mcc.HasTool("mcc_server_info"))
        {
            server = await mcc.ServerInfoAsync().ConfigureAwait(true);
        }

        return TargetFingerprintBuilder.Create(mcc, session, world, server);
    }

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
