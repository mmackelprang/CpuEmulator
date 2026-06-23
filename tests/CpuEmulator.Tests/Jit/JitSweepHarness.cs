using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-1: the deterministic flat-CPU JIT-sweep harness for the SAFE identity gate
/// (<see cref="KeyWideningIdentityTests"/>). Each Run* drives a small, fully self-contained,
/// byte-deterministic program through <c>JittedCpu&lt;TCpu&gt;</c> and returns the cumulative test-seam
/// counters. The programs are chosen to exercise compile + chain (all CPUs) and, for the 6502,
/// eviction + recompile (an SMC thrasher with the lever OFF so it evicts-and-recompiles every pass).
/// FallbackEmitCount is the LAST-Compile probe (it resets per Compile), so it is measured from a
/// canonical direct <c>compiler.Compile</c> of a fixed block rather than read off the dispatcher.</summary>
internal static class JitSweepHarness
{
    internal readonly record struct SweepResult(
        int CompileCount, long ChainStepCount, long TotalEvictions, long TotalRecompiles, int FallbackEmitCount);

    private static AddressSpace NewRam16()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    // ── 6502 ──────────────────────────────────────────────────────────────────────────────────────
    // Sub-run A: a two-block static chain (compile + chain). $0200 LDA #$01 / JMP $0300 ; $0300 LDX
    //   #$02 / JMP $0300 (self-loop via the chain). Budget 200.
    // Sub-run B: an SMC thrasher with the lever OFF — it stores onto its own code page every pass, so
    //   the SMC guard evicts + recompiles the block each iteration (the eviction/recompile arm).
    // The returned counters SUM the two sub-runs; FallbackEmitCount is measured from a canonical
    //   fully-emittable block (LDA #imm / JMP-self → 0 fallbacks).
    internal static SweepResult RunMos6502()
    {
        // Sub-run A: chain.
        var spaceA = NewRam16();
        Poke(spaceA, 0x0200, 0xA9, 0x01, 0x4C, 0x00, 0x03);   // LDA #$01 / JMP $0300
        Poke(spaceA, 0x0300, 0xA2, 0x02, 0x4C, 0x00, 0x03);   // $0300 LDX #$02 / JMP $0300
        var innerA = new Mos6502Cpu(spaceA) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jitA = new JittedCpu<Mos6502Cpu>(innerA, Mos6502Cpu.JitTarget, spaceA);
        long budgetA = 200; jitA.Run(ref budgetA);

        // Sub-run B: SMC thrasher, lever OFF, a fixed trip count, driven to its JMP-self park.
        var spaceB = NewRam16();
        const byte trips = 8;
        // $0200 LDX #trips / $0202 LDA #$EA / $0204 STA $0207 / $0207 EA / $0208 DEX / $0209 BNE $0202
        // / $020B JMP $020B
        Poke(spaceB, 0x0200, 0xA2, trips, 0xA9, 0xEA, 0x8D, 0x07, 0x02, 0xEA,
                             0xCA, 0xD0, 0xF7, 0x4C, 0x0B, 0x02);
        var innerB = new Mos6502Cpu(spaceB, UndefinedOpcodePolicy.Nop) { PC = 0x0200, S = 0xFD, P = 0x34 };
        var jitB = new JittedCpu<Mos6502Cpu>(innerB, Mos6502Cpu.JitTarget, spaceB,
            options: new JitOptions { DisableSmcLever = true });
        DriveToPark(innerB, jitB);

        // FallbackEmitCount probe: a canonical fully-emittable 6502 block (LDA #imm / JMP-self) → 0.
        var spaceF = NewRam16();
        Poke(spaceF, 0x0400, 0xA9, 0x01, 0x4C, 0x00, 0x04);   // LDA #$01 / JMP $0400
        var innerF = new Mos6502Cpu(spaceF) { PC = 0x0400 };
        var compilerF = new BlockCompiler<Mos6502Cpu>(innerF, Mos6502Cpu.JitTarget, spaceF,
            new Fastmem(spaceF, new JitOptions()), new JitOptions());
        compilerF.Compile(0x0400);

        return new SweepResult(
            jitA.CompileCount + jitB.CompileCount,
            jitA.ChainStepCount + jitB.ChainStepCount,
            jitA.TotalEvictions + jitB.TotalEvictions,
            jitA.TotalRecompiles + jitB.TotalRecompiles,
            compilerF.FallbackEmitCount);
    }

    /// <summary>Drive a 6502 JIT to its JMP-self park (mirrors SmcRecompileLeverTests.DriveToPark) —
    /// a deterministic terminator for the SMC thrasher sub-run.</summary>
    private static void DriveToPark(Mos6502Cpu cpu, JittedCpu<Mos6502Cpu> jit)
    {
        while (cpu.CycleCount < 5_000_000)
        {
            ushort before = cpu.PC;
            long budget = 64;
            jit.Run(ref budget);
            if (cpu.PC != before) continue;
            ushort atRest = cpu.PC;
            long probe = 1;
            jit.Run(ref probe);
            if (cpu.PC == atRest) return;
        }
    }

    // ── Z80 ───────────────────────────────────────────────────────────────────────────────────────
    // A two-block chain: $0100 LD A,$01 / JR $0100 (self-loop via the chain edge). One block ends at the
    //   JR (block-ending flow op, emitted), and the JR chains back to $0100. Budget 64.
    // FallbackEmitCount probe: LD A,$42 / HALT — the LD emits, the HALT is the one fallback → 1.
    internal static SweepResult RunZ80()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        // $0100 LD A,$01 (3E 01) ; $0102 LD B,$02 (06 02) ; $0104 JR $0100 (18 FA, rel -6 from $0106).
        // Two emitted blocks: [LD A / LD B / JR] is ONE straight-line block ending at JR — but JR is the
        // only block-ender, so this is a single block self-looping. To get a 2-block chain, split with a
        // second entry: $0100 LD A,$01 / JP $0103 ; $0103 LD B,$02 / JR $0103.
        bus.Write8(0x0100, 0x3E); bus.Write8(0x0101, 0x01);   // LD A,$01
        bus.Write8(0x0102, 0xC3); bus.Write8(0x0103, 0x06); bus.Write8(0x0104, 0x01);   // JP $0106
        bus.Write8(0x0106, 0x06); bus.Write8(0x0107, 0x02);   // LD B,$02
        bus.Write8(0x0108, 0x18); bus.Write8(0x0109, 0xFC);   // JR $0106 (rel -4 from $010A → $0106)
        var inner = new Z80Cpu(bus);
        inner.SetRegister("PC", 0x0100);
        var jit = new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus);
        long budget = 64; jit.Run(ref budget);

        // FallbackEmitCount probe: LD A,$42 / HALT → 1 (the HALT).
        var busF = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        busF.MapMemory(0x0000, new byte[0x10000], writable: true);
        busF.Write8(0x0100, 0x3E); busF.Write8(0x0101, 0x42); busF.Write8(0x0102, 0x76);   // LD A,$42 / HALT
        var z80F = new Z80Cpu(busF);
        var compilerF = new BlockCompiler<Z80Cpu>(z80F, Z80Cpu.JitTarget, busF,
            new Fastmem(busF, new JitOptions()), new JitOptions());
        compilerF.Compile(0x0100);

        return new SweepResult(
            jit.CompileCount, jit.ChainStepCount, jit.TotalEvictions, jit.TotalRecompiles,
            compilerF.FallbackEmitCount);
    }

    // ── 68000 ─────────────────────────────────────────────────────────────────────────────────────
    // Every 68000 op is all-fallback (each block is ONE op that ends the block via inner.Step), so this
    //   sweep never chains. Step a fixed run of distinct NOP blocks: 4 NOPs in a row at $1000, each a
    //   distinct PC → 4 compiles, 0 chains, 0 evict/recompile.
    // FallbackEmitCount probe: a single NOP block → 1 (the one fallback op).
    internal static SweepResult RunM68000()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        for (uint i = 0; i < 16; i++) bus.Write16(0x001000 + i * 2, 0x4E71);   // a run of NOPs
        var inner = new M68000Cpu(bus);
        inner.SetRegister("PC", 0x001000);
        inner.SetRegister("SR", 0x2700);
        var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
        // Budget 32: several single-instruction fallback blocks dispatch (each NOP charges its full
        // fixed cost, each at a distinct PC → a fixed number of distinct compiles). Deterministic: the
        // NOP cycle cost is fixed, so the block count is exact and stable.
        long budget = 32; jit.Run(ref budget);

        // FallbackEmitCount probe: a single NOP block compiled directly → 1.
        var busF = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        busF.MapMemory(0x000000, new byte[0x1000000], writable: true);
        busF.Write16(0x001000, 0x4E71); busF.Write16(0x001002, 0x4E71);
        var m68kF = new M68000Cpu(busF);
        var compilerF = new BlockCompiler<M68000Cpu>(m68kF, M68000Cpu.JitTarget, busF,
            new Fastmem(busF, new JitOptions()), new JitOptions());
        compilerF.Compile(0x001000);

        return new SweepResult(
            jit.CompileCount, jit.ChainStepCount, jit.TotalEvictions, jit.TotalRecompiles,
            compilerF.FallbackEmitCount);
    }
}
