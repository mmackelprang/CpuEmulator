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

    /// <summary>M6 PR-4a: the DEAD-ARM-NOW-LIVE gate. Pre-PR-4a the byte-granular Discover mis-decoded every 68000
    /// op, so EmitM68kMove was NEVER selected (0 dispatches across 847 MOVE.w cases — the PR-4 Builder's finding) and
    /// the MOVE parity sweep was vacuous (interpreter-vs-interpreter via the all-fallback valve). With the
    /// word-granular Discover (PR-4a) the MOVE descriptor matches, reaches the emit switch, and EmitM68kMove
    /// DISPATCHES — so M68kMoveEmitSelections is &gt; 0. This is the un-fakeable proof the 68000 MOVE JIT parity is
    /// now REAL emitted-IL-vs-interpreter, not a degenerate tautology.</summary>
    [Fact]
    public void M68000_MOVE_arm_actually_dispatches_after_PR4a()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x3200);   // MOVE.w D0,D1 (register-only EA — no ext words)
        bus.Write16(0x001002, 0x4E71);   // NOP — the block-ending fallback
        compiler.Compile(0x1000);
        Assert.True(compiler.M68kMoveEmitSelections > 0,
            "EmitM68kMove was never selected — Discover is still feeding the 68000 a byte-granular stream (the dead-arm blocker).");
    }

    /// <summary>M6 PR-4a: the NEGATIVE control — proves the M68kMoveEmitSelections counter can read 0, so the
    /// positive case (<see cref="M68000_MOVE_arm_actually_dispatches_after_PR4a"/>) is meaningful and not a counter
    /// that always trips. A block of ONLY a fallback 68000 op (NOP) selects the MOVE arm zero times.</summary>
    [Fact]
    public void M68000_non_MOVE_block_selects_the_MOVE_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x4E71);   // NOP only — falls back, no MOVE row
        compiler.Compile(0x1000);
        Assert.Equal(0, compiler.M68kMoveEmitSelections);
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
    [InlineData(0x32C0)]   // PROBE: MOVE.w D0,(A1)+  (memory dest, no ext words)
    [InlineData(0x3300)]   // PROBE: MOVE.w D0,-(A1)
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

    /// <summary>M6 PR-4: a MOVE with an <c>(An)+</c> / <c>-(An)</c> MEMORY DESTINATION must write the SOURCE
    /// operand to memory and advance An. Drives Tier-1 (JittedCpu.Run) and diffs against the interpreter Step
    /// (Tier-0) on the written RAM + A1.
    ///   0x32C0 = MOVE.w D0,(A1)+   |   0x3300 = MOVE.w D0,-(A1)   (dest mode 3/4, reg 1; src mode 0, reg 0)
    ///
    /// <para>M6 PR-4a made this a REAL emitted-IL gate: BlockCompiler.Discover now feeds the 68000 a word-granular
    /// M68000FetchStream, so the MOVE descriptor matches, EmitM68kMove dispatches at runtime (proven by
    /// M68kMoveEmitSelections &gt; 0, see <see cref="M68000_MOVE_arm_actually_dispatches_after_PR4a"/>), and
    /// <c>throughJit</c> runs the emitted IL — this assertion now diffs emitted IL vs the interpreter oracle, not
    /// interpreter-vs-interpreter.</para></summary>
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

    /// <summary>PR-4b regression: a wide MOVE whose destination sits at the very TOP of the 24-bit bus
    /// (<c>(A1)</c> with A1 = 0x00FFFFFF) makes the .l write straddle the address-space boundary — bytes land at
    /// 0xFFFFFF then WRAP to 0x000000.. (the bus masks each component access). The JIT derives the SMC dirty page
    /// from <c>(addr + byteSpan - 1) &gt;&gt; 8</c>; before PR-4b that end-page term was unmasked, so the trailing
    /// bytes produced a page index past the DirtyMap's <c>bool[PageCount]</c> (IndexOutOfRangeException). This pins
    /// the masked-end-page fix (EmitWideEndPage): the emitted MOVE must run AND be byte-identical to the
    /// interpreter, which wraps the write. Deterministic — does NOT depend on the random TomHarte corpus hitting
    /// the boundary (it does not).</summary>
    [Theory]
    [InlineData(0x2281, 4)]   // MOVE.l D1,(A1)  — wide write at the top of the space
    [InlineData(0x3281, 2)]   // MOVE.w D1,(A1)
    public void Wide_MOVE_to_top_of_address_space_wraps_like_the_interpreter(int operword, int span)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof

        const uint topBase = 0x00FFFFFFu;   // the highest legal 24-bit byte address — the wide write wraps past it
        const uint srcValue = 0xDEADBEEFu;

        byte[] RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, (ushort)operword);
            bus.Write16(0x001002, 0x4E71);             // NOP — block-ending fallback
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);
            inner.SetRegister("D1", srcValue);
            inner.SetRegister("A1", topBase);
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                long budget = 1;
                jit.Run(ref budget);
            }
            else inner.Step();
            // Read back every byte the wide write touched (wrapping), so the comparison covers the wrapped tail.
            var bytes = new byte[span];
            for (int i = 0; i < span; i++) bytes[i] = bus.Read8((topBase + (uint)i) & bus.AddressMask);
            return bytes;
        }

        byte[] jit = RunOne(throughJit: true);    // must not throw IndexOutOfRange (the PR-4b end-page fix)
        byte[] interp = RunOne(throughJit: false);
        Assert.Equal(interp, jit);                // byte-identical wrapped write
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

    // ── M6 PR-5: the integer-ALU emit gates ──────────────────────────────────────────────────────────────────

    /// <summary>M6 PR-5: the ALU analogue of <see cref="M68000_MOVE_arm_actually_dispatches_after_PR4a"/> — the
    /// dead-arm-now-live proof. Compiling a block whose first op is an ALU-family op selects EmitM68kAlu at least
    /// once, so M68kAluEmitSelections is &gt; 0: the ALU JIT parity is REAL emitted-IL-vs-interpreter, not a
    /// degenerate tautology. 0xD041 = ADD.w D0,D1 (register-only EA — no ext words).</summary>
    [Fact]
    public void M68000_ALU_arm_actually_dispatches()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0xD041);   // ADD.w D0,D1
        bus.Write16(0x001002, 0x4E71);   // NOP — block-ending fallback
        compiler.Compile(0x1000);
        Assert.True(compiler.M68kAluEmitSelections > 0,
            "EmitM68kAlu was never selected — the ALU descriptor row did not reach the emit switch.");
    }

    /// <summary>M6 PR-5: the negative control — a non-ALU block (NOP) selects the ALU arm zero times, so the
    /// positive case is meaningful (the counter can read 0).</summary>
    [Fact]
    public void M68000_non_ALU_block_selects_the_ALU_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x4E71);   // NOP only — falls back, no ALU row
        compiler.Compile(0x1000);
        Assert.Equal(0, compiler.M68kAluEmitSelections);
    }

    /// <summary>M6 PR-5: the integer-ALU FallbackEmitCount flip — each emitted ALU op in a one-op block
    /// contributes 0 fallbacks (the block's ONLY fallback is the block-ending NOP). The "FallbackEmitCount drops
    /// by exactly the emitted opcodes" gate AND the gate/arm lockstep check (each form must have a real EmitM68kAlu
    /// path, or the arm throws and Compile fails). Operwords (big-endian words at PC; register-only EAs — no ext
    /// words). Covers every PR-5 shape: RegEa (ADD/SUB/CMP/AND/OR/EOR), AddrEa (ADDA/SUBA/CMPA), QuickEa
    /// (ADDQ/SUBQ — An-dest special case + Dn dest), XAlu (ADDX/SUBX reg form), and the memory-dest RMW RegEa.</summary>
    [Theory]
    [InlineData(0xD000)]   // ADD.b  D0,D0   (RegEa, toEa=false, Dn dest)
    [InlineData(0xD041)]   // ADD.w  D0,D1
    [InlineData(0xD082)]   // ADD.l  D0,D2
    [InlineData(0x9000)]   // SUB.b  D0,D0
    [InlineData(0xB000)]   // CMP.b  D0,D0   (writesResult=false)
    [InlineData(0xC000)]   // AND.b  D0,D0   (Logic CCR — X untouched)
    [InlineData(0x8000)]   // OR.b   D0,D0
    [InlineData(0xB100)]   // EOR.b  D0,D0
    [InlineData(0xD0C0)]   // ADDA.w D0,A0   (AddrEa, An dest, NO CCR)
    [InlineData(0x90C0)]   // SUBA.w D0,A0
    [InlineData(0xB0C0)]   // CMPA.w D0,A0   (Cmp CCR, no write)
    [InlineData(0x5000)]   // ADDQ.b #8,D0   (QuickEa, Dn dest)
    [InlineData(0x5100)]   // SUBQ.b #8,D0
    [InlineData(0x5048)]   // ADDQ.w #8,A0   (QuickEa, An-dest special case — whole An, NO CCR)
    [InlineData(0xD100)]   // ADDX.b D0,D0   (XAlu reg form — live X in, sticky Z)
    [InlineData(0x9100)]   // SUBX.b D0,D0
    [InlineData(0xD159)]   // ADD.w  D0,(A1)+ (RegEa, toEa=true — memory-dest RMW, address-once)
    [InlineData(0xD161)]   // ADD.w  D0,-(A1) (RegEa, toEa=true — memory-dest RMW predecrement)
    public void M68000_ALU_block_emits_no_fallback(int operword)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, (ushort)operword);   // the ALU op (register-only / single-EA — no ext words)
        bus.Write16(0x001002, 0x4E71);             // NOP — the one block-ending fallback
        compiler.Compile(0x1000);
        Assert.Equal(1, compiler.FallbackEmitCount);   // exactly the NOP; the ALU op emitted 0
    }

    /// <summary>M6 PR-5: the descriptor-state gate — the net-new ALU rows carry JitOpClass.M68000Alu,
    /// NeedsFallback=false, EndsBlock=false. The keys are the decode walk's (1&lt;&lt;24)|(opIndex&lt;&lt;8)|size
    /// packing (ADD opIndex 77, ADDX 76, CMP 69 from the grammar Ops order). A still-fallback ALU-adjacent op
    /// (NEG, NOT in scope) proves the table is otherwise unchanged.</summary>
    [Fact]
    public void M68000_ALU_family_descriptors_are_emittable_and_classed_M68000Alu()
    {
        var add = M68000Cpu.DescriptorFor(0x1004D01u);   // ADD.w (opIndex 77, size 1)
        Assert.Equal("ADD", add.Mnemonic);
        Assert.Equal(JitOpClass.M68000Alu, add.Class);
        Assert.False(add.NeedsFallback);
        Assert.False(add.EndsBlock);

        var addx = M68000Cpu.DescriptorFor(0x1004C01u);  // ADDX.w (opIndex 76, size 1)
        Assert.Equal("ADDX", addx.Mnemonic);
        Assert.Equal(JitOpClass.M68000Alu, addx.Class);
        Assert.False(addx.NeedsFallback);

        var cmp = M68000Cpu.DescriptorFor(0x1004501u);   // CMP.w (opIndex 69, size 1)
        Assert.Equal("CMP", cmp.Mnemonic);
        Assert.Equal(JitOpClass.M68000Alu, cmp.Class);

        // NEG stays fallback (NOT a PR-5 family) — proving the ALU rows are the in-scope set only.
        // NEG opIndex is in the grammar; any NEG-key descriptor must remain Undefined/fallback.
        var addqAn = M68000Cpu.DescriptorFor(0x1003701u); // ADDQ.w (opIndex 55, size 1) — emitted
        Assert.Equal("ADDQ", addqAn.Mnemonic);
        Assert.Equal(JitOpClass.M68000Alu, addqAn.Class);
    }

    /// <summary>M6 PR-5: the memory-dest-RMW An-mutation tripwire (the plan's required tripwire). An ADD with an
    /// (A1)+ / -(A1) MEMORY dest must write the RESULT (a+b) to memory AND advance A1 exactly once (address-once —
    /// a double-resolve would advance A1 twice / write to the wrong address). Drives Tier-1 (JittedCpu.Run) and
    /// diffs against the interpreter Step (Tier-0) on the written RAM + A1 + SR (the X-bit).
    ///   0xD159 = ADD.w D0,(A1)+   |   0xD161 = ADD.w D0,-(A1)   (dest mode 3/4 reg 1; D0 = the other operand).</summary>
    [Theory]
    [InlineData(0xD159, /*postInc*/ true)]    // ADD.w D0,(A1)+
    [InlineData(0xD161, /*postInc*/ false)]   // ADD.w D0,-(A1)
    public void ADD_to_An_postinc_predec_writes_the_result_and_advances_An_once(int operword, bool postInc)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof

        const uint d0 = 0x0011u;
        const ushort memSeed = 0x2200;     // the existing memory word the RMW adds D0 to
        const uint a1Base = 0x002000u;
        uint writtenAddr = postInc ? a1Base : a1Base - 2;

        (uint a1, ushort memWord, ushort sr) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, (ushort)operword);
            bus.Write16(0x001002, 0x4E71);             // NOP — block-ending fallback
            bus.Write16(writtenAddr, memSeed);         // the RMW reads this, adds D0, writes back
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);           // supervisor, ints masked
            inner.SetRegister("D0", d0);
            inner.SetRegister("A1", a1Base);
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                long budget = 1;                       // exactly one instruction (the ADD)
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((uint)inner.GetRegister("A1"), bus.Read16(writtenAddr), (ushort)inner.GetRegister("SR"));
        }
        var (ja1, jmem, jsr) = RunOne(throughJit: true);
        var (ia1, imem, isr) = RunOne(throughJit: false);

        // The written word MUST be the ADD result (memSeed + D0), NOT D0 alone nor the advanced address.
        Assert.Equal((ushort)(memSeed + d0), imem);   // interpreter oracle: writes the RMW result
        Assert.Equal(imem, jmem);                     // JIT byte-identical on the written RAM
        Assert.Equal(ia1, ja1);                        // ... and on the address-once-advanced A1
        Assert.Equal(isr, jsr);                        // ... and on SR (the X=C output bit)
    }

    // ── M6 PR-6: the shift + control-flow emit gates ──────────────────────────────────────────────────────────

    /// <summary>M6 PR-6: the shift analogue of <see cref="M68000_ALU_arm_actually_dispatches"/> — compiling a
    /// block whose first op is a shift selects EmitM68kShift at least once, so M68kShiftEmitSelections is &gt; 0:
    /// the shift JIT parity is REAL emitted-IL-vs-interpreter. 0xE048 = LSR.w #8,D0 (register-only — no ext words).</summary>
    [Fact]
    public void M68000_Shift_arm_actually_dispatches()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0xE048);   // LSR.w #8,D0
        bus.Write16(0x001002, 0x4E71);   // NOP — block-ending fallback
        compiler.Compile(0x1000);
        Assert.True(compiler.M68kShiftEmitSelections > 0,
            "EmitM68kShift was never selected — the shift descriptor row did not reach the emit switch.");
    }

    /// <summary>M6 PR-6: the negative control — a non-shift block (NOP) selects the shift arm zero times.</summary>
    [Fact]
    public void M68000_non_shift_block_selects_the_shift_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x4E71);   // NOP only
        compiler.Compile(0x1000);
        Assert.Equal(0, compiler.M68kShiftEmitSelections);
    }

    /// <summary>M6 PR-6: the shift FallbackEmitCount flip — each emitted shift op in a one-op block contributes 0
    /// fallbacks (the block's ONLY fallback is the block-ending NOP). Covers all 8 register kinds (imm + register
    /// count) + the memory-by-1 form. Operwords (register-only / single-EA — no leading ext words):</summary>
    [Theory]
    [InlineData(0xE100)]   // ASL.b #8,D0   (imm count, left)
    [InlineData(0xE000)]   // ASR.b #8,D0   (imm count, right)
    [InlineData(0xE108)]   // LSL.b #8,D0
    [InlineData(0xE008)]   // LSR.b #8,D0
    [InlineData(0xE118)]   // ROL.b #8,D0
    [InlineData(0xE018)]   // ROR.b #8,D0
    [InlineData(0xE110)]   // ROXL.b #8,D0  (through-X)
    [InlineData(0xE010)]   // ROXR.b #8,D0
    [InlineData(0xE160)]   // ASL.b D3,D0   (register count: bits 11-9 = D3, bit 5 set)
    [InlineData(0xE368)]   // LSL.b D1,D0   (register count)
    [InlineData(0xE1D0)]   // ASL.w (A0)    (SHIFT_MEM — memory-by-1 RMW; eaMode 2)
    public void M68000_Shift_block_emits_no_fallback(int operword)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, (ushort)operword);   // the shift op (no leading ext words)
        bus.Write16(0x001002, 0x4E71);             // NOP — the one block-ending fallback
        compiler.Compile(0x1000);
        Assert.Equal(1, compiler.FallbackEmitCount);   // exactly the NOP; the shift op emitted 0
    }

    /// <summary>M6 PR-6: the flow analogue of <see cref="M68000_ALU_arm_actually_dispatches"/>. 0x6002 = BRA.b +2.</summary>
    [Fact]
    public void M68000_Flow_arm_actually_dispatches()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x6002);   // BRA.b +2 (cc 0 — always taken)
        bus.Write16(0x001002, 0x4E71);
        compiler.Compile(0x1000);
        Assert.True(compiler.M68kFlowEmitSelections > 0,
            "EmitM68kFlow was never selected — the flow descriptor row did not reach the emit switch.");
    }

    /// <summary>M6 PR-6: the negative control — a non-flow block (NOP) selects the flow arm zero times.</summary>
    [Fact]
    public void M68000_non_flow_block_selects_the_flow_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, 0x4E71);   // NOP only
        compiler.Compile(0x1000);
        Assert.Equal(0, compiler.M68kFlowEmitSelections);
    }

    /// <summary>M6 PR-6: the flow FallbackEmitCount flip. A flow op ENDS the block (it self-terminates via
    /// EmitChainOrExit / EmitNormalExit), so a one-flow-op block has 0 fallbacks if it emits (vs 1 before PR-6
    /// when it fell back). NO trailing NOP — the flow op is the whole block. Bcc.b/BRA/BSR carry no ext word;
    /// DBcc has one ext word (its disp); JMP/JSR (An) and RTS carry no ext word. Both Bcc edges + the JMP/JSR
    /// dynamic-ea / static-ea routing are exercised by the gate file (Task 5); here we only assert the flip.</summary>
    [Theory]
    [InlineData(0x6002, false)]   // BRA.b +2
    [InlineData(0x6102, false)]   // BSR.b +2   (push + branch)
    [InlineData(0x6602, false)]   // BNE.b +2   (conditional)
    [InlineData(0x51C8, true)]    // DBF D0,disp (DBRA — always decrements; has one ext word)
    [InlineData(0x4ED0, false)]   // JMP (A0)    (dynamic ea -> EmitNormalExit)
    [InlineData(0x4E90, false)]   // JSR (A0)    (dynamic ea push + exit)
    [InlineData(0x4EF8, false)]   // JMP abs.w   (static ea -> chain; has one ext word)
    [InlineData(0x4E75, false)]   // RTS         (pop + exit)
    public void M68000_Flow_block_emits_no_fallback(int operword, bool hasExtWord)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, bus, compiler) = NewM68k();
        bus.Write16(0x001000, (ushort)operword);
        if (hasExtWord) bus.Write16(0x001002, 0x0004);   // a benign +4 displacement / abs.w address
        compiler.Compile(0x1000);
        Assert.Equal(0, compiler.FallbackEmitCount);   // the flow op emitted real IL AND ended the block
    }

    /// <summary>M6 PR-6: the descriptor-state gate — the net-new shift rows carry JitOpClass.M68000Shift
    /// (EndsBlock=false) and the flow rows JitOpClass.M68000Flow (EndsBlock=TRUE), all NeedsFallback=false. Keys
    /// are the decode walk's (1&lt;&lt;24)|(opIndex&lt;&lt;8)|size packing (ASLR_REG opIndex 0x4F, Bcc 0x39, DBcc
    /// 0x35, JMP 0x2A, JSR 0x2B, RTS 0x19). A still-fallback tail op (TRAP) stays Undefined.</summary>
    [Fact]
    public void M68000_Shift_and_Flow_descriptors_are_emittable_and_classed()
    {
        var asl = M68000Cpu.DescriptorFor(0x1004F01u);   // ASLR_REG .w
        Assert.Equal("ASLR_REG", asl.Mnemonic);
        Assert.Equal(JitOpClass.M68000Shift, asl.Class);
        Assert.False(asl.NeedsFallback);
        Assert.False(asl.EndsBlock);                     // shifts continue the block

        var shiftMem = M68000Cpu.DescriptorFor(0x1004E01u);
        Assert.Equal("SHIFT_MEM", shiftMem.Mnemonic);
        Assert.Equal(JitOpClass.M68000Shift, shiftMem.Class);

        var bcc = M68000Cpu.DescriptorFor(0x1003901u);   // Bcc
        Assert.Equal("Bcc", bcc.Mnemonic);
        Assert.Equal(JitOpClass.M68000Flow, bcc.Class);
        Assert.False(bcc.NeedsFallback);
        Assert.True(bcc.EndsBlock);                      // flow ops END the block

        var dbcc = M68000Cpu.DescriptorFor(0x1003501u);  // DBcc
        Assert.Equal("DBcc", dbcc.Mnemonic);
        Assert.Equal(JitOpClass.M68000Flow, dbcc.Class);
        Assert.True(dbcc.EndsBlock);

        var rts = M68000Cpu.DescriptorFor(0x1001901u);   // RTS
        Assert.Equal("RTS", rts.Mnemonic);
        Assert.Equal(JitOpClass.M68000Flow, rts.Class);

        var jmp = M68000Cpu.DescriptorFor(0x1002A01u);   // JMP
        Assert.Equal("JMP", jmp.Mnemonic);
        Assert.Equal(JitOpClass.M68000Flow, jmp.Class);

        // The exception/microcoded tail stays fallback (RTE/MOVEM/MUL/DIV/LINK/UNLK not classified).
        // RTS emits but RTE does NOT — RTE has no row, so any RTE-key descriptor is Undefined/fallback.
    }

    /// <summary>M6 PR-6 (DECISION D): the DBcc three-outcome tripwire — the load-bearing PR-6 semantic. Runs a
    /// single DBcc through the JIT (Tier-1) and asserts Dn.w + the landed PC for each outcome, pinned against the
    /// interpreter (Tier-0). The off-by-one is the oracle's `counter--; if (counter != 0xFFFF) branch` — so DBF
    /// (cc 1 = F, never true) ALWAYS decrements but TERMINATES (falls through) at -1 (0xFFFF):
    ///   Dn.w == 0  -&gt; decrements to 0xFFFF -&gt; FALLS THROUGH (-1 terminates, NOT 0 — the classic off-by-one);
    ///   Dn.w == 1  -&gt; decrements to 0      -&gt; BRANCHES (one more iteration);
    ///   Dn.w == 2  -&gt; decrements to 1      -&gt; BRANCHES.
    /// DBT (cc 0 = T, always true) exercises outcome (1): condition TRUE -&gt; fall through, NO decrement (Dn
    /// untouched). The .w PARTIAL decrement is verified by seeding the upper word (0xAAAA0000) and asserting it
    /// survives every decrement.</summary>
    [Theory]
    [InlineData(0x51C8, 0xAAAA0000u, /*branches*/ false, 0xAAAAFFFFu)]  // DBF, Dn.w=0 -> 0xFFFF, FALL THROUGH (terminate)
    [InlineData(0x51C8, 0xAAAA0001u, true, 0xAAAA0000u)]               // DBF, Dn.w=1 -> 0,      branch
    [InlineData(0x51C8, 0xAAAA0002u, true, 0xAAAA0001u)]              // DBF, Dn.w=2 -> 1,      branch
    [InlineData(0x50C8, 0xAAAA0005u, /*falls through*/ false, 0xAAAA0005u)]  // DBT (cc T) -> no decrement, fall through
    public void DBcc_three_outcome_tripwire(int operword, uint d0Seed, bool branches, uint expectedD0)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        const short disp = 0x0010;   // a +16 displacement; branchBase = pc+2
        // taken target = (pc+2) + disp = 0x1002 + 0x10 = 0x1012; fall-through = pc + length = 0x1004.
        const uint takenPc = 0x1000u + 2u + 0x10u;
        const uint fallThroughPc = 0x1000u + 4u;
        uint expectedPc = branches ? takenPc : fallThroughPc;

        (uint d0, uint pc) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, (ushort)operword);
            bus.Write16(0x001002, (ushort)disp);   // the DBcc displacement ext word
            // landing pad words (so a chained/continued block at the target sees defined memory — fallback NOP)
            bus.Write16((ushort)takenPc, 0x4E71);
            bus.Write16((ushort)fallThroughPc, 0x4E71);
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);       // supervisor, ints masked; CCR clear (condition irrelevant for DBF/DBT)
            inner.SetRegister("D0", d0Seed);
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                long budget = 1;                   // exactly one instruction (the DBcc)
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((uint)inner.GetRegister("D0"), (uint)inner.GetRegister("PC"));
        }

        var (jd0, jpc) = RunOne(throughJit: true);
        var (id0, ipc) = RunOne(throughJit: false);

        // The interpreter oracle pins the expected semantics.
        Assert.Equal(expectedD0, id0);     // Dn.w decremented (.w partial — upper word preserved) or untouched
        Assert.Equal(expectedPc, ipc);     // the landed PC (branch target or fall-through)
        // The JIT is byte-identical to the interpreter.
        Assert.Equal(id0, jd0);
        Assert.Equal(ipc, jpc);
    }
}
