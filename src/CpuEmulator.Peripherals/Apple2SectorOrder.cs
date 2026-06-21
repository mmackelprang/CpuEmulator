namespace CpuEmulator.Peripherals;

/// <summary>Which logical-sector ordering a flat .dsk/.po image uses. A `.dsk` is in DOS 3.3 logical
/// order; a `.po` is in ProDOS order; the SoftCard CP/M `.dsk` is in the CP/M (apple-do) order (the
/// THIRD ordering, research §5 — added in the CP/M arc, PR-K).</summary>
public enum SectorOrderKind { Dos33, ProDos, Cpm }

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

    // CP/M (SoftCard) data-track skew (research §5, the canonical apple-do data-track order). The Z80 BIOS
    // does no translation (XLT=0); the skew is applied by the 6502 RWTS, so the on-disk physical->logical
    // interleave for CP/M data tracks is this third ordering (distinct from DOS 3.3 / ProDOS). Lands with
    // the CP/M disk in PR-K, exactly as this file's header note promised.
    private static readonly int[] CpmPhysToLog =
        [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> (a fresh copy per call so
    /// callers cannot mutate the shared table).</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        SectorOrderKind.Cpm => (int[])CpmPhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
