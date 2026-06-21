namespace CpuEmulator.Peripherals;

/// <summary>Which logical-sector ordering a flat .dsk/.po image uses. A `.dsk` is in DOS 3.3 logical
/// order; a `.po` is in ProDOS order. (CP/M uses a THIRD ordering — NOT modeled here; it lands with the
/// CP/M disk in the CP/M arc, PR-K/O.)</summary>
public enum SectorOrderKind { Dos33, ProDos }

/// <summary>The logical↔physical sector interleave (Beneath Apple DOS). The disk head reads PHYSICAL
/// sectors 0..15 along a track; a .dsk/.po file stores its 16 sectors in LOGICAL order. When PR-G's
/// <see cref="DskFluxImage"/> lays physical sector p onto a synthesized track, it pulls the image LBA for
/// the LOGICAL sector that maps to physical p — i.e. PhysicalToLogical(order)[p]. DOS 3.3 and ProDOS use
/// different (well-documented, constant) 16-entry tables.</summary>
public static class Apple2SectorOrder
{
    // DOS 3.3: the standard "soft interleave" mapping physical -> logical (Beneath Apple DOS, Table).
    private static readonly int[] Dos33PhysToLog =
        [0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15];

    // ProDOS (.po): the ProDOS block interleave mapping physical -> logical.
    private static readonly int[] ProDosPhysToLog =
        [0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15];

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> (a fresh copy per call so
    /// callers cannot mutate the shared table).</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
