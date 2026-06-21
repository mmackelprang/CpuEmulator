using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2GcrTests
{
    [Fact]
    public void There_are_exactly_64_valid_on_disk_bytes()
    {
        Assert.Equal(64, Apple2Gcr.WriteTable.Length);
    }

    [Fact]
    public void Every_valid_byte_has_MSB_set_and_at_most_two_consecutive_zero_bits()
    {
        foreach (byte b in Apple2Gcr.WriteTable)
        {
            Assert.True((b & 0x80) != 0, $"byte ${b:X2} must have MSB set");
            Assert.True(NoMoreThanTwoConsecutiveZeros(b), $"byte ${b:X2} has >2 consecutive zero bits");
        }
    }

    [Fact]
    public void First_is_0x96_and_last_is_0xFF()
    {
        Assert.Equal(0x96, Apple2Gcr.WriteTable[0]);
        Assert.Equal(0xFF, Apple2Gcr.WriteTable[^1]);
    }

    [Fact]
    public void The_inverse_round_trips_every_6_bit_value()
    {
        for (int v = 0; v < 64; v++)
        {
            byte disk = Apple2Gcr.WriteTable[v];
            Assert.True(Apple2Gcr.TryDecode(disk, out int back));
            Assert.Equal(v, back);
        }
    }

    private static bool NoMoreThanTwoConsecutiveZeros(byte b)
    {
        int run = 0;
        for (int i = 7; i >= 0; i--)
        {
            if ((b & (1 << i)) == 0) { run++; if (run > 2) return false; }
            else run = 0;
        }
        return true;
    }
}
