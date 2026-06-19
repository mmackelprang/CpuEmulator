using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-C: the 8086 integer-ALU emit arm + the FLAGS word are LIVE (not all-fallback) and DISPATCH,
/// and the emitted FLAGS computation is BYTE-IDENTICAL to the interpreter oracle (M8086Cpu.Alu.cs). The headline
/// gate is the full 8088 TomHarte JIT sweep; this file pins the non-vacuity + the densest correctness pocket
/// (AF half-carry, OF signed-overflow for ADD vs SUB, CF-preserve on INC/DEC, parity, ADC carry-in, the
/// logical CF/OF clear, NEG, NOT-no-flags) independent of the broad sweep, comparing the FULL FLAGS word (all
/// six defined bits — no mask) of a JIT run against a fresh interpreter stepped from the SAME initial state.</summary>
public class M8086AluEmitTests
{
    // A CPU over a fully-mapped 1 MB little-endian space, code + data at (CS<<4)+IP.
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

    /// <summary>5a: an ALU block emits real IL (the ALU row is NOT a fallback) AND the arm actually DISPATCHED
    /// (M8086AluEmitSelections &gt; 0) — the un-fakeable non-vacuity proof.</summary>
    [Fact]
    public void Alu_block_emits_no_fallback_and_dispatches_the_arm()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        // 04 05 = ADD AL,5  at CS=0x1000, IP=0x0020. A pure-register ALU, then a trailing NOP ends discovery.
        var (c, _, _) = Make(0x1000, 0x0020, 0x04, 0x05, 0x90);
        _ = c.Compile(0x0020);
        Assert.Equal(1, c.FallbackEmitCount);          // ONLY the trailing NOP fell back; the ADD emitted real IL
        Assert.True(c.M8086AluEmitSelections > 0,      // ... and the ALU arm actually dispatched (non-vacuous)
            "EmitM8086Alu was never selected — the ALU gate-flip / dispatch route is not wired.");
    }

    /// <summary>The NEGATIVE control — a block of only a fallback op (NOP) selects the ALU arm zero times, so the
    /// positive case above is meaningful (the counter is not always-tripping).</summary>
    [Fact]
    public void Non_ALU_block_selects_the_ALU_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (c, _, _) = Make(0x1000, 0x0020, 0x90);   // NOP only — falls back, no ALU row
        _ = c.Compile(0x0020);
        Assert.Equal(0, c.M8086AluEmitSelections);
    }

    // ─────────────────────────── 5c: flag-exactness vs the interpreter oracle ───────────────────────────

    /// <summary>Drive one ALU instruction through JittedCpu&lt;M8086Cpu&gt; AND through a fresh interpreter
    /// (M8086Cpu.Step) from the SAME initial 16-bit register seeds + FLAGS, then assert the FULL FLAGS word
    /// matches (all six defined bits, no mask) AND the asserted register matches. The trailing NOP only ends the
    /// JIT block (a fallback inner.Step that touches neither FLAGS nor the ALU register), so the comparison after
    /// the ADD/SUB/etc is exact.</summary>
    private static void AssertAluFlagsMatchOracle(
        ushort initialFlags, (string Name, ushort Value)[] seeds, string assertReg, params byte[] aluCode)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        const ushort cs = 0x2000, ip = 0x0000;
        byte[] withTerminator = [.. aluCode, 0x90];   // trailing NOP ends the JIT block (changes no flags/regs)

        // ── the JIT run ──
        var (_, jitCpu, jitBus) = Make(cs, ip, withTerminator);
        jitCpu.SetRegister("FLAGS", initialFlags);
        foreach (var (n, v) in seeds) jitCpu.SetRegister(n, v);
        var jit = new JittedCpu<M8086Cpu>(jitCpu, M8086Cpu.JitTarget, jitBus);
        long budget = 1; jit.Run(ref budget);

        // ── the interpreter oracle: a fresh CPU, same seeds, ONE Step of the SAME ALU instruction ──
        var interp = NewInterp(out _, cs, ip, aluCode);
        interp.SetRegister("FLAGS", initialFlags);
        foreach (var (n, v) in seeds) interp.SetRegister(n, v);
        interp.Step();

        Assert.Equal(interp.GetRegister("FLAGS"), jitCpu.GetRegister("FLAGS"));      // the full FLAGS word — exact
        Assert.Equal(interp.GetRegister(assertReg), jitCpu.GetRegister(assertReg));  // the affected register
    }

    /// <summary>ADD AL,1 with AL=0x7F: carry out of bit 3 (AF set) + signed overflow 0x7F→0x80 (OF set) + SF set,
    /// CF clear, ZF clear. The classic AF/OF half-carry/overflow edge.</summary>
    [Fact]
    public void Add_AF_and_OF_edge() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x007F)], "AX", 0x04, 0x01);   // ADD AL,1

    /// <summary>SUB AL,1 with AL=0x00: borrow (CF set) + AF set (borrow out of bit 3), result 0xFF (SF set).</summary>
    [Fact]
    public void Sub_borrow_CF_and_AF_edge() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0000)], "AX", 0x2C, 0x01);   // SUB AL,1

    /// <summary>ADD AL,imm to a known EVEN-parity byte (result 0x03 = two set bits ⇒ PF set).</summary>
    [Fact]
    public void Parity_even_sets_PF() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0001)], "AX", 0x04, 0x02);   // ADD AL,2 -> 0x03 (PF set)

    /// <summary>ADD AL,imm to a known ODD-parity byte (result 0x07 = three set bits ⇒ PF clear).</summary>
    [Fact]
    public void Parity_odd_clears_PF() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0003)], "AX", 0x04, 0x04);   // ADD AL,4 -> 0x07 (PF clear)

    /// <summary>A WORD ADD that carries out of bit 15: ADD AX,1 with AX=0x7FFF → 0x8000 (OF+SF set, AF set).</summary>
    [Fact]
    public void Word_add_overflow_edge() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x7FFF)], "AX", 0x05, 0x01, 0x00);   // ADD AX,1

    /// <summary>ADC AL,0 with carry-in set (FLAGS CF=1): 0x0F + 0 + 1 = 0x10 — the carry-in must feed the sum
    /// (and AF from the bit-3 carry). Seeds FLAGS with CF set.</summary>
    [Fact]
    public void Adc_consumes_the_carry_in() =>
        AssertAluFlagsMatchOracle(0x0001 /*CF set*/, [("AX", 0x000F)], "AX", 0x14, 0x00);   // ADC AL,0

    /// <summary>SBB AL,0 with borrow-in set (CF=1): 0x10 - 0 - 1 = 0x0F — the borrow-in feeds the difference.</summary>
    [Fact]
    public void Sbb_consumes_the_borrow_in() =>
        AssertAluFlagsMatchOracle(0x0001 /*CF set*/, [("AX", 0x0010)], "AX", 0x1C, 0x00);   // SBB AL,0

    /// <summary>AND AL,imm clears CF and OF (LogicFlags): seed CF+OF set, AND AL,0x0F, assert both cleared and
    /// SZP from the result.</summary>
    [Fact]
    public void And_clears_CF_and_OF() =>
        AssertAluFlagsMatchOracle(0x0801 /*CF+OF set*/, [("AX", 0x00FF)], "AX", 0x24, 0x0F);   // AND AL,0x0F

    /// <summary>OR AL,imm — another logical, CF/OF cleared, SZP from the result.</summary>
    [Fact]
    public void Or_clears_CF_and_OF() =>
        AssertAluFlagsMatchOracle(0x0801 /*CF+OF set*/, [("AX", 0x0010)], "AX", 0x0C, 0x21);   // OR AL,0x21

    /// <summary>XOR AL,imm — logical, CF/OF cleared.</summary>
    [Fact]
    public void Xor_clears_CF_and_OF() =>
        AssertAluFlagsMatchOracle(0x0801 /*CF+OF set*/, [("AX", 0x00AA)], "AX", 0x34, 0xFF);   // XOR AL,0xFF

    /// <summary>CMP is SUB with the result discarded — the register must be UNCHANGED, only FLAGS set. CMP AL,1
    /// with AL=0x00 (the SUB-borrow edge) and the AL register asserted unchanged.</summary>
    [Fact]
    public void Cmp_sets_flags_without_writing_back() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0000)], "AX", 0x3C, 0x01);   // CMP AL,1

    /// <summary>INC r16 PRESERVES CF (INC/DEC do NOT touch carry). Seed CF set, INC CX, assert CF still set in the
    /// full FLAGS word + the other flags from the +1 result.</summary>
    [Fact]
    public void Inc_reg16_preserves_CF() =>
        AssertAluFlagsMatchOracle(0x0001 /*CF set*/, [("CX", 0x00FF)], "CX", 0x41);   // INC CX

    /// <summary>DEC r16 also preserves CF. Seed CF set, DEC CX from 0x0000 → 0xFFFF (SF set), CF preserved.</summary>
    [Fact]
    public void Dec_reg16_preserves_CF() =>
        AssertAluFlagsMatchOracle(0x0001 /*CF set*/, [("CX", 0x0000)], "CX", 0x49);   // DEC CX

    /// <summary>NEG r/m8 — 0 - operand (SUB-form flags). NEG AL with AL=0x01 → 0xFF; CF set (operand != 0),
    /// SF set, AF set.</summary>
    [Fact]
    public void Neg_uses_sub_form_flags() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0001)], "AX", 0xF6, 0xD8);   // F6 /3 (mod=11 rm=000 AL) NEG AL

    /// <summary>NOT r/m8 sets NO flags — seed a distinctive FLAGS word, NOT AL, assert FLAGS is UNCHANGED and the
    /// register is the bitwise complement.</summary>
    [Fact]
    public void Not_sets_no_flags() =>
        AssertAluFlagsMatchOracle(0x0895 /*CF+PF+AF+SF+OF set*/, [("AX", 0x0055)], "AX", 0xF6, 0xD0);   // F6 /2 NOT AL

    /// <summary>The 80/81/83 group: a reg-field-selected op (CMP r/m8,imm8 via 0x80 /7). 80 F8 05 = CMP AL,5 —
    /// reg=7 ⇒ CMP; AL=0x03 borrows. Pins the (key &amp; 7) → op map and the group-imm path.</summary>
    [Fact]
    public void Group_imm_cmp_selects_op_from_reg_field() =>
        AssertAluFlagsMatchOracle(0x0000, [("AX", 0x0003)], "AX", 0x80, 0xF8, 0x05);   // CMP AL,5

    /// <summary>0x83 sign-extends imm8→16: ADD CX,-1 (83 C1 FF) with CX=0x0001 → 0x0000 (ZF set, CF set from the
    /// 0xFFFF add). Pins the sign-extension in the group path.</summary>
    [Fact]
    public void Group_imm_0x83_sign_extends() =>
        AssertAluFlagsMatchOracle(0x0000, [("CX", 0x0001)], "CX", 0x83, 0xC1, 0xFF);   // ADD CX,-1 (83 /0 imm8 SX)

    /// <summary>TEST r/m,reg (84/85) — AND, flags only, no write-back. TEST AL,AL with AL=0x00 → ZF set, CF/OF
    /// cleared, the register unchanged.</summary>
    [Fact]
    public void Test_rm_reg_flags_only() =>
        AssertAluFlagsMatchOracle(0x0801 /*CF+OF set*/, [("AX", 0x0000)], "AX", 0x84, 0xC0);   // TEST AL,AL

    /// <summary>A memory-dest RMW ALU: ADD byte [BX], AL. Pins the address-once read-compute-write to the SAME
    /// EA and the SMC-guard arming. Seeds BX + a data byte, asserts FLAGS + the written RAM byte.</summary>
    [Fact]
    public void Add_to_memory_rmw_writes_back_and_sets_flags()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000;
        // 00 07 = ADD [BX], AL  (mod=00, reg=000(AL), rm=111(BX)). DS default. + trailing NOP.
        byte[] code = [0x00, 0x07, 0x90];

        var (_, jitCpu, jitBus) = Make(cs, ip, code);
        jitCpu.SetRegister("FLAGS", 0x0000);
        jitCpu.SetRegister("DS", 0x3000);
        jitCpu.SetRegister("BX", 0x0040);
        jitCpu.SetRegister("AX", 0x007F);   // AL = 0x7F
        uint dataPhys = (uint)(((0x3000 << 4) + 0x0040) & 0xFFFFF);
        jitBus.Write8(dataPhys, 0x01);      // [BX] = 0x01  -> 0x01 + 0x7F = 0x80 (AF+OF+SF)
        var jit = new JittedCpu<M8086Cpu>(jitCpu, M8086Cpu.JitTarget, jitBus);
        long budget = 1; jit.Run(ref budget);

        var interp = NewInterp(out var iBus, cs, ip, [0x00, 0x07]);
        interp.SetRegister("FLAGS", 0x0000);
        interp.SetRegister("DS", 0x3000);
        interp.SetRegister("BX", 0x0040);
        interp.SetRegister("AX", 0x007F);
        iBus.Write8(dataPhys, 0x01);
        interp.Step();

        Assert.Equal(interp.GetRegister("FLAGS"), jitCpu.GetRegister("FLAGS"));
        Assert.Equal(iBus.Read8(dataPhys), jitBus.Read8(dataPhys));   // the RMW write-back to the SAME EA
        Assert.Equal((byte)0x80, jitBus.Read8(dataPhys));             // 0x7F + 0x01
    }
}
