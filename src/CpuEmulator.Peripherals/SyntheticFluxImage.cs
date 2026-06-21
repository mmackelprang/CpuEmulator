using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>An in-memory IFluxImage for tests + the .dsk/.po re-nibblizing adapter (PR-G): poke a
/// track's nibble bytes; they pack MSB-first into the track's bitstream (the bit length = 8 * byteCount).
/// A real .woz parser (WozFluxImage, a thin follow-on) produces the same IFluxImage from a file; the
/// controller cannot tell them apart (format-agnostic above the seam — the whole point of OQ1-✅).</summary>
public sealed class SyntheticFluxImage : IFluxImage
{
    private readonly byte[][] _trackBytes;
    private readonly int[] _trackBitLen;

    public SyntheticFluxImage(int trackCount)
    {
        _trackBytes = new byte[trackCount][];
        _trackBitLen = new int[trackCount];
        for (int t = 0; t < trackCount; t++)
        {
            _trackBytes[t] = [0xFF];     // a 1-byte all-sync default (a blank-ish track)
            _trackBitLen[t] = 8;
        }
    }

    public int TrackCount => _trackBytes.Length;
    public bool IsWriteProtected => false;

    /// <summary>Pack a sequence of nibble bytes (each already a valid on-disk byte) MSB-first into
    /// <paramref name="track"/>'s bitstream; the bit length becomes 8 * nibbles.Length (the loop point).</summary>
    public void SetTrackNibbles(int track, byte[] nibbles)
    {
        ArgumentNullException.ThrowIfNull(nibbles);
        _trackBytes[track] = (byte[])nibbles.Clone();
        _trackBitLen[track] = nibbles.Length * 8;
    }

    public ReadOnlySpan<byte> TrackBits(int track) => _trackBytes[track];
    public int TrackBitLength(int track) => _trackBitLen[track];
}
