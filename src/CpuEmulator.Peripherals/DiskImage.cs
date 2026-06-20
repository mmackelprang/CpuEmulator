using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// A raw sector image backing an <see cref="IBlockDevice"/>: a flat byte array where
/// LBA n occupies <c>[n * SectorSize, (n+1) * SectorSize)</c>. Constructed over an in-memory
/// array (the demo + tests) or loaded from a host file via <see cref="FromFile"/>. SP0 keeps
/// it raw; ATR/IMG headers and machine-specific formats are SP1+.
/// </summary>
public sealed class DiskImage : IBlockDevice
{
    private readonly byte[] _image;

    public int SectorSize { get; }
    public long SectorCount { get; }
    public bool IsReadOnly { get; }

    /// <summary>Wrap an existing image array. Its length must be a positive multiple of
    /// <paramref name="sectorSize"/>.</summary>
    public DiskImage(byte[] image, int sectorSize, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (sectorSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be positive.");
        if (image.Length == 0 || image.Length % sectorSize != 0)
            throw new ArgumentException(
                $"Image length {image.Length} must be a positive multiple of sector size {sectorSize}.",
                nameof(image));

        _image = image;
        SectorSize = sectorSize;
        SectorCount = image.Length / sectorSize;
        IsReadOnly = isReadOnly;
    }

    /// <summary>Load a raw image from a host file (read-write unless <paramref name="isReadOnly"/>).
    /// The on-disk file is NOT written back in SP0 — writes mutate the in-memory copy only
    /// (persistence is SP1+); the demo only reads.</summary>
    public static DiskImage FromFile(string path, int sectorSize, bool isReadOnly) =>
        new(File.ReadAllBytes(path), sectorSize, isReadOnly);

    public void ReadSector(long lba, Span<byte> dst)
    {
        if (dst.Length != SectorSize)
            throw new ArgumentException(
                $"Destination span length {dst.Length} must equal sector size {SectorSize}.", nameof(dst));
        _image.AsSpan(Offset(lba), SectorSize).CopyTo(dst);
    }

    public void WriteSector(long lba, ReadOnlySpan<byte> src)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Disk image is read-only.");
        if (src.Length != SectorSize)
            throw new ArgumentException(
                $"Source span length {src.Length} must equal sector size {SectorSize}.", nameof(src));
        src.CopyTo(_image.AsSpan(Offset(lba), SectorSize));
    }

    private int Offset(long lba)
    {
        if (lba < 0 || lba >= SectorCount)
            throw new ArgumentOutOfRangeException(nameof(lba),
                $"LBA {lba} is out of range [0, {SectorCount}).");
        return checked((int)(lba * SectorSize));
    }
}
