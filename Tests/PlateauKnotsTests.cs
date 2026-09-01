using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// Battery level is recorded in whole percentage points, so the charge line's samples are flat runs
// joined by single-step jumps. An interpolation that must pass through both ends of a flat run has
// no freedom inside it, so the staircase can only be addressed by choosing fewer knots. These pin
// what that choice is allowed to do: keep only real points, keep every level, and keep whatever the
// caller says it cannot lose.
public class PlateauKnotsTests
{
    private static double[] Staircase(int levelsCount, int plateauLength, int firstLevel = 50)
    {
        var ys = new List<double>();
        for (int level = 0; level < levelsCount; level++)
            for (int i = 0; i < plateauLength; i++)
                ys.Add(firstLevel + level);
        return [.. ys];
    }

    [Fact]
    public void KeepsOnlyPositionsThatExist()
    {
        var ys = Staircase(levelsCount: 8, plateauLength: 6);
        foreach (int k in PlateauKnots.Select(ys))
            Assert.InRange(k, 0, ys.Length - 1);
    }

    [Fact]
    public void ReturnsStrictlyIncreasingPositions()
    {
        var keep = PlateauKnots.Select(Staircase(levelsCount: 8, plateauLength: 6));
        for (int i = 1; i < keep.Length; i++)
            Assert.True(keep[i] > keep[i - 1], "Positions came back out of order or duplicated.");
    }

    [Fact]
    public void KeepsTheFirstAndLastPoint()
    {
        var ys   = Staircase(levelsCount: 8, plateauLength: 6);
        var keep = PlateauKnots.Select(ys);

        Assert.Equal(0, keep[0]);
        Assert.Equal(ys.Length - 1, keep[^1]);
    }

    // The min and max markers are placed on the knots, so a level lost here would move a marker off
    // the drawn line or report the wrong percentage.
    [Fact]
    public void KeepsEveryDistinctLevel()
    {
        var ys   = Staircase(levelsCount: 8, plateauLength: 6);
        var keep = PlateauKnots.Select(ys);

        var kept = keep.Select(k => ys[k]).ToHashSet();
        Assert.Equal(ys.ToHashSet(), kept);
    }

    [Fact]
    public void KeepsTheExtremes()
    {
        double[] ys  = [70, 70, 70, 99, 70, 70, 12, 70, 70];
        var      keep = PlateauKnots.Select(ys);

        var kept = keep.Select(k => ys[k]).ToList();
        Assert.Equal(ys.Max(), kept.Max());
        Assert.Equal(ys.Min(), kept.Min());
    }

    [Fact]
    public void CollapsesAPlateauToOneInteriorKnot()
    {
        // Two levels of six: first and last are forced, and each plateau contributes its centre.
        var ys   = Staircase(levelsCount: 2, plateauLength: 6);
        var keep = PlateauKnots.Select(ys);

        Assert.Equal([0, 2, 8, 11], keep);
    }

    [Fact]
    public void ThinsHardEnoughToMatter()
    {
        var ys   = Staircase(levelsCount: 8, plateauLength: 6);   // 48 points, 8 levels
        var keep = PlateauKnots.Select(ys);

        // One per level plus the two forced endpoints is the ceiling; anything more leaves flats.
        Assert.True(keep.Length <= 10, $"Expected at most 10 knots for 8 levels, got {keep.Length}.");
    }

    [Fact]
    public void KeepsEverythingWhenNothingRepeats()
    {
        double[] ys = [40, 41, 42, 43, 44];
        Assert.Equal([0, 1, 2, 3, 4], PlateauKnots.Select(ys));
    }

    [Fact]
    public void ShortSeriesAreLeftAlone()
    {
        Assert.Equal([], PlateauKnots.Select([]));
        Assert.Equal([0], PlateauKnots.Select([50]));
        Assert.Equal([0, 1], PlateauKnots.Select([50, 50]));
    }

    // The line's colour is keyed on the power state as well as the level, and the state can flip in
    // the middle of a plateau — sitting on mains at 80% for hours. Thinning must not erase that.
    [Fact]
    public void HonoursForcedPositions()
    {
        var ys   = Staircase(levelsCount: 2, plateauLength: 6);
        var keep = PlateauKnots.Select(ys, i => i is 4 or 5);

        Assert.Contains(4, keep);
        Assert.Contains(5, keep);
    }

    [Fact]
    public void ForcedPositionsDoNotDuplicateAnExistingKnot()
    {
        var ys   = Staircase(levelsCount: 2, plateauLength: 6);
        var keep = PlateauKnots.Select(ys, i => i is 0 or 2 or 11);

        Assert.Equal(keep.Distinct().Count(), keep.Length);
    }

    [Fact]
    public void FlatSeriesKeepsItsOwnEnds()
    {
        double[] ys  = [80, 80, 80, 80, 80];
        var      keep = PlateauKnots.Select(ys);

        Assert.Contains(0, keep);
        Assert.Contains(4, keep);
        Assert.All(keep, k => Assert.Equal(80, ys[k]));
    }
}
