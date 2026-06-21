using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class DskFluxImageTests
{
    [Fact]
    public void Dos33_order_is_a_16_entry_permutation()
    {
        int[] map = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, map.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), map.ToHashSet()); // a permutation of 0..15
        Assert.Equal(0, map[0]);    // physical 0 == logical 0 (the DOS 3.3 anchor)
        Assert.Equal(15, map[15]);  // physical 15 == logical 15
    }

    [Fact]
    public void ProDos_order_is_a_16_entry_permutation_distinct_from_Dos33()
    {
        int[] po = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos);
        int[] dos = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, po.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), po.ToHashSet());
        Assert.NotEqual(dos, po);   // the two interleaves differ (that is the .dsk vs .po distinction)
    }
}
