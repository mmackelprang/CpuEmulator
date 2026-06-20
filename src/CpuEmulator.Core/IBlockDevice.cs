namespace CpuEmulator.Core;

/// <summary>
/// Backing storage for disk controllers: a flat array of fixed-size sectors addressed by LBA.
/// Machine-specific disk controllers (SIO/810, µPD765) sit ON TOP, translating the guest's
/// register protocol into block ops; image-format quirks (ATR headers, etc.) are the
/// controller/adapter's concern (SP1+). SP0's demo uses a RAW sector image.
/// </summary>
public interface IBlockDevice
{
    int SectorSize { get; }
    long SectorCount { get; }
    bool IsReadOnly { get; }

    /// <summary>Read sector <paramref name="lba"/> into <paramref name="dst"/> (exactly
    /// <see cref="SectorSize"/> bytes). Out-of-range LBA throws
    /// <see cref="ArgumentOutOfRangeException"/>; a wrong-length span throws
    /// <see cref="ArgumentException"/>.</summary>
    void ReadSector(long lba, Span<byte> dst);

    /// <summary>Write <paramref name="src"/> (exactly <see cref="SectorSize"/> bytes) to sector
    /// <paramref name="lba"/>. Throws <see cref="System.InvalidOperationException"/> if
    /// <see cref="IsReadOnly"/>; out-of-range LBA / wrong-length span throw as in
    /// <see cref="ReadSector"/>.</summary>
    void WriteSector(long lba, ReadOnlySpan<byte> src);
}
