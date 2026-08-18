using JdMcBuilder.Backends;
using JdMcBuilder.Core.Blueprint;

namespace JdMcBuilder.Tests;

public sealed class CommandSafetyTests
{
    [Fact]
    public void CommandsUseExpectedCoordinatesAndBlock()
    {
        var safety = new CommandSafety();
        var range = new BlockRange(new BlockPosition(1, 64, 2), new BlockPosition(3, 65, 4));

        Assert.Equal("//pos 1,64,2 3,65,4", safety.BuildWorldEditSelection(range));
        Assert.Equal("//set minecraft:stone", safety.BuildWorldEditSet("minecraft:stone"));
        Assert.Equal("//replace minecraft:stone minecraft:dirt", safety.BuildWorldEditReplace("minecraft:stone", "minecraft:dirt"));
        Assert.Equal("/fill 1 64 2 3 65 4 minecraft:stone", safety.BuildNativeFill(range, "minecraft:stone"));
    }

    [Fact]
    public void CommandsRejectInjectionInBlockId()
    {
        var safety = new CommandSafety();

        Assert.Throws<BackendException>(() => safety.BuildWorldEditSet("minecraft:stone;op nobody"));
        Assert.Throws<BackendException>(() => safety.BuildWorldEditSet("minecraft:stone\n"));
        Assert.Throws<BackendException>(() => safety.BuildNativeFill(
            new BlockRange(new BlockPosition(0, 0, 0), new BlockPosition(0, 0, 0)),
            "minecraft:stone\n/op nobody"));
        Assert.Throws<BackendException>(() => safety.BuildNativeFill(
            new BlockRange(new BlockPosition(1, 0, 0), new BlockPosition(0, 0, 0)),
            "minecraft:stone"));
        Assert.Throws<ArgumentException>(() => safety.BuildInternalSend("send\n/op nobody"));
        Assert.Throws<BackendException>(() => safety.BuildInternalSend("/fill 0 0 0 1 1 1 minecraft:stone"));
    }
}
