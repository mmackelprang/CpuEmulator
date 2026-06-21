using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Builds an <see cref="IFluxImage"/> from raw disk-image bytes + a format (design D12 / T-D).
/// The one place "these bytes -> a flux image" lives, shared by the library-insert (R) and upload (S)
/// paths. .dsk/.po wrap a 256-byte-sector <see cref="DiskImage"/> in <see cref="DskFluxImage"/> with the
/// format's sector order; .woz is the native flux path (a thin WozFluxImage follow-on — see the note).
/// Validation (length, .woz magic) is the caller's job (S re-validates server-side); this throws the
/// shipped DskFluxImage/DiskImage exceptions on a malformed .dsk/.po.</summary>
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
                // The native .woz flux path is a thin WozFluxImage (a noted IFluxImage follow-on; the
                // SyntheticFluxImage doc-comment flags it). Until it ships, a .woz runtime insert is out
                // of this PR's literal scope; R/S that need .woz construct the WozFluxImage when it lands.
                throw new NotSupportedException(
                    ".woz runtime insert needs WozFluxImage (a noted IFluxImage follow-on); use .dsk/.po for Q.");
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown disk format.");
        }
    }
}
