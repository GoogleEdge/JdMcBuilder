using System.Text.Json;
using JdMcBuilder.Core.Blueprint;
using JdMcBuilder.Core.Safety;

namespace JdMcBuilder.Tests;

public sealed class BlueprintTests
{
    [Fact]
    public void ValidatorRejectsDuplicateExplicitPositions()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            "test",
            new CoordinateSystem(new BlockPosition(0, 64, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 64, 0), new BlockPosition(2, 64, 2)),
            [new BlueprintPhase(
                "details",
                "Details",
                10,
                [new BlocksOperation("blocks", [
                    new BlockPlacement(new BlockPosition(1, 64, 1), "minecraft:stone"),
                    new BlockPlacement(new BlockPosition(1, 64, 1), "minecraft:glass")
                ])])]);

        var result = BlueprintValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "blocks.duplicate_position");
    }

    [Fact]
    public void BatchPlannerSplitsLargeFillAlongLargestAxis()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            null,
            new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(9, 0, 0)),
            [new BlueprintPhase(
                "foundation",
                "Foundation",
                1,
                [new FillOperation("fill", new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(9, 0, 0)), "minecraft:stone")])]);

        var batches = new BatchPlanner(new BatchPlannerOptions(MaxBlocksPerBatch: 3)).Plan(document);

        Assert.Equal(4, batches.Count);
        Assert.All(batches, batch => Assert.True(batch.BlockCount <= 3));
        Assert.Equal(10, batches.Sum(batch => batch.BlockCount));
    }

    [Fact]
    public void ParserNormalizesUnqualifiedBlockAndParsesNestedBounds()
    {
        using var json = JsonDocument.Parse("""
        {
          "format": "mc-blueprint/v1",
          "bounds": { "from": [1, 2, 3], "to": [2, 2, 3] },
          "phases": [{
            "id": "a",
            "operations": [{
              "id": "f",
              "type": "fill",
              "from": [1, 2, 3],
              "to": [2, 2, 3],
              "block": "stone"
            }]
          }]
        }
        """);

        var document = BlueprintParser.ParseDocument(json.RootElement);

        Assert.Equal(new BlockPosition(1, 2, 3), document.Bounds.Min);
        var fill = Assert.IsType<FillOperation>(document.Phases[0].Operations[0]);
        Assert.Equal("minecraft:stone", fill.Block);
    }

    [Fact]
    public void ParserAcceptsCanonicalMinMaxBounds()
    {
        using var json = JsonDocument.Parse("""
        {
          "format": "mc-blueprint/v1",
          "bounds": { "min": [1, 2, 3], "max": [2, 2, 3] },
          "phases": [{
            "id": "a",
            "operations": [{
              "id": "f",
              "type": "fill",
              "from": [1, 2, 3],
              "to": [2, 2, 3],
              "block": "stone"
            }]
          }]
        }
        """);

        var document = BlueprintParser.ParseDocument(json.RootElement);

        Assert.Equal(new BlockPosition(1, 2, 3), document.Bounds.Min);
        Assert.Equal(new BlockPosition(2, 2, 3), document.Bounds.Max);
    }

    [Fact]
    public void ValidatorRejectsTrailingNewlineInBlockId()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            null,
            new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
            [new BlueprintPhase("a", "A", 1, [new BlocksOperation("b", [
                new BlockPlacement(new BlockPosition(0, 0, 0), "minecraft:stone\n")
            ])])]);

        var result = BlueprintValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "block.invalid_id");
    }

    [Fact]
    public void ValidatorRejectsUnsupportedBlockStates()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            null,
            new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
            [new BlueprintPhase("a", "A", 1, [new FillOperation(
                "f",
                new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
                "minecraft:oak_log",
                new Dictionary<string, string> { ["axis"] = "y" })])]);

        var result = BlueprintValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "states.unsupported");
    }

    [Fact]
    public void ValidatorRejectsNonMinecraftBlockNamespace()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            null,
            new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
            [new BlueprintPhase("a", "A", 1, [new BlocksOperation("b", [
                new BlockPlacement(new BlockPosition(0, 0, 0), "custom:stone")])])]);

        var result = BlueprintValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "block.invalid_id");
    }

    [Fact]
    public void ValidatorAppliesAllowedRegionToFill()
    {
        var document = new BlueprintDocument(
            "mc-blueprint/v1",
            null,
            new CoordinateSystem(new BlockPosition(0, 0, 0), "+z", "minecraft-block"),
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(4, 4, 4)),
            [new BlueprintPhase("a", "A", 1, [new FillOperation("f", new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(4, 4, 4)), "minecraft:stone")])]);

        var result = BlueprintValidator.Validate(document, new BuildSafetyOptions
        {
            AllowedRegion = new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(3, 4, 4))
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "range.outside_allowed_region");
    }
}
