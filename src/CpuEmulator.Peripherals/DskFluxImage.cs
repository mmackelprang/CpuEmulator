using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The .dsk/.po re-nibblizing adapter (ADR 0014 Decision 6 + OQ1-✅ — the .dsk/.po path folds
/// into the SAME IFluxImage track-bitstream seam PR-F shipped). Wraps a logical-sector image (the SP0
/// <see cref="IBlockDevice"/>: 256-byte sectors, 16 per track) and SYNTHESIZES each track's GCR bitstream
/// on demand — 16 physical sectors, each framed by self-sync gaps, a 4-and-4 address field
/// (volume/track/sector/checksum, D5 AA 96 ... DE AA EB) and a 6-and-2 data field (D5 AA AD + 343 bytes +
/// DE AA EB) from <see cref="Apple2SectorCodec"/>. The UNCHANGED <see cref="Apple2DiskII"/> head reads it
/// exactly like a .woz (format-agnostic above the seam). Targets unprotected DOS 3.3 (.dsk) / ProDOS
/// (.po) images only; copy-protected layouts and the CP/M skew are out of scope (the CP/M arc).</summary>
public sealed class DskFluxImage : IFluxImage
{
    private const int SectorsPerTrack = 16;
    private const byte Volume = 254;            // the conventional DOS 3.3 volume number ($FE)

    private readonly IBlockDevice _block;
    private readonly SectorOrderKind _order;     // resolved per track at synthesis (ADR 0017 Decision 1)
    private readonly byte[]?[] _trackCache;      // lazily synthesized per-track nibble bitstreams

    public DskFluxImage(IBlockDevice block, SectorOrderKind order)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.SectorSize != 256)
            throw new ArgumentException($"a .dsk/.po image must have 256-byte sectors; got {block.SectorSize}.",
                nameof(block));
        if (block.SectorCount % SectorsPerTrack != 0)
            throw new ArgumentException(
                $"sector count {block.SectorCount} must be a multiple of {SectorsPerTrack} (whole tracks).",
                nameof(block));
        _block = block;
        _order = order;
        _trackCache = new byte[]?[block.SectorCount / SectorsPerTrack];
    }

    public int TrackCount => _trackCache.Length;
    public bool IsWriteProtected => _block.IsReadOnly;

    public ReadOnlySpan<byte> TrackBits(int track) => GetTrack(track);
    public int TrackBitLength(int track) => GetTrack(track).Length * 8;   // packed 8 bits per nibble byte

    private byte[] GetTrack(int track)
    {
        if (track < 0 || track >= _trackCache.Length)
            throw new ArgumentOutOfRangeException(nameof(track));
        // Not thread-safe: concurrent first-touch of the same track may Synthesize twice, but Synthesize is
        // pure (same image bytes + order -> identical bitstream), so the result is the same either way.
        return _trackCache[track] ??= Synthesize(track);
    }

    /// <summary>Build the nibble bitstream for <paramref name="track"/>: 16 physical sectors, each with a
    /// self-sync gap, an address field, and a 6-and-2 data field. The PR-F head finds byte boundaries on
    /// any MSB-set byte and the prologues on D5 AA 96 / D5 AA AD, exactly as a real RWTS does.</summary>
    private byte[] Synthesize(int track)
    {
        // Resolve the physical->logical skew for THIS track (CP/M is per-track: boot table for tracks 0-2,
        // data table for 3+; DOS/ProDOS ignore the track -> the single-skew table). ADR 0017 Decision 1.
        int[] physToLog = Apple2SectorOrder.PhysicalToLogical(_order, track);
        var nibbles = new List<byte>(SectorsPerTrack * 400);
        for (int phys = 0; phys < SectorsPerTrack; phys++)
        {
            int logical = physToLog[phys];
            long lba = (long)track * SectorsPerTrack + logical;
            var sector = new byte[256];
            _block.ReadSector(lba, sector);

            // --- self-sync gap (12 sync bytes is ample for the head to re-byte-align) ---
            for (int i = 0; i < 12; i++) nibbles.Add(0xFF);

            // --- address field: D5 AA 96 | vol track sector chk (4-and-4) | DE AA EB ---
            nibbles.AddRange([0xD5, 0xAA, 0x96]);
            byte chk = (byte)(Volume ^ track ^ phys);
            Add44(nibbles, Volume);
            Add44(nibbles, (byte)track);
            Add44(nibbles, (byte)phys);
            Add44(nibbles, chk);
            nibbles.AddRange([0xDE, 0xAA, 0xEB]);

            // --- a short gap, then the data field: D5 AA AD | 343 6-and-2 bytes | DE AA EB ---
            for (int i = 0; i < 6; i++) nibbles.Add(0xFF);
            nibbles.AddRange([0xD5, 0xAA, 0xAD]);
            nibbles.AddRange(Apple2SectorCodec.EncodeData(sector));
            nibbles.AddRange([0xDE, 0xAA, 0xEB]);
        }
        return nibbles.ToArray();
    }

    private static void Add44(List<byte> dst, byte value)
    {
        (byte hi, byte lo) = Apple2SectorCodec.Encode44(value);
        dst.Add(hi);
        dst.Add(lo);
    }
}
