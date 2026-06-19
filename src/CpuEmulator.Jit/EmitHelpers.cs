using System.Reflection.Emit;

namespace CpuEmulator.Jit;

/// <summary>Per-block emit state: the ILGenerator plus the reusable scratch locals the emit
/// arms share. Locals are typed to make the IL match the interpreter's C# (which works in
/// <c>uint</c> for addresses and <c>byte</c>/<c>int</c> for data). The arg indices for the
/// BlockDelegate signature are fixed (M2-ii, after inserting ChainDispatch as the 5th param):
///   0 = cpu (TCpu — the concrete interpreter type), 1 = bus (AddressSpace), 2 = fastmem (Fastmem),
///   3 = dirty (DirtyMap), 4 = chain (ChainDispatch), 5 = ref long budget,
///   6 = out BlockExit exit. See BlockCompiler.ArgChain/ArgBudget/ArgExit.</summary>
internal sealed class EmitContext
{
    public ILGenerator Il { get; }

    /// <summary>uint — a resolved operand byte / zero-page address / pointer.</summary>
    public LocalBuilder AddrLocal { get; }

    /// <summary>int — a data byte read from memory (held as int so byte ops compose).</summary>
    public LocalBuilder DataLocal { get; }

    /// <summary>uint — the effective address an access targets (the page-class branch keys on it).
    /// INVARIANT: both LoadByteFromBus and EmitStoreByte CLOBBER this local (they stash the access
    /// address here). An arm that needs an address to survive across a bus access must hold it in
    /// AddrLocal and re-store ea before each access (the RMW memory arms do exactly this).</summary>
    public LocalBuilder EaLocal { get; }

    /// <summary>uint — a low byte during multi-byte address resolution.</summary>
    public LocalBuilder LoLocal { get; }

    /// <summary>uint — a high byte during multi-byte address resolution.</summary>
    public LocalBuilder HiLocal { get; }

    /// <summary>long — scratch for the fallback's cycle-delta math.</summary>
    public LocalBuilder TmpLong { get; }

    /// <summary>int — the binary sum the ADC/SBC arms compute first (the interpreter's <c>temp</c>):
    /// for ADC it carries the Z-from-binary quirk; for SBC it carries ALL the decimal-mode flags
    /// (Ground truth E). Held as a signed int so the SBC subtraction composes (the interpreter's
    /// <c>temp</c> is an int). No bus access occurs between writing and reading it.</summary>
    public LocalBuilder TmpInt { get; }

    /// <summary>int — the ADC/SBC decimal low-nibble intermediate (the interpreter's <c>before</c>),
    /// signed (SBC's <c>before</c> can go negative). Decimal arm only.</summary>
    public LocalBuilder NibLocal { get; }

    /// <summary>int — the ADC/SBC decimal BCD sum (the interpreter's <c>sum</c>), signed (SBC's
    /// <c>sum</c> can go negative; N/V/C derive from it before the +/-0x60 correction). Decimal arm
    /// only. A dedicated int local (NOT EaLocal, which is uint and clobbered by bus accesses).</summary>
    public LocalBuilder SumLocal { get; }

    /// <summary>The set of 256-byte pages this block's instruction bytes occupy. The intra-block
    /// SMC guard (Ground truth B / Task-5 hand-off note #2) uses this: a writable-RAM store whose
    /// target page is one of these MUST end the block, so the next dispatch re-decodes the
    /// (possibly modified) bytes — the JIT cannot keep running stale compiled IL for an opcode the
    /// guest just rewrote ahead of PC within the same block.</summary>
    public IReadOnlyCollection<int> SpannedPages { get; }

    /// <summary>int — the page index a writable-RAM store last wrote, or -1 when no such store has
    /// happened since the local was reset. The intra-block SMC guard reads this AFTER a store/RMW
    /// instruction completes: if the written page is one of the block's own SpannedPages, the block
    /// ends so the next dispatch re-decodes the (possibly self-modified) bytes. Reset to -1 before
    /// each store/RMW instruction so a no-store instruction never trips the guard.</summary>
    public LocalBuilder SmcPageLocal { get; }

    /// <summary>uint — M6 PR-4: a 32-bit staging local for the 68000 emit arms. EmitStoreReg32 stages the
    /// to-be-stored value here (the value arrives on the stack BELOW the receiver, so it must be stashed,
    /// the receiver pushed, then the value reloaded). Typed uint so the 68000's 32-bit register/operand
    /// values round-trip without the sign-extension a signed int local would impose. The next PR-4 agent's
    /// EA resolver + MOVE arm (Tasks 4-7) reuse it to hold a resolved EA value / the MOVE operand across the
    /// dest-EA resolution. Distinct from DataLocal (int) — keeping a separate uint local avoids Conv churn.</summary>
    public LocalBuilder M68kValueLocal { get; }

    /// <summary>uint — M6 PR-4 (Task 4/5): a SECOND 32-bit address staging local for the 68000 MOVE arm. The
    /// source EA read uses AddrLocal (the wide-bus helpers stash the access address there) and the dest-EA
    /// resolution would CLOBBER it, so the dest EA is resolved into THIS local (held across the dest store).
    /// The MOVE crux: the source read (with its (An)+/-(An) mutation) happens FIRST and the dest EA is
    /// resolved AFTER it, so the dest address needs a survivor local distinct from the source's AddrLocal.</summary>
    public LocalBuilder M68kAddr2Local { get; }

    /// <summary>uint — M6 PR-4: a DEDICATED staging local for the 68000 register-store helpers
    /// (EmitStoreReg32 / EmitStoreDataRegSized / EmitStoreAreg). These helpers receive the value BELOW the
    /// receiver on the stack, so they must stash it, push the receiver, then reload. CRITICAL: this staging
    /// local must be DISTINCT from M68kValueLocal — the dest (An)+/-(An) write path calls EmitAdvanceAreg
    /// (a register store) WHILE the MOVE operand is live in M68kValueLocal; sharing one local let the An
    /// write-back clobber the operand, writing the post-incremented/pre-decremented address to memory
    /// instead of the MOVE source value. (Pre-merge review HIGH finding, M6 PR-4.)</summary>
    public LocalBuilder M68kStoreStageLocal { get; }

    public EmitContext(ILGenerator il, IReadOnlyCollection<int> spannedPages)
    {
        Il = il;
        SpannedPages = spannedPages;
        AddrLocal = il.DeclareLocal(typeof(uint));
        DataLocal = il.DeclareLocal(typeof(int));
        EaLocal = il.DeclareLocal(typeof(uint));
        LoLocal = il.DeclareLocal(typeof(uint));
        HiLocal = il.DeclareLocal(typeof(uint));
        TmpLong = il.DeclareLocal(typeof(long));
        SmcPageLocal = il.DeclareLocal(typeof(int));
        TmpInt = il.DeclareLocal(typeof(int));
        NibLocal = il.DeclareLocal(typeof(int));
        SumLocal = il.DeclareLocal(typeof(int));
        M68kValueLocal = il.DeclareLocal(typeof(uint));
        M68kAddr2Local = il.DeclareLocal(typeof(uint));
        M68kStoreStageLocal = il.DeclareLocal(typeof(uint));
    }
}
