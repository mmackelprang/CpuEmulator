namespace CpuEmulator.Core;

/// <summary>Maps a coprocessor's LOGICAL address to the primary CPU's PHYSICAL address on the shared
/// bus (ADR 0015 Decision 2). Page-granular (4 KiB for the SoftCard). The dual-CPU Machine wraps the
/// primary program AddressSpace in a TranslatingAddressSpace built from this, and constructs the
/// coprocessor core over that wrapper — so the coprocessor core is UNCHANGED (it sees an ordinary
/// IAddressSpace). PR-J ships the concrete SoftCardTranslation (the 6-branch MAME-verified table).</summary>
public interface IAddressTranslation
{
    /// <summary>Translate a coprocessor logical address to the primary physical address. Pure: the same
    /// logical address always maps to the same physical address while the coprocessor runs.</summary>
    uint ToPhysical(uint logical);
}
