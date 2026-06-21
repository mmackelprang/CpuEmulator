using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2TextAddressTests
{
    [Theory]
    [InlineData(0, 0x400)]
    [InlineData(1, 0x480)]
    [InlineData(7, 0x780)]   // region 0 last
    [InlineData(8, 0x428)]   // region 1 first
    [InlineData(15, 0x7A8)]  // region 1 last
    [InlineData(16, 0x450)]  // region 2 first
    [InlineData(23, 0x7D0)]  // region 2 last
    public void Text_row_base_matches_the_GBASCALC_landmarks(int r, int expected)
    {
        Assert.Equal((uint)expected, Apple2HiResAddress.TextRowBase(r, page2: false));
    }

    [Fact]
    public void The_24_text_row_bases_are_distinct()
    {
        var seen = new HashSet<uint>();
        for (int r = 0; r < 24; r++)
            Assert.True(seen.Add(Apple2HiResAddress.TextRowBase(r, page2: false)));
        Assert.Equal(24, seen.Count);
    }
}
