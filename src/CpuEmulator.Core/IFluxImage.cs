namespace CpuEmulator.Core;

/// <summary>
/// Track-bitstream backing for nibble-level disk controllers (the Apple Disk II, ADR 0014 Decision 6),
/// SITTING BESIDE <see cref="IBlockDevice"/> — NOT a replacement. A flux image is a per-track bit array
/// + an exact bit length that LOOPS (the on-disk track is a continuous spiral the head reads forever); a
/// `.woz` file IS this, and a `.dsk`/`.po` logical-sector image is RE-NIBBLIZED into one (PR-G). This is
/// the honest seam for copy-protection-grade fidelity: a track bitstream cannot be expressed as LBA
/// sectors, so it gets its own interface (the way <see cref="IDisplayDevice"/> sits beside
/// <see cref="IBlockDevice"/>). The controller owns the LSS sequencer + the head; the image only stores
/// bits.
/// </summary>
public interface IFluxImage
{
    /// <summary>Number of quarter/half/whole tracks the image addresses (the controller maps its
    /// half-track head position onto this; a 35-track DOS 3.3 disk has 35 whole tracks).</summary>
    int TrackCount { get; }

    /// <summary>The packed bits of <paramref name="track"/> (MSB-first within each byte). The valid bit
    /// count is <see cref="TrackBitLength"/> — the last byte may be partially used; the head wraps at the
    /// bit length, not the byte length.</summary>
    ReadOnlySpan<byte> TrackBits(int track);

    /// <summary>The exact number of VALID bits in <paramref name="track"/>'s stream (the loop point).
    /// The head advances bit by bit and wraps to 0 at this count.</summary>
    int TrackBitLength(int track);

    /// <summary>Whether the image is write-protected (a write-mode store is ignored when true).</summary>
    bool IsWriteProtected { get; }
}
