using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;   // BlockCompiler<>, JittedCpu<>, Fastmem, JitOptions
using CpuEmulator.Tests.TomHarte;   // M68000TomHarteTheory, the loader/runner/corpus (smoke fact)
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M4.6 genericity pins: the generated per-CPU <see cref="IJitTarget"/> seam resolves the 68000's
/// CPU-typed handles by name — including the new hand-written <c>AdvanceCycles</c> charge seam (GAP 1) — and
/// the generic <c>BlockCompiler&lt;M68000Cpu&gt;</c> discovers every 68000 block as a SINGLE fallback op (the
/// empty <c>JitDescriptorsByKey</c> → every op <c>Undefined</c>/<c>NeedsFallback</c>/<c>EndsBlock</c>), builds
/// the 19-name register map without throwing, and a one-instruction <c>JittedCpu&lt;M68000Cpu&gt;.Run</c>
/// produces the interpreter's exact state (the GAP-3 ushort-key single-block invariant). The all-fallback model
/// is what makes the M4.6 tier-parity gate byte-identical Tier-0-vs-Tier-1 with ZERO JIT-assembly change.</summary>
public class M68000JitGenericityTests
{
    [Fact]
    public void M68000_JitTarget_resolves_all_handles_including_AdvanceCycles()
    {
        IJitTarget t = M68000Cpu.JitTarget;
        Assert.Equal(typeof(M68000Cpu), t.CpuType);
        Assert.Equal("SR", t.StatusField.Name);          // 68000 status = SR
        Assert.Equal("PC", t.ProgramCounterField.Name);
        Assert.NotNull(t.StepMethod);
        Assert.NotNull(t.AdvanceCyclesMethod);            // GAP 1: must resolve, was null
        Assert.NotNull(t.CycleCountGetter);
        Assert.NotNull(t.InterruptPendingGetter);
    }

    [Fact]
    public void Generic_compiler_discovers_a_68000_block_as_a_single_fallback()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        bus.Write16(0x001000, 0x4E71);   // NOP (operword); any 68000 word — the table is empty so it falls back
        var cpu = new M68000Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<M68000Cpu>(cpu, M68000Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);

        var run = compiler.Discover(0x1000);
        Assert.Single(run);                       // every 68000 block is ONE op (Undefined ends the block)
        Assert.True(run[0].D.NeedsFallback);      // ... that op falls back to the interpreter
        Assert.True(run[0].D.EndsBlock);
    }

    [Fact]
    public void Register_map_builds_against_all_68000_register_names()
    {
        // The map must NOT throw on any of D0-D7/A0-A6/USP/SSP/PC/SR (all are field-backed on M68000Cpu —
        // the 68000 has no composed pair-view PROPERTIES like the Z80, so every name resolves).
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        var cpu = new M68000Cpu(bus);
        var opts = new JitOptions();
        _ = new BlockCompiler<M68000Cpu>(cpu, M68000Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);  // no throw
        foreach (var name in M68000Cpu.JitTarget.RegisterNames)
            Assert.True(typeof(M68000Cpu).GetField(name) is not null, $"register '{name}' has no field");
    }

    [Fact]
    public void JittedCpu_of_68000_runs_a_NOP_via_fallback_identically_to_the_interpreter()
    {
        // GAP-3 guard: one instruction through JittedCpu<M68000Cpu>.Run is byte-identical to a single Step.
        static (uint pc, long cyc) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, 0x4E71);     // NOP at PC; prefetch refill word at PC+2
            bus.Write16(0x001002, 0x4E71);
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);   // supervisor, ints masked (a benign live SR)
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                // A budget of 1 cycle runs EXACTLY ONE block iteration: `while (budget > 0)` passes once
                // (1 > 0), the single fallback op charges the NOP's full cycle cost (driving budget < 0), and
                // the loop exits — one instruction, mirroring the interpreter's single Step(). A larger budget
                // would let Run loop and execute several NOPs (the JIT dispatcher is a budget-driven loop, not
                // a one-shot), which is correct JIT behavior but not a single-instruction parity comparison.
                long budget = 1;
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((uint)inner.GetRegister("PC"), inner.CycleCount);
        }
        var (jpc, jcyc) = RunOne(throughJit: true);
        var (ipc, icyc) = RunOne(throughJit: false);
        Assert.Equal(ipc, jpc);     // the fallback set the real 24-bit PC; the ushort cache key never aliased
        Assert.Equal(icyc, jcyc);   // the fallback charged the same cycles (CycleCount delta)
    }

    private static (M68000Cpu Cpu, AddressSpace Bus, BlockCompiler<M68000Cpu> Compiler) NewM68k()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        var cpu = new M68000Cpu(bus);
        var opts = new JitOptions();
        return (cpu, bus, new BlockCompiler<M68000Cpu>(cpu, M68000Cpu.JitTarget, bus, new Fastmem(bus, opts), opts));
    }

    /// <summary>M6 PR-4: the 68000 MOVE-family FallbackEmitCount flip. Before PR-4 the 68000 was 100% fallback
    /// (every op = 1 fallback); after PR-4 a MOVE/MOVEA/MOVEQ emits real IL (0 fallbacks). Each block is one
    /// MOVE-family op (register-only EAs, so no extension words) terminated by the still-fallback NOP (0x4E71),
    /// which ends the block — so the block's ONLY fallback is the NOP. This is the "FallbackEmitCount drops by
    /// exactly the emitted opcodes" gate AND the gate/arm lockstep check (each form must have a real EmitM68kMove
    /// path, or the arm's default throws and Compile fails). Operwords (big-endian words at PC):
    ///   0x3200 MOVE.w  D0,D1   |  0x1200 MOVE.b  D0,D1   |  0x2200 MOVE.l  D0,D1
    ///   0x3248 MOVEA.w A0,A1   |  0x2248 MOVEA.l A0,A1   |  0x7001 MOVEQ #1,D0  |  0x7E80 MOVEQ #-128,D7</summary>
    [Theory]
    [InlineData(0x3200)]   // MOVE.w  D0,D1
    [InlineData(0x1200)]   // MOVE.b  D0,D1
    [InlineData(0x2200)]   // MOVE.l  D0,D1
    [InlineData(0x3248)]   // MOVEA.w A0,A1
    [InlineData(0x2248)]   // MOVEA.l A0,A1
    [InlineData(0x7001)]   // MOVEQ #1,D0
    [InlineData(0x7E80)]   // MOVEQ #-128,D7
    public void M68000_MOVE_block_emits_no_fallback_after_PR4(int operword)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, (ushort)operword);   // the MOVE-family op (register-only EA — no ext words)
        bus.Write16(0x001002, 0x4E71);             // NOP — the one block-ending fallback
        compiler.Compile(0x1000);
        Assert.Equal(1, compiler.FallbackEmitCount);   // exactly the NOP; the MOVE-family op emitted 0
    }

    /// <summary>M6 PR-4: the descriptor-state gate — the net-new MOVE/MOVEA/MOVEQ rows carry
    /// JitOpClass.M68000Move, NeedsFallback=false, EndsBlock=false. The keys are the decode walk's
    /// (1&lt;&lt;24)|(opIndex&lt;&lt;8)|size packing: MOVEA opIndex 21, MOVE opIndex 22, MOVEQ opIndex 58.
    /// A still-fallback 68000 op (NOP key 0x011C00 — opIndex 28) proves the table is otherwise unchanged.</summary>
    [Fact]
    public void M68000_MOVE_family_descriptors_are_emittable_and_classed_M68000Move()
    {
        var move = M68000Cpu.DescriptorFor(0x1001601u);   // MOVE.w (opIndex 22, size 1)
        Assert.Equal("MOVE", move.Mnemonic);
        Assert.Equal(JitOpClass.M68000Move, move.Class);
        Assert.False(move.NeedsFallback);
        Assert.False(move.EndsBlock);

        var movea = M68000Cpu.DescriptorFor(0x1001501u);  // MOVEA.w (opIndex 21, size 1)
        Assert.Equal("MOVEA", movea.Mnemonic);
        Assert.Equal(JitOpClass.M68000Move, movea.Class);
        Assert.False(movea.NeedsFallback);

        var moveq = M68000Cpu.DescriptorFor(0x1003A00u);  // MOVEQ (opIndex 58)
        Assert.Equal("MOVEQ", moveq.Mnemonic);
        Assert.Equal(JitOpClass.M68000Move, moveq.Class);
        Assert.False(moveq.NeedsFallback);

        // A non-MOVE 68000 op stays Undefined/fallback (the table is MOVE-family-only after PR-4).
        Assert.True(M68000Cpu.DescriptorFor(0x011C00u).NeedsFallback);   // NOP (opIndex 28) — still fallback
    }

    /// <summary>M6 PR-4 (pre-merge review HIGH regression guard): a MOVE with an <c>(An)+</c> / <c>-(An)</c>
    /// MEMORY DESTINATION must write the SOURCE operand to memory and advance An — NOT write the
    /// post-incremented/pre-decremented An value (the bug where the dest write-back's register-store staging
    /// clobbered the live MOVE operand local). This is a DETERMINISTIC unit gate (not corpus-sampled), so it
    /// catches the clobber regardless of which TomHarte vectors a CI sample happens to draw. Drives Tier-1
    /// directly and diffs against the interpreter Step (Tier-0) on BOTH the written RAM and A1.
    ///   0x32C0 = MOVE.w D0,(A1)+   |   0x3300 = MOVE.w D0,-(A1)   (dest mode 3/4, reg 1; src mode 0, reg 0)</summary>
    [Theory]
    [InlineData(0x32C0, /*postInc*/ true)]    // MOVE.w D0,(A1)+
    [InlineData(0x3300, /*postInc*/ false)]   // MOVE.w D0,-(A1)
    public void MOVE_to_An_postinc_predec_writes_the_source_operand_not_the_advanced_address(int operword, bool postInc)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof

        const uint srcValue = 0x1234u;   // the operand to MOVE — distinct from the A1 base/advanced address
        const uint a1Base = 0x002000u;
        // (A1)+ wrote at a1Base then A1=a1Base+2; -(A1) set A1=a1Base-2 then wrote there.
        uint writtenAddr = postInc ? a1Base : a1Base - 2;

        (uint a1, ushort memWord) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, (ushort)operword);   // MOVE.w D0,(A1)+ / -(A1)
            bus.Write16(0x001002, 0x4E71);             // NOP — block-ending fallback
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);           // supervisor, ints masked
            inner.SetRegister("D0", srcValue);
            inner.SetRegister("A1", a1Base);
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                long budget = 1;                       // exactly one instruction (the MOVE)
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((uint)inner.GetRegister("A1"), bus.Read16(writtenAddr));
        }
        var (ja1, jmem) = RunOne(throughJit: true);
        var (ia1, imem) = RunOne(throughJit: false);

        // The written word MUST be the source operand (0x1234), NOT the advanced A1 (0x2002 / 0x1FFE).
        Assert.Equal((ushort)srcValue, imem);   // interpreter oracle: writes the operand
        Assert.Equal(imem, jmem);               // JIT byte-identical on the written RAM
        Assert.Equal(ia1, ja1);                 // ... and on the post-inc/pre-dec An
    }

    [M68000TomHarteTheory]   // skips when the 680x0 vectors are absent (same attribute the data-axis sweeps use)
    [InlineData("NOP.json.gz")]
    public void One_family_file_is_tier_parity_green_through_the_JIT(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir, file);
        Assert.True(System.IO.File.Exists(path), $"vector file missing: {path}");
        var cases = M68000TomHarteLoader.LoadFile(path);
        int executed = 0;
        var failures = new System.Collections.Generic.List<string>();
        foreach (var c in cases)
        {
            // Carry the interpreter sweeps' corpus-artifact exclusions forward (Refinement 3). NOP.json.gz has
            // neither artifact, so this is a no-op here — included for symmetry with the headline sweep.
            if (M68000DataAxisCorpus.IsExcludedCase(c)) continue;
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) continue;
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 5) break; }
        }
        Assert.True(executed > 0, $"{file}: 0 executed cases");
        Assert.True(failures.Count == 0, $"{file}: {failures.Count} tier-parity failures:\n" + string.Join("\n", failures));
    }
}
