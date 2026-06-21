using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2SectorCodecTests
{
    private static byte[] SampleSector()
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)((i * 7 + 0x13) & 0xFF); // a distinctive pattern
        return s;
    }

    [Fact]
    public void EncodeData_emits_exactly_343_bytes()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        Assert.Equal(343, gcr.Length);   // 342 6-and-2 bytes + 1 checksum (research §8)
    }

    [Fact]
    public void Every_encoded_data_byte_is_a_valid_on_disk_GCR_byte()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        foreach (byte b in gcr)
            Assert.True(Apple2Gcr.TryDecode(b, out _), $"data byte ${b:X2} must be a valid GCR byte");
    }

    [Fact]
    public void The_data_field_round_trips_to_the_original_256_bytes()
    {
        byte[] sector = SampleSector();
        byte[] gcr = Apple2SectorCodec.EncodeData(sector);
        Assert.True(Apple2SectorCodec.TryDecodeData(gcr, out byte[] back));
        Assert.Equal(sector, back);
    }

    [Fact]
    public void A_corrupted_data_checksum_fails_to_decode()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        gcr[10] ^= 0x04;   // flip a bit inside a still-valid GCR byte region (corrupt the running XOR)
        // Either the byte is no longer valid GCR, or the checksum mismatches -> decode reports failure.
        bool ok = Apple2SectorCodec.TryDecodeData(gcr, out _);
        Assert.False(ok, "a corrupted data field must not silently decode");
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    [InlineData(0xA5)]
    [InlineData(0x3C)]
    public void The_4and4_address_encoding_round_trips_and_is_valid_on_disk(byte value)
    {
        (byte hi, byte lo) = Apple2SectorCodec.Encode44(value);
        // 4-and-4 bytes always have bit 7 set (the odd bits are 1).
        Assert.NotEqual(0, hi & 0x80);
        Assert.NotEqual(0, lo & 0x80);
        Assert.Equal(value, Apple2SectorCodec.Decode44(hi, lo));
    }
}
