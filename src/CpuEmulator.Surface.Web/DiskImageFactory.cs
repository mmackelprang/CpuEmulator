using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Builds an <see cref="IFluxImage"/> from raw disk-image bytes + a format (design D12 / T-D).
/// The one place "these bytes -> a flux image" lives, shared by the library-insert (R) and upload (S)
/// paths. .dsk/.po wrap a 256-byte-sector <see cref="DiskImage"/> in <see cref="DskFluxImage"/> with the
/// format's sector order; .woz is the native flux path — WozFluxImage parses WOZ2 (backlog row W shipped).
/// Validation (length, .woz magic) is the caller's job (S re-validates server-side); this throws the
/// shipped DskFluxImage/DiskImage/WozFluxImage exceptions on a malformed image.</summary>
public static class DiskImageFactory
{
    /// <summary>A .dsk/.po image is exactly 143,360 bytes (35 tracks * 16 sectors * 256). Exposed so the
    /// upload validator (S) can length-check before building.</summary>
    public const int DskBytes = 35 * 16 * 256;

    public static IFluxImage FromBytes(byte[] bytes, DiskFormat format)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        switch (format)
        {
            case DiskFormat.Dsk:
            case DiskFormat.Po:
                var block = new DiskImage(bytes, sectorSize: 256, isReadOnly: true);
                SectorOrderKind order = format == DiskFormat.Po ? SectorOrderKind.ProDos : SectorOrderKind.Dos33;
                return new DskFluxImage(block, order);
            case DiskFormat.Woz:
                // The native .woz flux path: WozFluxImage parses the WOZ2 container into the same IFluxImage
                // track-bitstream seam the controller reads (backlog row W). A malformed body throws the
                // shipped InvalidDataException (surfaced by S as the generic upload error).
                return new WozFluxImage(bytes);
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown disk format.");
        }
    }
}
