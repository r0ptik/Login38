using Login38.Aux.Hunt;
using Login38.Interop;
using Shouldly;

namespace Login38.Aux.Tests.Hunt;

/// <summary>
/// Covers the rule that tells a monster which walked from one that was put somewhere.
/// </summary>
/// <remarks>
/// Everything the hunt measures about a target — the route to it, how long the character has
/// stood still, how long it has gone without losing a point — is measured about where it was.
/// A blink invalidates all of it at once, and carrying those numbers across is what made a
/// teleporting monster cost the stall period and then the ignore period on top.
/// </remarks>
public sealed class HuntTaskTests
{
    [Theory]
    [InlineData(0, 0)]          // stood still
    [InlineData(2, 1)]          // one tile, which is two columns across
    [InlineData(6, 3)]          // three, which a late pass can still account for
    [InlineData(-6, -3)]
    public void ReadsAnOrdinaryStepAsWalking(int dx, int dy) =>
        HuntTask.Blinked(At(100, 200), At(100 + dx, 200 + dy)).ShouldBeFalse();

    [Theory]
    [InlineData(8, 0)]          // four tiles across in one fifth of a second
    [InlineData(0, 4)]
    [InlineData(40, 0)]
    [InlineData(0, -30)]
    public void ReadsAnythingFurtherAsHavingBeenMoved(int dx, int dy) =>
        HuntTask.Blinked(At(100, 200), At(100 + dx, 200 + dy)).ShouldBeTrue();

    // Two columns to a tile across, one row down, so the same number of grid units is a
    // different distance depending on which way it went.
    [Fact]
    public void CountsAcrossInColumnsAndDownInRows()
    {
        HuntTask.Blinked(At(100, 200), At(106, 200)).ShouldBeFalse();
        HuntTask.Blinked(At(100, 200), At(100, 206)).ShouldBeTrue();
    }

    private static HuntTarget At(int x, int y) =>
        new(default(GameAddress), 0x1234, "wolf", x, y, 100);
}
