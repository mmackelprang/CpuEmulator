using System.Buffers.Binary;
using CpuEmulator.Core;
using CpuEmulator.Peripherals.Woz;

namespace CpuEmulator.Peripherals;

/// <summary>Parses a WOZ2 (.woz) disk image into per-track bitstreams the Apple Disk II head reads through
/// the IFluxImage seam (PR-F shipped the read path + seam; this is the file parser — backlog row W). WOZ2's
/// TRKS chunk stores MSB-first packed bits + an exact bit_count loop length, which map DIRECTLY onto
/// IFluxImage.TrackBits / TrackBitLength, so no re-nibblizing is needed (unlike DskFluxImage). WOZ2 only
/// (WOZ1 rejected); 5.25" only; the TRKS bitstream tracks (the FLUX chunk is skipped); read-only.</summary>
public sealed class WozFluxImage : IFluxImage
{
    private const int QuarterTracksPerTrack = 4;
    private const int WholeTracks = 40;          // a 5.25" image addresses tracks 0..39 (quarter-track 0..159)

    private readonly byte[] _file;
    private readonly byte[] _tmap;               // 160 quarter-track -> TRKS index ($FF = none)
    private readonly (int Start, int Blocks, int BitCount)[] _trk;  // 160 TRK records
    private readonly bool _writeProtected;

    public WozFluxImage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _file = bytes;

        if (bytes.Length < 12)
            throw new InvalidDataException("Not a .woz file: shorter than the 12-byte header.");
        // Header: "WOZ2" + FF 0A 0D 0A. WOZ1 ("WOZ1") is explicitly unsupported (decision W-1).
        if (bytes[0] == 0x57 && bytes[1] == 0x4F && bytes[2] == 0x5A && bytes[3] == 0x31)
            throw new InvalidDataException("WOZ1 is not supported; re-image as WOZ2.");
        if (!(bytes[0] == 0x57 && bytes[1] == 0x4F && bytes[2] == 0x5A && bytes[3] == 0x32))
            throw new InvalidDataException("Not a WOZ2 file (bad magic).");
        if (bytes[4] != 0xFF || bytes[5] != 0x0A || bytes[6] != 0x0D || bytes[7] != 0x0A)
            throw new InvalidDataException("Bad WOZ2 header sentinel.");

        // CRC32 (LE) over all bytes after the 4-byte CRC field; 0 = "do not verify" (WOZ spec).
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (storedCrc != 0u)
        {
            uint actual = WozCrc32.Compute(bytes.AsSpan(12));
            if (actual != storedCrc)
                throw new InvalidDataException($"WOZ CRC32 mismatch: stored 0x{storedCrc:X8}, computed 0x{actual:X8}.");
        }

        if (!TryFindChunk(bytes, "INFO", out ReadOnlySpan<byte> info))
            throw new InvalidDataException("WOZ2 missing the INFO chunk.");
        if (info.Length < 6)
            throw new InvalidDataException("WOZ2 INFO chunk is truncated.");
        byte diskType = info[1];
        if (diskType != 1)
            throw new InvalidDataException($"Only 5.25\" .woz images are supported (INFO disk_type={diskType}).");
        _writeProtected = info[2] != 0;

        if (!TryFindChunk(bytes, "TMAP", out ReadOnlySpan<byte> tmap))
            throw new InvalidDataException("WOZ2 missing the TMAP chunk.");
        if (tmap.Length < 160)
            throw new InvalidDataException("WOZ2 TMAP chunk is truncated.");
        _tmap = tmap.Slice(0, 160).ToArray();

        if (!TryFindChunk(bytes, "TRKS", out ReadOnlySpan<byte> trks))
            throw new InvalidDataException("WOZ2 missing the TRKS chunk.");
        if (trks.Length < 160 * 8)
            throw new InvalidDataException("WOZ2 TRKS record table is truncated.");
        _trk = new (int, int, int)[160];
        for (int i = 0; i < 160; i++)
        {
            int o = i * 8;
            int start = BinaryPrimitives.ReadUInt16LittleEndian(trks.Slice(o, 2));
            int blocks = BinaryPrimitives.ReadUInt16LittleEndian(trks.Slice(o + 2, 2));
            int bitCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(trks.Slice(o + 4, 4));
            _trk[i] = (start, blocks, bitCount);
        }
    }

    public int TrackCount => WholeTracks;
    public bool IsWriteProtected => _writeProtected;

    public ReadOnlySpan<byte> TrackBits(int track)
    {
        if (!TryResolve(track, out int start, out int blocks, out _))
            return ReadOnlySpan<byte>.Empty;
        int byteOffset = start * 512;
        int byteLen = blocks * 512;
        if (byteOffset < 0 || byteLen < 0 || byteOffset + byteLen > _file.Length)
            throw new InvalidDataException($"WOZ2 track {track} bitstream runs past the file.");
        return _file.AsSpan(byteOffset, byteLen);
    }

    public int TrackBitLength(int track)
        => TryResolve(track, out _, out _, out int bitCount) ? bitCount : 0;

    private bool TryResolve(int track, out int start, out int blocks, out int bitCount)
    {
        start = blocks = bitCount = 0;
        if (track < 0 || track >= WholeTracks) return false;
        byte idx = _tmap[track * QuarterTracksPerTrack];   // whole track t -> quarter-track t*4 (decision W-7)
        if (idx == 0xFF) return false;                     // no track mapped here
        (start, blocks, bitCount) = _trk[idx];
        return blocks > 0 && bitCount > 0;
    }

    /// <summary>Find the payload span of the named 4-char chunk, returning false if absent. Chunks are
    /// [4-byte id][4-byte LE size][size bytes], starting after the 12-byte header+CRC. (A bool/out form, not
    /// a nullable return: ReadOnlySpan&lt;byte&gt; cannot be the operand of <c>??</c>.)</summary>
    private static bool TryFindChunk(byte[] file, string id, out ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> idBytes = stackalloc byte[] { (byte)id[0], (byte)id[1], (byte)id[2], (byte)id[3] };
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            ReadOnlySpan<byte> here = file.AsSpan(pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4, 4));
            int payloadStart = pos + 8;
            if (payloadStart + (long)size > file.Length)
                break;                                      // a malformed/truncated chunk: stop scanning
            if (here.SequenceEqual(idBytes))
            {
                payload = file.AsSpan(payloadStart, (int)size);
                return true;
            }
            pos = payloadStart + (int)size;
        }
        payload = default;
        return false;
    }
}
