using CpuEmulator.Peripherals.Woz;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class WozCrc32Tests
{
    [Fact]
    public void Crc32_of_the_check_string_matches_the_known_vector()
    {
        // The canonical CRC32("123456789") test vector for the zlib/PNG polynomial.
        byte[] input = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, WozCrc32.Compute(input));
    }

    [Fact]
    public void Crc32_of_empty_is_zero()
    {
        Assert.Equal(0u, WozCrc32.Compute(System.ReadOnlySpan<byte>.Empty));
    }
}
