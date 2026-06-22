namespace CpuEmulator.Peripherals;

/// <summary>Which logical-sector ordering a flat .dsk/.po image uses. A `.dsk` is in DOS 3.3 logical
/// order; a `.po` is in ProDOS order; the SoftCard CP/M `.dsk` is in the CP/M (apple-do) order (the
/// THIRD ordering, research §5 — added in the CP/M arc, PR-K). <see cref="Cpm3"/> is apl2cpm3's CP/M 3.1
/// ordering — RAW DOS 3.3 on EVERY track (ADR 0018-A; the BOOTLDR `xlt` + the running LDRBIOS `fdxlt`
/// compose to identity over a raw presentation, so the disk must be laid down un-skewed).</summary>
public enum SectorOrderKind { Dos33, ProDos, Cpm, Cpm3 }

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

    // CP/M (SoftCard) DATA-track skew (research §5, the canonical apple-do data-track order, live-verified
    // correct). Used for tracks 3-34. The single-arg PhysicalToLogical(Cpm) returns this (its historical
    // meaning); the new (kind, track) overload selects boot vs. data per track.
    private static readonly int[] CpmDataPhysToLog =
        [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];

    // CP/M SoftCard BOOT-track skew (ADR 0017 Decision 1 — live-verified; research §5's earlier boot table
    // was wrong). System tracks 0-2 were written by the SoftCard boot ROM/loader with this interleave
    // physToLog[p] = (p*11) mod 16. Using the data table for these tracks loads boot2's bytes at the wrong
    // addresses (its $0F7D routine becomes $00/BRK -> a silent monitor crash before any handshake).
    private static readonly int[] CpmBootPhysToLog =
        [0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5];

    /// <summary>The number of CP/M system (boot) tracks: tracks 0-2 use the boot interleave, 3-34 the data
    /// table (DPB OFF=3, research §4 — the disk's own system/data split).</summary>
    private const int CpmSystemTracks = 3;

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> (a fresh copy per call so
    /// callers cannot mutate the shared table). For <see cref="SectorOrderKind.Cpm"/> this returns the
    /// DATA-track table (its historical meaning); use the (kind, track) overload for the per-track skew.</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        SectorOrderKind.Cpm => (int[])CpmDataPhysToLog.Clone(),
        // apl2cpm3 (CP/M 3.1): RAW DOS 3.3 on EVERY track. ADR 0018-A — the apl2cpm3 BOOTLDR applies its
        // OWN software `xlt` skew over the Disk II interface ROM, and the running LDRBIOS RWTS (`fdxlt`)
        // does the same, both matching the address-field sector ID; against a raw (un-pre-skewed) DOS 3.3
        // presentation these compose to identity. So unlike the 2.2 `Cpm` path (which needs the
        // `CpmBootPhysToLog`/`CpmDataPhysToLog` pre-skew because its loader does NOT re-translate), apl2cpm3
        // must be presented un-skewed: live-verified — under raw DOS33 on all tracks, CPMLDR's `LD SP,$0281`
        // (`$31`) lands at Z80 $0100, CPM3.SYS reads byte-exact, and the boot reaches CP/M 3.1 `A>`.
        SectorOrderKind.Cpm3 => (int[])Dos33PhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> on <paramref name="track"/>
    /// (ADR 0017 Decision 1). Only <see cref="SectorOrderKind.Cpm"/> is track-dependent: system tracks 0-2
    /// use the boot interleave, tracks 3+ use the data table. DOS 3.3 / ProDOS / <see cref="SectorOrderKind.Cpm3"/>
    /// are single-skew and ignore <paramref name="track"/> (the same table the single-arg overload returns —
    /// <see cref="SectorOrderKind.Cpm3"/> is raw DOS 3.3 on EVERY track, ADR 0018-A). A fresh copy per call.</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind, int track) => kind switch
    {
        SectorOrderKind.Cpm => track < CpmSystemTracks
            ? (int[])CpmBootPhysToLog.Clone()
            : (int[])CpmDataPhysToLog.Clone(),
        _ => PhysicalToLogical(kind),   // Dos33 / ProDos / Cpm3: track-independent, unchanged
    };
}
