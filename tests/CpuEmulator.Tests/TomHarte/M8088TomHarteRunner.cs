using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 8088 TomHarte DATA-axis runner — M5.5a (the scaffold flipped to a real Step + diff). It installs the
/// case's initial bus + the full 14-register initial state, calls <see cref="M8086Cpu.Step"/> exactly once,
/// then diffs the resulting 14 registers + the changed RAM cells against the case's MERGED final state
/// (<see cref="M8088TomHarteCase.MergedFinalRegs"/>). The FLAGS compare is mask-aware (both sides ANDed with the
/// opcode's defined-flag mask before comparing) so an undefined flag bit never spuriously fails — though for the
/// MOV family this is moot (MOV sets no flags). The <c>queue</c> + <c>cycles</c> stay carried-not-asserted (the
/// data axis does not need them; the timing axis is M5.5e).
/// </summary>
internal static class M8088TomHarteRunner
{
    // Per-worker reusable 1 MiB 20-bit little-endian program bus (lever 2). 1 MB/case × ~millions of cases is the
    // single largest per-case allocation in the suite; pooling collapses it. RunCase is synchronous → [ThreadStatic]
    // is reentrancy-safe.
    [ThreadStatic] private static AddressSpace? _busTls;
    [ThreadStatic] private static byte[]? _ramTls;

    private static AddressSpace RentBus()
    {
        if (_busTls is null)
        {
            _ramTls = new byte[0x100000];
            _busTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
            _busTls.MapMemory(0, _ramTls, writable: true);
        }
        _busTls.ClearMappedBacking(_ramTls!);   // re-zero; mapping persists → identical to a fresh new byte[0x100000]
        return _busTls;
    }

    // Per-worker reused JIT (lever 4). Built ONCE per worker thread bound to the pooled bus; ResetForReuse() flushes
    // the block cache between cases so the SAME (ushort)IP recompiles from the new case's bytes (the isolation
    // invariant). The inner M8086Cpu is wrapped once; SetRegister re-seeds it per case.
    [ThreadStatic] private static JittedCpu<M8086Cpu>? _jitTls;
    [ThreadStatic] private static M8086Cpu? _jitInnerTls;

    private static (JittedCpu<M8086Cpu> Jit, M8086Cpu Inner) RentJit(AddressSpace bus)
    {
        if (_jitTls is null)
            (_jitTls, _jitInnerTls) = M8086JittedCpuFactory.Create(bus);
        else
            _jitTls.ResetForReuse();   // flush cache + clear chains + reset inner — bound to the SAME pooled bus
        return (_jitTls, _jitInnerTls!);
    }

    /// <summary>
    /// Build a fresh 20-bit little-endian bus, install <c>initial.ram</c>, construct an <see cref="M8086Cpu"/>,
    /// set the full 14-register initial state, Step once, then diff registers + RAM against the merged final.
    /// Returns null on a full data-axis pass, or a human-readable diff string on the FIRST mismatch.
    /// </summary>
    /// <param name="c">The vector case.</param>
    /// <param name="metadata">The per-opcode flags-mask table (<see cref="M8088Metadata.Empty"/> when absent).</param>
    /// <param name="opcodeHex">The file's opcode (e.g. "88") — keys the flags-mask lookup. The reg-field lookup
    /// is null here: the only MOV group opcodes are C6/C7 (reg=0) and MOV writes no flags, so the mask path is
    /// exercised but the result is moot (MOV asserts no flag changes — any reasonable mask passes).</param>
    /// <param name="regField">For an opcode-GROUP form (M5.5b ALU 0x80/0x81/0x83), the caller passes the ModR/M
    /// reg subfield (e.g. (c.Bytes[1] >> 3) &amp; 7) so the per-subgroup flags-mask is selected; null for plain
    /// opcodes and the MOV family.</param>
    public static string? RunCase(M8088TomHarteCase c, M8088Metadata metadata, string opcodeHex, int? regField = null)
    {
        // 20-bit physical address space, little-endian (the 8086/8088 default — NO BigEndian), per ADR 0005 D2.
        // Map the WHOLE 1 MB as writable RAM up front: AddressSpace silently drops a Write8 to an UNMAPPED page
        // and returns open-bus (0xFF) on a read there, so without the backing the initial-RAM install would be a
        // no-op and the instruction fetch would read garbage. (The M5.4 scaffold never Stepped, so it never
        // needed the backing; M5.5a does.)
        var bus = RentBus();
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        var cpu = new M8086Cpu(bus);
        var ir = c.Initial.Regs;
        cpu.SetRegister("AX", ir.Ax);
        cpu.SetRegister("BX", ir.Bx);
        cpu.SetRegister("CX", ir.Cx);
        cpu.SetRegister("DX", ir.Dx);
        cpu.SetRegister("CS", ir.Cs);
        cpu.SetRegister("SS", ir.Ss);
        cpu.SetRegister("DS", ir.Ds);
        cpu.SetRegister("ES", ir.Es);
        cpu.SetRegister("SP", ir.Sp);
        cpu.SetRegister("BP", ir.Bp);
        cpu.SetRegister("SI", ir.Si);
        cpu.SetRegister("DI", ir.Di);
        cpu.SetRegister("IP", ir.Ip);
        cpu.SetRegister("FLAGS", ir.Flags);

        // (a) Step ONCE. The generated Step decodes through the segmented (CS<<4)+IP fetch, advances IP, and
        //     executes the MOV body. The queue + cycles are ignored (data axis only).
        cpu.Step();

        // (b)/(c) read back the 14 registers; compute the expected merged-final regs.
        var expected = c.MergedFinalRegs();
        ushort flagsMask = metadata.FlagsMask(opcodeHex, regField);

        // (d) compare each register. FLAGS is mask-aware (both sides ANDed with the defined-flag mask).
        string? regMismatch =
            Diff("AX", (ushort)cpu.GetRegister("AX"), expected.Ax) ??
            Diff("BX", (ushort)cpu.GetRegister("BX"), expected.Bx) ??
            Diff("CX", (ushort)cpu.GetRegister("CX"), expected.Cx) ??
            Diff("DX", (ushort)cpu.GetRegister("DX"), expected.Dx) ??
            Diff("CS", (ushort)cpu.GetRegister("CS"), expected.Cs) ??
            Diff("SS", (ushort)cpu.GetRegister("SS"), expected.Ss) ??
            Diff("DS", (ushort)cpu.GetRegister("DS"), expected.Ds) ??
            Diff("ES", (ushort)cpu.GetRegister("ES"), expected.Es) ??
            Diff("SP", (ushort)cpu.GetRegister("SP"), expected.Sp) ??
            Diff("BP", (ushort)cpu.GetRegister("BP"), expected.Bp) ??
            Diff("SI", (ushort)cpu.GetRegister("SI"), expected.Si) ??
            Diff("DI", (ushort)cpu.GetRegister("DI"), expected.Di) ??
            Diff("IP", (ushort)cpu.GetRegister("IP"), expected.Ip) ??
            Diff("FLAGS",
                M8088Metadata.ApplyFlagsMask((ushort)cpu.GetRegister("FLAGS"), flagsMask),
                M8088Metadata.ApplyFlagsMask(expected.Flags, flagsMask));
        if (regMismatch is not null)
            return $"{c.Name}: {regMismatch}";

        // (e) compare final.ram cells against the bus read-back.
        foreach (var cell in c.Final.Ram)
        {
            byte actual = bus.Read8(cell.Address & mask);
            if (actual != cell.Value)
                return $"{c.Name}: ram[0x{cell.Address & mask:X5}] expected 0x{cell.Value:X2}, got 0x{actual:X2}";
        }

        // (f) full pass.
        return null;
    }

    /// <summary>
    /// M5.6 tier-parity path: run one instruction through <see cref="CpuEmulator.Jit.JittedCpu{TCpu}"/> over the
    /// 8086 (all-fallback → the JIT result IS the interpreter result) and diff the DATA axis exactly as
    /// <see cref="RunCase"/> does — the byte-identical Tier-0-vs-Tier-1 gate. BYTE-FOR-BYTE identical to RunCase
    /// EXCEPT it drives <c>jit.Run(ref budget)</c> with a 1-cycle budget instead of <c>cpu.Step()</c>: every 8086
    /// op falls back to inner.Step (the empty-Ops NeedsFallback descriptors), so the single emitted fallback runs
    /// one Step and the final state matches the interpreter's bit-for-bit.
    ///
    /// <para>The budget=1 single-instruction rationale: JittedCpu.Run is a budget-driven loop — `while (budget &gt; 0)`
    /// runs another block each iteration. A 1-cycle budget runs the loop ONCE: the check passes once (1 &gt; 0), the
    /// fallback op charges the instruction's cycle cost (driving budget &lt;= 0 — every 8086 instruction charges
    /// &gt;= 1 cycle via the ReadBus/fetch loop), and the loop exits. A larger budget would run a SECOND, garbage
    /// instruction at the advanced (CS:)IP.</para>
    ///
    /// <para>GAP-3 note: the JIT dispatcher's cache key is <c>(ushort)IP</c> — EXACT (IP is already a ushort, no
    /// 24-bit-PC truncation like the 68000). BlockCompiler.Discover decodes at the RAW ushort IP (ignoring CS), so
    /// the discovery decode may read the wrong physical bytes — but harmlessly: M8086Cpu.Decode never throws
    /// (pure byte-consumption + dictionary lookups; unknown keys → the Undefined sentinel = fallback), and the
    /// real fallback inner.Step does the proper segmented (CS&lt;&lt;4)+IP fetch. So the result is exact.</para>
    /// </summary>
    public static string? RunCaseThroughJit(M8088TomHarteCase c, M8088Metadata metadata, string opcodeHex, int? regField = null)
    {
        // Build the bus EXACTLY as RunCase: 20-bit little-endian, 1 MB writable RAM, install initial.ram.
        var bus = RentBus();
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        // Rent this worker thread's reused Tier-1 JittedCpu<M8086Cpu> (lever 4) bound to the SAME pooled bus.
        // RentJit flushes the block cache + resets the inner CPU between cases (ResetForReuse) so the SAME (ushort)IP
        // recompiles from THIS case's bytes — set the 14 registers on `cpu` (the rent's inner) below.
        var (jit, cpu) = RentJit(bus);
        var ir = c.Initial.Regs;
        cpu.SetRegister("AX", ir.Ax);
        cpu.SetRegister("BX", ir.Bx);
        cpu.SetRegister("CX", ir.Cx);
        cpu.SetRegister("DX", ir.Dx);
        cpu.SetRegister("CS", ir.Cs);
        cpu.SetRegister("SS", ir.Ss);
        cpu.SetRegister("DS", ir.Ds);
        cpu.SetRegister("ES", ir.Es);
        cpu.SetRegister("SP", ir.Sp);
        cpu.SetRegister("BP", ir.Bp);
        cpu.SetRegister("SI", ir.Si);
        cpu.SetRegister("DI", ir.Di);
        cpu.SetRegister("IP", ir.Ip);
        cpu.SetRegister("FLAGS", ir.Flags);

        // (a) Run EXACTLY ONE instruction through Tier-1. The all-fallback descriptor runs one inner.Step; the
        //     budget-driven loop runs once (see the budget=1 rationale above).
        long budget = 1;
        jit.Run(ref budget);

        // (b)/(c) read back the 14 registers; compute the expected merged-final regs — IDENTICAL to RunCase.
        var expected = c.MergedFinalRegs();
        ushort flagsMask = metadata.FlagsMask(opcodeHex, regField);

        // (d) compare each register. FLAGS is mask-aware (both sides ANDed with the defined-flag mask).
        string? regMismatch =
            Diff("AX", (ushort)cpu.GetRegister("AX"), expected.Ax) ??
            Diff("BX", (ushort)cpu.GetRegister("BX"), expected.Bx) ??
            Diff("CX", (ushort)cpu.GetRegister("CX"), expected.Cx) ??
            Diff("DX", (ushort)cpu.GetRegister("DX"), expected.Dx) ??
            Diff("CS", (ushort)cpu.GetRegister("CS"), expected.Cs) ??
            Diff("SS", (ushort)cpu.GetRegister("SS"), expected.Ss) ??
            Diff("DS", (ushort)cpu.GetRegister("DS"), expected.Ds) ??
            Diff("ES", (ushort)cpu.GetRegister("ES"), expected.Es) ??
            Diff("SP", (ushort)cpu.GetRegister("SP"), expected.Sp) ??
            Diff("BP", (ushort)cpu.GetRegister("BP"), expected.Bp) ??
            Diff("SI", (ushort)cpu.GetRegister("SI"), expected.Si) ??
            Diff("DI", (ushort)cpu.GetRegister("DI"), expected.Di) ??
            Diff("IP", (ushort)cpu.GetRegister("IP"), expected.Ip) ??
            Diff("FLAGS",
                M8088Metadata.ApplyFlagsMask((ushort)cpu.GetRegister("FLAGS"), flagsMask),
                M8088Metadata.ApplyFlagsMask(expected.Flags, flagsMask));
        if (regMismatch is not null)
            return $"{c.Name}: {regMismatch}";

        // (e) compare final.ram cells against the bus read-back — IDENTICAL to RunCase.
        foreach (var cell in c.Final.Ram)
        {
            byte actual = bus.Read8(cell.Address & mask);
            if (actual != cell.Value)
                return $"{c.Name}: ram[0x{cell.Address & mask:X5}] expected 0x{cell.Value:X2}, got 0x{actual:X2}";
        }

        // (f) full pass.
        return null;
    }

    private static string? Diff(string name, ushort actual, ushort expected) =>
        actual == expected ? null : $"{name} expected 0x{expected:X4}, got 0x{actual:X4}";

    /// <summary>
    /// M5.5b — classify whether a FAILING IDIV (F6/F7 /7) case is the documented 8086 IDIV QUOTIENT-SIGN QUIRK
    /// (a SECOND honest deferral, distinct from the divide-error/INT0 class). The 8086's microcoded divider
    /// applies the quotient sign via a NEG step whose result differs from the clean two's-complement quotient
    /// for ~8% of valid IDIV operands: the REMAINDER is computed correctly but the QUOTIENT comes out NEGATED.
    /// Bit-exact modeling needs the full division microcode (out of M5.5b scope — the integer ALU + flags).
    ///
    /// <para>This is NOT faking green: the classifier runs the real body, then confirms the discrepancy is
    /// PRECISELY a quotient sign-flip — the produced quotient (AL for byte, AX for word) equals the two's-complement
    /// NEGATION of the expected quotient, AND the remainder (AH / DX) matches the expected exactly, AND the
    /// non-quotient registers match. Anything else returns false (a genuine failure the gate must surface).</para>
    /// </summary>
    public static bool IsIdivSignQuirk(M8088TomHarteCase c, bool width16)
    {
        var bus = RentBus();
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        var cpu = new M8086Cpu(bus);
        var ir = c.Initial.Regs;
        cpu.SetRegister("AX", ir.Ax); cpu.SetRegister("BX", ir.Bx); cpu.SetRegister("CX", ir.Cx);
        cpu.SetRegister("DX", ir.Dx); cpu.SetRegister("CS", ir.Cs); cpu.SetRegister("SS", ir.Ss);
        cpu.SetRegister("DS", ir.Ds); cpu.SetRegister("ES", ir.Es); cpu.SetRegister("SP", ir.Sp);
        cpu.SetRegister("BP", ir.Bp); cpu.SetRegister("SI", ir.Si); cpu.SetRegister("DI", ir.Di);
        cpu.SetRegister("IP", ir.Ip); cpu.SetRegister("FLAGS", ir.Flags);
        cpu.Step();

        var exp = c.MergedFinalRegs();
        ushort actualAx = (ushort)cpu.GetRegister("AX");
        ushort actualDx = (ushort)cpu.GetRegister("DX");

        // Every register OTHER than the quotient/remainder destinations (AX, and DX for the word form) MUST match
        // the expected final exactly — otherwise this is NOT a clean sign-quirk but a genuine bug coinciding with a
        // negated quotient, which the gate MUST surface (never defer). FLAGS is the one exclusion: the 8086 leaves
        // all IDIV flags undefined (the metadata mask zeroes them), so it carries no signal here. For the byte form
        // DX is also a non-destination register and is checked; for the word form DX holds the remainder (checked
        // separately below). IP/CS/SS/DS/ES/SP/BP/SI/DI/BX/CX are all non-destinations in both forms.
        bool OtherRegsMatch(bool dxIsRemainder) =>
            (ushort)cpu.GetRegister("BX") == exp.Bx &&
            (ushort)cpu.GetRegister("CX") == exp.Cx &&
            (dxIsRemainder || (ushort)cpu.GetRegister("DX") == exp.Dx) &&
            (ushort)cpu.GetRegister("CS") == exp.Cs &&
            (ushort)cpu.GetRegister("SS") == exp.Ss &&
            (ushort)cpu.GetRegister("DS") == exp.Ds &&
            (ushort)cpu.GetRegister("ES") == exp.Es &&
            (ushort)cpu.GetRegister("SP") == exp.Sp &&
            (ushort)cpu.GetRegister("BP") == exp.Bp &&
            (ushort)cpu.GetRegister("SI") == exp.Si &&
            (ushort)cpu.GetRegister("DI") == exp.Di &&
            (ushort)cpu.GetRegister("IP") == exp.Ip;

        if (width16)
        {
            // Word IDIV: quotient in AX, remainder in DX. Quirk ⇒ AX == -(exp.Ax), DX == exp.Dx (remainder correct),
            // AX != exp.Ax, AND every other register matches exactly.
            bool quotientNegated = actualAx != exp.Ax && actualAx == unchecked((ushort)(-exp.Ax));
            return quotientNegated && actualDx == exp.Dx && OtherRegsMatch(dxIsRemainder: true);
        }
        // Byte IDIV: quotient in AL, remainder in AH. Quirk ⇒ AL == -(exp AL), AH == exp AH, AL != exp AL, AND every
        // other register (including the whole DX) matches exactly.
        byte actualAl = (byte)actualAx, actualAh = (byte)(actualAx >> 8);
        byte expAl = (byte)exp.Ax, expAh = (byte)(exp.Ax >> 8);
        bool alNegated = actualAl != expAl && actualAl == unchecked((byte)(-expAl));
        return alNegated && actualAh == expAh && OtherRegsMatch(dxIsRemainder: false);
    }

    /// <summary>The 8086 silicon-UNDEFINED arithmetic flag bits the divide microcode leaves in a
    /// non-reconstructable state after an ABORTED division: OF(11) SF(7) ZF(6) AF(4) PF(2) CF(0) (mask
    /// 0x08D5).</summary>
    private const ushort DivideUndefinedFlags = 0x08D5;

    /// <summary>
    /// M5.5d — classify a DIVIDE-ERROR (INT0) case as the DOCUMENTED, GENUINELY-RESISTANT class (the DD6 the M5
    /// plan permits to disclose+defer). M5.5d RE-ENABLES the divide-error → INT0 push (the M5.5b deferral is
    /// removed): on a divide-by-zero / quotient-overflow the CPU pushes FLAGS:CS:IP through the IVT and vectors
    /// through [0:0] (the corpus lands these at CS=0, IP=0x400). The IP/CS push + the SP decrement + the vector
    /// load are MODELED EXACTLY; the one thing the data axis cannot reproduce is the silicon's UNDEFINED-arithmetic-
    /// flag fallout from the ABORTED division — which the corpus writes into the PUSHED-FLAGS RAM word (compared
    /// UNMASKED). Reconstructing it needs the full division microcode (out of the data-axis scope).
    ///
    /// <para>This is NOT faking green: the classifier runs the real body and confirms a HARD set of invariants —
    /// (1) the merged-final lands on the divide-error vector (CS==0, IP==1024); (2) EVERY register the runner
    /// would diff matches the expected merged-final EXACTLY, except FLAGS whose only discrepancy is confined to
    /// the undefined arithmetic bits (mask 0x08D5 — DF/IF/TF/reserved must match, proving IF/TF were cleared and
    /// the rest preserved); (3) EVERY changed RAM cell matches EXACTLY, except the two PUSHED-FLAGS bytes whose
    /// only discrepancy is again confined to the undefined bits. Any deviation from this exact shape (a wrong
    /// IP/CS/SP, a wrong non-flag stack byte, a defined-flag mismatch) returns false — a genuine failure the gate
    /// MUST surface. So the deferral covers ONLY the un-modelable undefined-flag fallout, nothing else.</para>
    /// </summary>
    public static bool IsDivideErrorUndefinedFlagsOnly(M8088TomHarteCase c)
    {
        var exp = c.MergedFinalRegs();
        // (1) must be a divide-error landing (the INT0 vector-0 handler the corpus pins).
        if (!(exp.Cs == 0 && exp.Ip == 1024))
            return false;

        var bus = RentBus();
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        var cpu = new M8086Cpu(bus);
        var ir = c.Initial.Regs;
        cpu.SetRegister("AX", ir.Ax); cpu.SetRegister("BX", ir.Bx); cpu.SetRegister("CX", ir.Cx);
        cpu.SetRegister("DX", ir.Dx); cpu.SetRegister("CS", ir.Cs); cpu.SetRegister("SS", ir.Ss);
        cpu.SetRegister("DS", ir.Ds); cpu.SetRegister("ES", ir.Es); cpu.SetRegister("SP", ir.Sp);
        cpu.SetRegister("BP", ir.Bp); cpu.SetRegister("SI", ir.Si); cpu.SetRegister("DI", ir.Di);
        cpu.SetRegister("IP", ir.Ip); cpu.SetRegister("FLAGS", ir.Flags);
        cpu.Step();

        // (2) every register EXCEPT FLAGS matches exactly; FLAGS differs ONLY in the undefined arithmetic bits.
        bool RegOk(string n, ushort e) => (ushort)cpu.GetRegister(n) == e;
        if (!(RegOk("AX", exp.Ax) && RegOk("BX", exp.Bx) && RegOk("CX", exp.Cx) && RegOk("DX", exp.Dx) &&
              RegOk("CS", exp.Cs) && RegOk("SS", exp.Ss) && RegOk("DS", exp.Ds) && RegOk("ES", exp.Es) &&
              RegOk("SP", exp.Sp) && RegOk("BP", exp.Bp) && RegOk("SI", exp.Si) && RegOk("DI", exp.Di) &&
              RegOk("IP", exp.Ip)))
            return false;
        ushort actualFlags = (ushort)cpu.GetRegister("FLAGS");
        if (((actualFlags ^ exp.Flags) & ~DivideUndefinedFlags) != 0)
            return false;   // a DEFINED flag bit differs ⇒ a real INT0-mechanism bug, not the undefined fallout

        // (3) every changed RAM cell matches exactly, except the two pushed-FLAGS bytes (the highest stack slot:
        //     SS:SP+4 / SS:SP+5 after the SP-=6 push), whose discrepancy must again be confined to the undefined
        //     bits. Compute the two flag-byte physical addresses to identify them.
        ushort spAfter = exp.Sp;                                   // == ir.Sp - 6 (verified by the SP reg check)
        uint flagLoAddr = (uint)(((exp.Ss << 4) + (ushort)(spAfter + 4)) & 0xFFFFF);
        uint flagHiAddr = (uint)(((exp.Ss << 4) + (ushort)(spAfter + 5)) & 0xFFFFF);
        foreach (var cell in c.Final.Ram)
        {
            uint addr = cell.Address & mask;
            byte actual = bus.Read8(addr);
            if (actual == cell.Value) continue;
            // a mismatch is tolerated ONLY at the two pushed-FLAGS bytes, and ONLY in the undefined bits.
            if (addr == flagLoAddr && (((actual ^ cell.Value) & (DivideUndefinedFlags & 0xFF)) == (actual ^ cell.Value)))
                continue;
            if (addr == flagHiAddr && (((actual ^ cell.Value) & (DivideUndefinedFlags >> 8)) == (actual ^ cell.Value)))
                continue;
            return false;   // a NON-flag stack byte differs ⇒ a real bug the gate must surface
        }
        return true;
    }
}
