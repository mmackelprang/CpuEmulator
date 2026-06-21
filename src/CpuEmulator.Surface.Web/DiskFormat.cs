namespace CpuEmulator.Surface.Web;

/// <summary>The disk image format a runtime insert provides (design D12). <c>Woz</c> is the native flux
/// bitstream (PR-F); <c>Dsk</c>/<c>Po</c> are logical-sector images re-nibblized by DskFluxImage (PR-G)
/// — DOS 3.3 order for .dsk, ProDOS order for .po.</summary>
public enum DiskFormat { Woz, Dsk, Po }
