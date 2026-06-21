using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardBoardTests
{
    [Fact]
    public void Cpm_sector_order_is_the_documented_data_track_skew()
    {
        // research §5: the canonical CP/M data-track skew (apple-do order).
        int[] expected = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];
        int[] actual = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Cpm_order_is_a_permutation_distinct_from_dos33_and_prodos()
    {
        int[] cpm = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        // A valid interleave is a permutation of 0..15.
        Assert.Equal(Enumerable.Range(0, 16), cpm.OrderBy(x => x));
        // And it is genuinely a third ordering (distinct from the two shipped tables).
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33), cpm);
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos), cpm);
    }
}
