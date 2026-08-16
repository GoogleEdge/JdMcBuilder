namespace JdMcBuilder.Core.Blueprint;

public sealed record BatchPlannerOptions(
    long MaxBlocksPerBatch = 100_000,
    int MaxPayloadBytes = 512 * 1024);

public abstract record BuildBatch(
    string BatchId,
    string PhaseId,
    string OperationId,
    long BlockCount);

public sealed record FillBatch(
    string BatchId,
    string PhaseId,
    string OperationId,
    BlockRange Range,
    string Block,
    IReadOnlyDictionary<string, string>? States = null)
    : BuildBatch(BatchId, PhaseId, OperationId, Range.Volume);

public sealed record ExplicitBlocksBatch(
    string BatchId,
    string PhaseId,
    string OperationId,
    IReadOnlyList<BlockPlacement> Blocks)
    : BuildBatch(BatchId, PhaseId, OperationId, Blocks.Count);

public sealed class BatchPlanner
{
    private readonly BatchPlannerOptions _options;

    public BatchPlanner(BatchPlannerOptions? options = null)
    {
        _options = options ?? new BatchPlannerOptions();
        if (_options.MaxBlocksPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBlocksPerBatch must be greater than zero.");
        }
    }

    public IReadOnlyList<BuildBatch> Plan(BlueprintDocument document)
    {
        var batches = new List<BuildBatch>();
        foreach (var phase in document.Phases.OrderBy(item => item.Order).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var operation in phase.Operations)
            {
                switch (operation)
                {
                    case FillOperation fill:
                        AddFillBatches(batches, phase, fill);
                        break;
                    case BlocksOperation blocks:
                        AddBlockBatches(batches, phase, blocks);
                        break;
                }
            }
        }

        return batches;
    }

    private void AddFillBatches(ICollection<BuildBatch> batches, BlueprintPhase phase, FillOperation operation)
    {
        var ranges = SplitRange(operation.Range, _options.MaxBlocksPerBatch);
        var index = 0;
        foreach (var range in ranges)
        {
            batches.Add(new FillBatch(
                CreateBatchId(phase.Id, operation.Id, index++),
                phase.Id,
                operation.Id,
                range,
                operation.Block,
                operation.States));
        }
    }

    private void AddBlockBatches(ICollection<BuildBatch> batches, BlueprintPhase phase, BlocksOperation operation)
    {
        var sorted = operation.Blocks
            .OrderBy(block => block.Block, StringComparer.Ordinal)
            .ThenBy(block => block.Position.Y)
            .ThenBy(block => block.Position.Z)
            .ThenBy(block => block.Position.X)
            .ToArray();
        var current = new List<BlockPlacement>();
        var currentBytes = 0;
        var index = 0;
        foreach (var block in sorted)
        {
            var estimated = EstimatePayloadBytes(block);
            if (current.Count > 0 && (current.Count >= _options.MaxBlocksPerBatch || currentBytes + estimated > _options.MaxPayloadBytes))
            {
                batches.Add(new ExplicitBlocksBatch(CreateBatchId(phase.Id, operation.Id, index++), phase.Id, operation.Id, current.ToArray()));
                current = [];
                currentBytes = 0;
            }

            current.Add(block);
            currentBytes += estimated;
        }

        if (current.Count > 0)
        {
            batches.Add(new ExplicitBlocksBatch(CreateBatchId(phase.Id, operation.Id, index), phase.Id, operation.Id, current.ToArray()));
        }
    }

    private static IReadOnlyList<BlockRange> SplitRange(BlockRange range, long maxBlocks)
    {
        if (!range.IsValid)
        {
            return [];
        }

        if (range.Volume <= maxBlocks)
        {
            return [range];
        }

        var result = new List<BlockRange>();
        var xLength = (long)range.Max.X - range.Min.X + 1;
        var yLength = (long)range.Max.Y - range.Min.Y + 1;
        var zLength = (long)range.Max.Z - range.Min.Z + 1;
        var sliceAxis = xLength >= yLength && xLength >= zLength ? 'x' : yLength >= zLength ? 'y' : 'z';
        var fixedVolume = sliceAxis switch
        {
            'x' => yLength * zLength,
            'y' => xLength * zLength,
            _ => xLength * yLength
        };
        var sliceLength = Math.Max(1, maxBlocks / Math.Max(1, fixedVolume));
        var cursor = sliceAxis switch
        {
            'x' => range.Min.X,
            'y' => range.Min.Y,
            _ => range.Min.Z
        };
        var end = sliceAxis switch
        {
            'x' => range.Max.X,
            'y' => range.Max.Y,
            _ => range.Max.Z
        };

        while (cursor <= end)
        {
            var sliceEnd = (int)Math.Min(end, (long)cursor + sliceLength - 1);
            result.Add(sliceAxis switch
            {
                'x' => new BlockRange(new(cursor, range.Min.Y, range.Min.Z), new(sliceEnd, range.Max.Y, range.Max.Z)),
                'y' => new BlockRange(new(range.Min.X, cursor, range.Min.Z), new(range.Max.X, sliceEnd, range.Max.Z)),
                _ => new BlockRange(new(range.Min.X, range.Min.Y, cursor), new(range.Max.X, range.Max.Y, sliceEnd))
            });
            cursor = sliceEnd + 1;
        }

        return result;
    }

    private static int EstimatePayloadBytes(BlockPlacement block) =>
        48 + block.Block.Length + (block.States?.Sum(state => state.Key.Length + state.Value.Length + 6) ?? 0);

    private static string CreateBatchId(string phaseId, string operationId, int index) =>
        $"{Sanitize(phaseId)}/{Sanitize(operationId)}/batch-{index:D4}";

    private static string Sanitize(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray();
        return new string(chars);
    }
}
