using Login38.Aux.Hunt;
using Shouldly;

namespace Login38.Aux.Tests.Hunt;

/// <summary>
/// Covers the fence the launcher puts round anything that would move the character.
/// </summary>
/// <remarks>
/// A hunt is set going on one floor and belongs on it. The client marks a cave mouth in the
/// attribute word it keeps per cell — one cell in a whole loaded floor carried the bit, and
/// it was the mouth — so the squares around it are shut in the launcher's own copy and the
/// routes never go there.
/// </remarks>
public sealed class GridWindowTests
{
    // As read off a client standing at the cave mouth on map 28. The window is 0x100 columns
    // wide and 131 rows tall from here, so the entrance below sits inside it.
    private const int OriginX = 32_640;
    private const int OriginY = 32_768;

    [Fact]
    public void ShutsTheSquareAnEntranceIsOn()
    {
        var grid = WithAnEntranceAt(32_830, 32_797);

        grid.Blocked(32_830, 32_797).ShouldBeTrue();
    }

    // Standing beside one is standing one server-side nudge away from another floor.
    [Theory]
    [InlineData(-2, 0)]
    [InlineData(2, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(-2, -1)]
    public void ShutsTheTileAroundItToo(int dx, int dy)
    {
        var grid = WithAnEntranceAt(32_830, 32_797);

        grid.Blocked(32_830 + dx, 32_797 + dy).ShouldBeTrue();
    }

    // And no further. A cave mouth sits in ground worth hunting, and walling off the room it
    // opens onto would cost the floor the hunt was put on.
    [Theory]
    [InlineData(6, 0)]
    [InlineData(0, 3)]
    [InlineData(-6, -3)]
    public void LeavesTheGroundBeyondItAlone(int dx, int dy)
    {
        var grid = WithAnEntranceAt(32_830, 32_797);

        grid.Blocked(32_830 + dx, 32_797 + dy).ShouldBeFalse();
    }

    [Fact]
    public void LeavesAFloorWithNoEntranceEntirelyOpen()
    {
        var grid = GridWindow.For(Empty(), OriginX, OriginY, Steps(), Corners());

        grid.Fence();

        grid.Blocked(32_830, 32_797).ShouldBeFalse();
    }

    private static GridWindow WithAnEntranceAt(int x, int y)
    {
        var cells = Empty();
        var at = (((y - OriginY) * HuntAddresses.GridStride) + (x - OriginX))
            * HuntAddresses.GridCellLength
            + HuntAddresses.GridCellAttribute;

        // What the client itself holds there: blocked, walkable ground, and the entrance bit.
        BitConverter.TryWriteBytes(cells.AsSpan(at), (ushort)0x000B);

        var grid = GridWindow.For(cells, OriginX, OriginY, Steps(), Corners());

        grid.Fence();

        return grid;
    }

    private static byte[] Empty() =>
        new byte[HuntAddresses.GridStride * HuntAddresses.GridRows * HuntAddresses.GridCellLength];

    /// <summary>The client's own eight headings and its four corner tables.</summary>
    /// <remarks>
    /// Only here so a window can be built. Nothing under test walks anywhere — the fence is
    /// written into the attribute word and read back out of it.
    /// </remarks>
    private static readonly (int X, int Y)[] TheEightHeadings =
        [(0, -1), (2, -1), (2, 0), (2, 1), (0, 1), (-2, 1), (-2, 0), (-2, -1)];

    /// <inheritdoc cref="TheEightHeadings"/>
    private static readonly int[][] TheCornerTables =
    [
        [0, -255, 0, 258, 0, 255, 0, -2],
        [0, 2, 0, 257, 0, 254, 0, 0],
        [0, 1, 0, 256, 0, -1, 0, -257],
        [0, 0, 1, 1, 256, 256, -1, -1],
    ];

    private static (int X, int Y)[] Steps() => TheEightHeadings;

    private static int[][] Corners() => TheCornerTables;
}
