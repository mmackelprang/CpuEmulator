using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-D: the 8086 NEAR control-flow emit arm (Jcc / JMP / CALL / RET / LOOP + FF /2 /4 indirect) is LIVE
/// (not all-fallback) and DISPATCHES, the emitted IP/SP/stack-RAM/CX are BYTE-IDENTICAL to the interpreter oracle
/// (M8086Cpu.Control.cs + Stack.cs), the STATIC targets CHAIN (a near JMP/Jcc transfers via the chain table, not a
/// dispatcher round-trip), and the DYNAMIC targets (RET pop, FF indirect) exit. The headline gate is the full 8088
/// TomHarte JIT sweep over the control-flow opcode files; this file pins the non-vacuity + the densest correctness
/// pockets (the Jcc taken/not-taken/boundary three-outcome, the CALL/RET SS:SP round-trip, the LOOP CX-count, and
/// the chain-hit probe) independent of the broad sweep, comparing a JIT run against a fresh interpreter stepped from
/// the SAME initial state. The far forms (9A/EA/CB/CA + FF /3 /5) stay interpreter-fallback (DECISION D-1).</summary>
public class M8086FlowEmitTests
{
    // A CPU over a fully-mapped 1 MB little-endian space, code + data at (CS<<4)+IP. Mirrors M8086AluEmitTests.Make.
    private static (BlockCompiler<M8086Cpu> C, M8086Cpu Cpu, AddressSpace Bus) Make(ushort cs, ushort ip, params byte[] code)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", cs); cpu.SetRegister("IP", ip);
        var opts = new JitOptions();
        return (new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus, new Fastmem(bus, opts), opts), cpu, bus);
    }

    private static M8086Cpu NewInterp(out AddressSpace bus, ushort cs, ushort ip, params byte[] code)
    {
        bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (uint i = 0; i < code.Length; i++) bus.Write8((phys + i) & 0xFFFFF, code[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", cs); cpu.SetRegister("IP", ip);
        return cpu;
    }

    // ─────────────────────────── 5a: non-vacuity ───────────────────────────

    /// <summary>5a: a NEAR flow block emits real IL (the flow row is NOT a fallback) AND the arm actually DISPATCHED
    /// (M8086FlowEmitSelections &gt; 0) — the un-fakeable non-vacuity proof. A JMP rel8 self-loop (EB FE) is one
    /// block that ENDS at the JMP, so FallbackEmitCount == 0 (nothing fell back).</summary>
    [Fact]
    public void Flow_block_emits_no_fallback_and_dispatches_the_arm()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        // EB FE = JMP $-2 (self-loop) at CS=0x1000, IP=0x0020. One in-scope near-flow op; the block ends at the JMP.
        var (c, _, _) = Make(0x1000, 0x0020, 0xEB, 0xFE);
        _ = c.Compile(0x0020);
        Assert.Equal(0, c.FallbackEmitCount);          // the JMP emitted real IL — NOTHING fell back
        Assert.True(c.M8086FlowEmitSelections > 0,     // ... and the flow arm actually dispatched (non-vacuous)
            "EmitM8086Flow was never selected — the flow gate-flip / dispatch route is not wired.");
    }

    /// <summary>The NEGATIVE control — a block of only a fallback op (a far JMP, 0xEA, which stays interpreter) selects
    /// the flow arm zero times, so the positive case is meaningful (the counter is not always-tripping).</summary>
    [Fact]
    public void Far_jump_block_selects_the_flow_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // EA 00 00 00 20 = JMP 0x2000:0x0000 (far direct) — DECISION D-1 keeps it fallback (CS-changing).
        var (c, _, _) = Make(0x1000, 0x0020, 0xEA, 0x00, 0x00, 0x00, 0x20);
        _ = c.Compile(0x0020);
        Assert.Equal(0, c.M8086FlowEmitSelections);    // a far flow op never reaches EmitM8086Flow (gate excludes it)
        Assert.Equal(1, c.FallbackEmitCount);          // it fell back (the far op is the oracle via inner.Step)
    }

    // ─────────────────────────── parity vs the interpreter oracle ───────────────────────────

    /// <summary>Drive a SINGLE flow block through JittedCpu&lt;M8086Cpu&gt; AND through a fresh interpreter from the
    /// SAME seeds, then assert the chosen registers match. One block runs (budget 1) — the flow op ends the block, so
    /// the JIT and interpreter stop at the same successor. The interpreter is the oracle.</summary>
    private static void AssertFlowMatchesOracle(
        ushort cs, ushort ip, (string Name, ushort Value)[] seeds, string[] assertRegs, params byte[] code)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        // ── the JIT run: a JittedCpu over a fresh bus seeded with the code ──
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) inner.SetRegister(n, v);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
        long budget = 1; jit.Run(ref budget);

        // ── the interpreter oracle: a fresh CPU, same seeds, ONE Step of the SAME flow instruction ──
        var interp = NewInterp(out _, cs, ip, code);
        foreach (var (n, v) in seeds) interp.SetRegister(n, v);
        interp.Step();

        foreach (var reg in assertRegs)
            Assert.Equal(interp.GetRegister(reg), inner.GetRegister(reg));
    }

    /// <summary>Jcc TAKEN: JE $+4 with ZF set lands IP at the taken target. (74 02 = JE rel8 +2.)</summary>
    [Fact]
    public void Jcc_taken_lands_at_the_target() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("FLAGS", 0x0040 /*ZF set*/)], ["IP"], 0x74, 0x02);

    /// <summary>Jcc NOT-TAKEN: JE $+4 with ZF clear falls through to IP = pc+2.</summary>
    [Fact]
    public void Jcc_not_taken_falls_through() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("FLAGS", 0x0000 /*ZF clear*/)], ["IP"], 0x74, 0x02);

    /// <summary>Jcc BOUNDARY: a BACKWARD rel8 (74 FC = JE $-2) with ZF set — the sign-extended negative displacement
    /// lands IP behind the instruction. Pins the (sbyte) sign-extension of the rel8 base.</summary>
    [Fact]
    public void Jcc_taken_backward_branch_sign_extends() =>
        AssertFlowMatchesOracle(0x2000, 0x0010, [("FLAGS", 0x0040 /*ZF set*/)], ["IP"], 0x74, 0xFC);

    /// <summary>A compound Jcc condition: JLE ($7E) = ZF | (SF != OF). Seed SF set, OF clear, ZF clear → SF!=OF true →
    /// taken. Pins the SF!=OF composition (EmitM8086SfNeOf).</summary>
    [Fact]
    public void Jle_taken_via_sf_ne_of() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("FLAGS", 0x0080 /*SF set, OF clear, ZF clear*/)], ["IP"], 0x7E, 0x05);

    /// <summary>JMP rel16 (E9): a near unconditional jump with a 16-bit displacement. Lands IP at pc+3+rel16.</summary>
    [Fact]
    public void Jmp_rel16_lands_at_the_target() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [], ["IP"], 0xE9, 0x34, 0x12);   // JMP $+0x1237

    /// <summary>JMP rel8 (EB) backward — sign-extended negative rel8.</summary>
    [Fact]
    public void Jmp_rel8_backward() =>
        AssertFlowMatchesOracle(0x2000, 0x0020, [], ["IP"], 0xEB, 0xF0);   // JMP $-0x0E

    /// <summary>LOOP (E2): CX -= 1; if CX != 0, jump. Seed CX=3 → CX becomes 2 (≠0) → taken. Asserts BOTH IP (taken
    /// target) AND CX (the side-effecting decrement).</summary>
    [Fact]
    public void Loop_decrements_cx_and_takes_when_nonzero() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("CX", 0x0003)], ["IP", "CX"], 0xE2, 0xFE);   // LOOP $-0

    /// <summary>LOOP (E2) NOT taken: CX=1 → CX becomes 0 → not taken (fall through). CX must still be decremented to 0
    /// (the side effect happens regardless of the predicate).</summary>
    [Fact]
    public void Loop_not_taken_still_decrements_cx() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("CX", 0x0001)], ["IP", "CX"], 0xE2, 0xFE);

    /// <summary>JCXZ (E3): jump if CX == 0; CX is NOT decremented. Seed CX=0 → taken; CX must be UNCHANGED.</summary>
    [Fact]
    public void Jcxz_taken_when_cx_zero_does_not_decrement() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("CX", 0x0000)], ["IP", "CX"], 0xE3, 0x05);

    /// <summary>LOOPE (E1): CX -= 1; taken iff CX != 0 AND ZF. Seed CX=2, ZF set → CX becomes 1 (≠0) and ZF set →
    /// taken.</summary>
    [Fact]
    public void Loope_takes_when_cx_nonzero_and_zf() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("CX", 0x0002), ("FLAGS", 0x0040 /*ZF*/)], ["IP", "CX"], 0xE1, 0x05);

    // ─────────────────────────── CALL / RET round-trip through SS:SP ───────────────────────────

    /// <summary>CALL rel16 (E8) pushes the return IP onto SS:SP then jumps; assert the JIT lands the same IP + SP and
    /// writes the same return-address word to the stack RAM as the interpreter. A single block (the CALL ends it).</summary>
    [Fact]
    public void Call_rel16_pushes_return_ip_and_jumps()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        byte[] code = [0xE8, 0x34, 0x12];   // CALL $+0x1237 (near)

        // ── the JIT run ──
        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8((cphys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        // ── the interpreter oracle ──
        var interp = NewInterp(out var ibus, cs, ip, code);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // landed at the call target
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP decremented by 2
        // the pushed return-address word in SS:SP RAM (the new SP, post-decrement):
        uint stackPhys = (uint)(((ss << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        Assert.Equal(ibus.Read8(stackPhys), jbus.Read8(stackPhys));               // low byte
        Assert.Equal(ibus.Read8((stackPhys + 1) & 0xFFFFF), jbus.Read8((stackPhys + 1) & 0xFFFFF));   // high byte
        Assert.Equal((byte)0x03, jbus.Read8(stackPhys));   // the return IP (pc+3) low byte == 0x03
    }

    /// <summary>The full CALL → RET round-trip: a near CALL into a near RET must return to the instruction AFTER the
    /// CALL with SP restored, the popped IP coming from the stack RAM (a DYNAMIC target — RET exits to the
    /// dispatcher). Driven for two blocks; asserts final IP + SP match the interpreter.</summary>
    [Fact]
    public void Call_then_ret_round_trips_through_the_stack()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ss = 0x3000, sp = 0x0100;
        // $0000: E8 03 00   CALL $0006 (near, returns to $0003)
        // $0003: EB FE      JMP $-2 (park — never reached past the RET path here; it is the return landing pad +loop)
        // $0006: C3         RET (pops the return IP $0003)
        byte[] code = new byte[0x0010];
        code[0x00] = 0xE8; code[0x01] = 0x03; code[0x02] = 0x00;   // CALL $0006
        code[0x03] = 0xEB; code[0x04] = 0xFE;                       // JMP $-2 (park at $0003)
        code[0x06] = 0xC3;                                          // RET

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)((cs << 4) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8((cphys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", 0x0000);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 4; jit.Run(ref budget);   // CALL block, RET block, then the JMP-self park

        var interp = NewInterp(out _, cs, 0x0000, code);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        long ibudget = 4;
        for (int i = 0; i < 4; i++) interp.Step();

        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP restored after the RET
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // landed back at $0003 (the park loop)
        _ = ibudget;
    }

    /// <summary>FF /4 JMP r/m16 near indirect: the operand IS the new IP (a DYNAMIC target → exit). FF E3 = JMP BX
    /// (mod=11, reg=4, rm=3=BX). Seed BX, assert the JIT lands IP = BX exactly like the interpreter.</summary>
    [Fact]
    public void Ff_jmp_indirect_register_sets_ip_from_the_operand() =>
        AssertFlowMatchesOracle(0x2000, 0x0000, [("BX", 0x1234)], ["IP"], 0xFF, 0xE3);   // JMP BX

    /// <summary>FF /2 CALL r/m16 near indirect with the SP-quirk: FF D4 = CALL SP (mod=11, reg=2, rm=4=SP). The oracle
    /// reads the target (= SP) BEFORE PushWord decrements SP, so the new IP is the PRE-push SP, not the post-decrement
    /// SP. Pins the read-target-before-push ordering (the TomHarte "call sp" cases). Asserts IP + SP AND the pushed
    /// return-address word in SS:SP RAM — the subtlest FF /2 invariant is that the return address (= fallThrough =
    /// pc+length) lands on the stack while the jumped-to target stays the PRE-push SP.</summary>
    [Fact]
    public void Ff_call_indirect_sp_reads_target_before_the_push()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        byte[] code = [0xFF, 0xD4];   // CALL SP (mod=11, reg=2=/2 CALL near, rm=4=SP)

        // ── the JIT run ──
        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8((cphys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        // ── the interpreter oracle ──
        var interp = NewInterp(out var ibus, cs, ip, code);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // IP = the PRE-push SP (the call-sp quirk)
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP decremented by 2
        // the pushed return-address word in SS:SP RAM (the new SP, post-decrement) — must equal fallThrough = pc+length:
        uint stackPhys = (uint)(((ss << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        Assert.Equal(ibus.Read8(stackPhys), jbus.Read8(stackPhys));               // low byte matches the oracle
        Assert.Equal(ibus.Read8((stackPhys + 1) & 0xFFFFF), jbus.Read8((stackPhys + 1) & 0xFFFFF));   // high byte matches
        const ushort fallThrough = ip + 2;   // pc + length(FF D4) = $0002
        Assert.Equal((byte)(fallThrough & 0xFF), jbus.Read8(stackPhys));                       // return IP low byte
        Assert.Equal((byte)(fallThrough >> 8), jbus.Read8((stackPhys + 1) & 0xFFFFF));         // return IP high byte
    }

    // ─────────────────────────── chaining: a near STATIC target chains ───────────────────────────

    /// <summary>A near JMP rel16 to a static target CHAINS to the target block (the static-target chain edge), not a
    /// dispatcher round-trip — proven by ChainStepCount &gt; 0 across the two blocks, with CompileCount == 2 (each
    /// block compiled once). Mirrors ChainingTests.Jmp_abs_chains_... for the 8086.</summary>
    [Fact]
    public void Near_jmp_chains_to_its_static_target()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000;
        // $0000: E9 03 00   JMP $0006 (block A: rel16 +3 from pc+3=$0003 → $0006). Chains to $0006.
        // $0006: EB FE      JMP $-2 (block B: self-loop via the chain).
        byte[] code = new byte[0x0010];
        code[0x00] = 0xE9; code[0x01] = 0x03; code[0x02] = 0x00;   // JMP $0006
        code[0x06] = 0xEB; code[0x07] = 0xFE;                       // JMP $-2 (self-loop)

        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)((cs << 4) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((cphys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", 0x0000);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
        long budget = 200; jit.Run(ref budget);

        Assert.Equal(2, jit.CompileCount);                 // both blocks compiled exactly once
        Assert.True(jit.ChainStepCount > 0,                // control transferred via the chain, not the dispatcher
            "the near JMP did not chain to its static target.");
        Assert.Equal((ulong)0x0006, inner.GetRegister("IP"));   // parked in block B
    }

    /// <summary>RET is a DYNAMIC target (the popped stack word) — it does NOT chain (it exits to the dispatcher). The
    /// CALL→RET program chains on the CALL (static entry) but adds NO chain step on the RET. Mirrors
    /// ChainingTests.Rts_does_not_chain for the 8086.</summary>
    [Fact]
    public void Ret_does_not_chain()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ss = 0x3000, sp = 0x0100;
        // $0000: E8 03 00 CALL $0006 (chains to $0006);  $0003: EB FE JMP-self park;  $0006: C3 RET (dynamic).
        byte[] code = new byte[0x0010];
        code[0x00] = 0xE8; code[0x01] = 0x03; code[0x02] = 0x00;
        code[0x03] = 0xEB; code[0x04] = 0xFE;
        code[0x06] = 0xC3;

        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)((cs << 4) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((cphys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", 0x0000);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);

        long budget = 1; jit.Run(ref budget);          // the CALL block — chains to $0006 (static), then runs RET
        long afterCallChains = jit.ChainStepCount;     // includes the CALL->$0006 chain
        budget = 1; jit.Run(ref budget);               // the RET block — must NOT add a chain step (dynamic target)
        Assert.Equal(afterCallChains, jit.ChainStepCount);
    }
}
