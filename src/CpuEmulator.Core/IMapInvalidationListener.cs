namespace CpuEmulator.Core;

/// <summary>A listener the JIT registers on an <see cref="AddressSpace"/> so a run-time bus remap
/// (<see cref="IAddressSpace.Remap"/> / <see cref="IAddressSpace.RemapPeripheral"/>) can invalidate the
/// affected pages: the JIT re-classifies them in its fastmem and evicts any compiled blocks decoded
/// from them. Defined in Core, implemented in Jit — the same dependency direction as the internal
/// fastmem view (Core defines the seam; Core never references Jit). The interpreter tier registers no
/// listener (it re-reads the live page table on every access, so a remap is immediately correct).</summary>
public interface IMapInvalidationListener
{
    /// <summary>A range of <paramref name="pageCount"/> 256-byte pages starting at page
    /// <paramref name="firstPage"/> (= address &gt;&gt; 8) was re-pointed. The listener must drop any
    /// cached state derived from the OLD mapping of those pages.</summary>
    void OnRemap(int firstPage, int pageCount);
}
