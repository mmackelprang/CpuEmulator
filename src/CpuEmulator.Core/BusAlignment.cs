namespace CpuEmulator.Core;

/// <summary>
/// The 68000 word/long bus-alignment rule, as a pure DETECTION predicate (M4.2, ADR 0003 Decision 2 +
/// ADR 0004 Decision 3). The 68000 requires word and long accesses to be EVEN-aligned; an odd-address
/// word/long access is an ADDRESS ERROR. This predicate detects that condition; it does NOT raise —
/// the address-error EXCEPTION (vector 3, the supervisor stack frame, the mode switch) is the M4.5
/// exception model. The M4.5 interpreter calls <see cref="IsMisaligned"/> BEFORE a wide access and
/// vectors through the address-error path instead of touching the bus. A byte access is never
/// misaligned. Alignment is a property of (address, width) — universal across buses — so this is a free
/// static function, not a per-bus method.
/// </summary>
public static class BusAlignment
{
    /// <summary>True iff the access is wider than a byte AND the address is odd (bit 0 set) — the 68000
    /// address-error condition. A byte access (<see cref="AccessWidth.Byte"/>) is never misaligned.</summary>
    public static bool IsMisaligned(uint address, AccessWidth width) =>
        width != AccessWidth.Byte && (address & 1u) != 0;
}
