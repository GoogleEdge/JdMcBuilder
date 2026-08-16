using JdMcBuilder.Core.Safety;

namespace JdMcBuilder.Core.Blueprint;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public sealed record BlueprintStatistics(
    long TotalBlocks,
    long ExplicitBlocks,
    long FillBlocks,
    int PhaseCount,
    int OperationCount,
    int FillOperationCount,
    int BlocksOperationCount,
    int DistinctBlockTypes,
    long EstimatedBackendCalls,
    IReadOnlyDictionary<string, long> BlocksByType,
    IReadOnlyDictionary<string, long> BlocksByPhase);

public sealed record BlueprintValidationResult(
    IReadOnlyList<ValidationIssue> Issues,
    BlueprintStatistics Statistics)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);
}

public static class BlueprintValidator
{
    private static readonly System.Text.RegularExpressions.Regex BlockIdRegex =
        new(@"\Aminecraft:[a-z0-9_/.-]+\z", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static BlueprintValidationResult Validate(
        BlueprintDocument document,
        BuildSafetyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new BuildSafetyOptions();
        var issues = new List<ValidationIssue>();
        var byType = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byPhase = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<BlockPosition>();
        var explicitPlacements = new List<(BlockPosition Position, string Path)>();
        var fillRanges = new List<(BlockRange Range, string Path)>();
        var phaseIds = new HashSet<string>(StringComparer.Ordinal);
        long totalBlocks = 0;
        long explicitBlocks = 0;
        long fillBlocks = 0;
        var fillCount = 0;
        var blocksCount = 0;
        long estimatedCalls = 0;

        if (options.MaxBlocksPerOperation <= 0)
        {
            issues.Add(new(ValidationSeverity.Error, "options.max_blocks_invalid", "MaxBlocksPerOperation 必须大于 0。", null));
        }

        if (options.MaxPayloadBytes <= 0)
        {
            issues.Add(new(ValidationSeverity.Error, "options.max_payload_invalid", "MaxPayloadBytes 必须大于 0。", null));
        }

        if (options.MaxCoordinateAbsoluteValue < 0)
        {
            issues.Add(new(ValidationSeverity.Error, "options.coordinate_limit_invalid", "MaxCoordinateAbsoluteValue 不能为负数。", null));
        }

        if (options.LargePhaseThreshold < 0)
        {
            issues.Add(new(ValidationSeverity.Error, "options.large_phase_threshold_invalid", "LargePhaseThreshold 不能为负数。", null));
        }

        if (!string.Equals(document.Format, "mc-blueprint/v1", StringComparison.Ordinal))
        {
            issues.Add(new(ValidationSeverity.Error, "format.unsupported", $"不支持的蓝图格式：{document.Format}", "format"));
        }

        ValidateRange(document.Bounds, "bounds", options, issues);
        if (options.AllowedRegion is { } allowed && !allowed.Contains(document.Bounds))
        {
            issues.Add(new(ValidationSeverity.Error, "bounds.outside_allowed_region", "蓝图 bounds 超出允许施工区域。", "bounds"));
        }

        if (document.Phases.Count == 0)
        {
            issues.Add(new(ValidationSeverity.Error, "phases.empty", "蓝图至少需要一个施工阶段。", "phases"));
        }

        foreach (var phase in document.Phases.OrderBy(item => item.Order).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(phase.Id))
            {
                issues.Add(new(ValidationSeverity.Error, "phase.id.empty", "阶段 ID 不能为空。", "phases"));
            }
            else if (!phaseIds.Add(phase.Id))
            {
                issues.Add(new(ValidationSeverity.Error, "phase.id.duplicate", $"阶段 ID 重复：{phase.Id}。", "phases"));
            }

            long phaseBlocks = 0;
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in phase.Operations)
            {
                var path = $"phases[{phase.Id}].operations[{operation.Id}]";
                if (string.IsNullOrWhiteSpace(operation.Id))
                {
                    ValidateOperationId(operation.Id, path, issues);
                }
                else if (!operationIds.Add(operation.Id))
                {
                    issues.Add(new(ValidationSeverity.Error, "operation.id.duplicate", $"阶段 {phase.Id} 中操作 ID 重复：{operation.Id}。", path));
                }

                switch (operation)
                {
                    case FillOperation fill:
                        fillCount++;
                        estimatedCalls++;
                        ValidateOperationId(fill.Id, path, issues);
                        ValidateRange(fill.Range, $"{path}.range", options, issues);
                        if (!document.Bounds.Contains(fill.Range))
                        {
                            issues.Add(new(ValidationSeverity.Error, "fill.outside_bounds", $"填充范围 {fill.Range} 超出蓝图 bounds。", path));
                        }

                        ValidateUnsupportedStates(fill.States, path, issues);
                        if (fill.Range.IsValid && fill.Range.TryGetVolume(out var fillVolume))
                        {
                            foreach (var existing in fillRanges)
                            {
                                if (RangesOverlap(existing.Range, fill.Range))
                                {
                                    issues.Add(new(ValidationSeverity.Error, "operations.overlap", $"填充范围与 {existing.Path} 重叠；首版不允许隐式覆盖。", path));
                                }
                            }

                            foreach (var explicitPlacement in explicitPlacements)
                            {
                                if (fill.Range.Contains(explicitPlacement.Position))
                                {
                                    issues.Add(new(ValidationSeverity.Error, "operations.overlap", $"填充范围覆盖显式方块 {explicitPlacement.Position}（{explicitPlacement.Path}）；首版不允许隐式覆盖。", path));
                                }
                            }

                            fillRanges.Add((fill.Range, path));
                            ValidateBlock(fill.Block, $"{path}.block", issues);
                            AddBlockCount(fill.Block, fillVolume, byType, path, issues);
                            fillBlocks = AddChecked(fillBlocks, fillVolume, path, issues);
                            phaseBlocks = AddChecked(phaseBlocks, fillVolume, path, issues);
                        }
                        else
                        {
                            ValidateBlock(fill.Block, $"{path}.block", issues);
                        }
                        break;
                    case BlocksOperation blocks:
                        blocksCount++;
                        ValidateOperationId(blocks.Id, path, issues);
                        estimatedCalls = AddChecked(estimatedCalls, Math.Max(1, blocks.Blocks.Count), path, issues);
                        foreach (var placement in blocks.Blocks)
                        {
                            explicitBlocks = AddChecked(explicitBlocks, 1, path, issues);
                            phaseBlocks = AddChecked(phaseBlocks, 1, path, issues);
                            ValidatePosition(placement.Position, $"{path}.blocks", options, issues);
                            ValidateBlock(placement.Block, $"{path}.blocks.block", issues);
                            ValidateUnsupportedStates(placement.States, $"{path}.blocks", issues);
                            if (!seen.Add(placement.Position))
                            {
                                issues.Add(new(ValidationSeverity.Error, "blocks.duplicate_position", $"坐标重复：{placement.Position}。", path));
                            }

                            foreach (var fillRange in fillRanges)
                            {
                                if (fillRange.Range.Contains(placement.Position))
                                {
                                    issues.Add(new(ValidationSeverity.Error, "operations.overlap", $"显式方块 {placement.Position} 位于填充范围（{fillRange.Path}）内；首版不允许隐式覆盖。", path));
                                }
                            }

                            explicitPlacements.Add((placement.Position, path));

                            if (!document.Bounds.Contains(placement.Position))
                            {
                                issues.Add(new(ValidationSeverity.Error, "blocks.outside_bounds", $"坐标 {placement.Position} 超出蓝图 bounds。", path));
                            }

                            AddBlockCount(placement.Block, 1, byType, path, issues);
                        }

                        break;
                    default:
                        issues.Add(new(ValidationSeverity.Error, "operation.unknown", $"未知操作类型：{operation.Type}。", path));
                        break;
                }
            }

            byPhase[phase.Id] = phaseBlocks;
            totalBlocks = AddChecked(totalBlocks, phaseBlocks, $"phases[{phase.Id}]", issues);
            if (phaseBlocks > options.LargePhaseThreshold)
            {
                issues.Add(new(ValidationSeverity.Warning, "phase.large", $"阶段 {phase.Id} 包含 {phaseBlocks} 个方块，需要执行前确认。", $"phases[{phase.Id}]"));
            }
        }

        return new BlueprintValidationResult(
            issues,
            new BlueprintStatistics(
                totalBlocks,
                explicitBlocks,
                fillBlocks,
                document.Phases.Count,
                document.Phases.Sum(phase => phase.Operations.Count),
                fillCount,
                blocksCount,
                byType.Count,
                estimatedCalls,
                byType,
                byPhase));
    }

    private static void ValidateOperationId(string id, string path, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            issues.Add(new(ValidationSeverity.Error, "operation.id.empty", "操作 ID 不能为空。", path));
        }
        else if (id.Length > 128 || id.Any(character => character > 0x7f || char.IsControl(character)))
        {
            issues.Add(new(ValidationSeverity.Error, "operation.id.invalid", "操作 ID 只能包含不超过 128 个字符的 ASCII 文本，且不能包含控制字符。", path));
        }
    }

    private static void ValidateUnsupportedStates(
        IReadOnlyDictionary<string, string>? states,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (states is not null && states.Count > 0)
        {
            issues.Add(new(ValidationSeverity.Error, "states.unsupported", "当前后端尚未实现方块 states；请删除 states 或先实现状态安全转换。", path));
        }
    }

    private static void ValidateRange(BlockRange range, string path, BuildSafetyOptions options, ICollection<ValidationIssue> issues)
    {
        if (!range.IsValid)
        {
            issues.Add(new(ValidationSeverity.Error, "range.invalid", $"范围无效：{range}。", path));
            return;
        }

        ValidatePosition(range.Min, $"{path}.min", options, issues);
        ValidatePosition(range.Max, $"{path}.max", options, issues);
        if (!range.TryGetVolume(out var volume))
        {
            issues.Add(new(ValidationSeverity.Error, "range.volume_overflow", "范围体积超出安全统计范围。", path));
            return;
        }

        if (volume > options.MaxBlocksPerOperation)
        {
            issues.Add(new(ValidationSeverity.Warning, "range.will_split", $"范围包含 {volume} 个方块，将被拆分为多个批次。", path));
        }

        if (options.AllowedRegion is { } allowed && !allowed.Contains(range))
        {
            issues.Add(new(ValidationSeverity.Error, "range.outside_allowed_region", "操作范围超出允许施工区域。", path));
        }
    }

    private static void ValidatePosition(BlockPosition position, string path, BuildSafetyOptions options, ICollection<ValidationIssue> issues)
    {
        var limit = options.MaxCoordinateAbsoluteValue;
        if (Math.Abs((long)position.X) > limit || Math.Abs((long)position.Y) > limit || Math.Abs((long)position.Z) > limit)
        {
            issues.Add(new(ValidationSeverity.Error, "position.out_of_range", $"坐标超出安全上限：{position}。", path));
        }
    }

    private static void ValidateBlock(string? block, string path, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(block) || !BlockIdRegex.IsMatch(block))
        {
            issues.Add(new(ValidationSeverity.Error, "block.invalid_id", $"非法方块 ID：{block ?? "<null>"}。", path));
        }
    }

    private static void AddBlockCount(string block, long count, IDictionary<string, long> counts, string path, ICollection<ValidationIssue> issues)
    {
        if (count <= 0)
        {
            return;
        }

        try
        {
            counts.TryGetValue(block, out var current);
            counts[block] = checked(current + count);
        }
        catch (OverflowException)
        {
            issues.Add(new(ValidationSeverity.Error, "statistics.overflow", "蓝图统计发生整数溢出。", path));
        }
    }

    private static bool RangesOverlap(BlockRange left, BlockRange right) =>
        left.IsValid && right.IsValid
        && left.Min.X <= right.Max.X && right.Min.X <= left.Max.X
        && left.Min.Y <= right.Max.Y && right.Min.Y <= left.Max.Y
        && left.Min.Z <= right.Max.Z && right.Min.Z <= left.Max.Z;

    private static long AddChecked(long left, long right, string path, ICollection<ValidationIssue> issues)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            issues.Add(new(ValidationSeverity.Error, "statistics.overflow", "蓝图统计发生整数溢出。", path));
            return long.MaxValue;
        }
    }
}
