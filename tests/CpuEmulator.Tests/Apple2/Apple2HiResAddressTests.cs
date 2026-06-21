using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2HiResAddressTests
{
    [Theory]
    [InlineData(0, 0x2000)]
    [InlineData(1, 0x2400)]
    [InlineData(8, 0x2080)]
    [InlineData(64, 0x2028)]
    [InlineData(191, 0x3FD0)]
    public void HiRes_row_base_matches_the_verified_landmarks_page1(int y, int expected)
    {
        Assert.Equal((uint)expected, Apple2HiResAddress.RowBase(y, page2: false));
    }

    [Fact]
    public void Page2_is_the_page1_base_plus_0x2000()
    {
        for (int y = 0; y < 192; y++)
            Assert.Equal(Apple2HiResAddress.RowBase(y, page2: false) + 0x2000,
                         Apple2HiResAddress.RowBase(y, page2: true));
    }

    [Fact]
    public void The_192_row_bases_are_all_distinct_within_their_8KiB_page()
    {
        // Bijective over y=0..191: every row maps to a distinct $2000-page base (the address math is
        // a permutation, not a collision — the refuted swapped-stride variant collides).
        var seen = new HashSet<uint>();
        for (int y = 0; y < 192; y++)
            Assert.True(seen.Add(Apple2HiResAddress.RowBase(y, page2: false)),
                $"row {y} collided at ${Apple2HiResAddress.RowBase(y, false):X4}");
        Assert.Equal(192, seen.Count);
    }
}
