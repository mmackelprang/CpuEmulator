using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 3 — the SMC-vs-chaining safety rule (the M2-i carry-forward #2 headline hazard).
/// Chaining transfers control block-to-block WITHOUT a dispatcher round-trip; it must NEVER chain
/// past a self-modifying patch and run stale IL. The resolution is BOTH a distinct
/// <see cref="BlockExit.Recompile"/> (the SMC guard's exit, never chainable — the PRECISE
/// intra-block signal) AND a <c>!Dirty.Any</c> gate on every chain edge (the COARSE cross-block
/// backstop). The headline pin: a deterministic SMC-biased seed-class — the kind the M2-i fuzzer
/// caught — stays at ZERO divergences with chaining ON (Ground truth B). The interpreter is the
/// oracle: full state + cycles + RAM are diffed.</summary>
public class ChainingSmcSafetyTests
{
    private static AddressSpace NewRamSpace()
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

    /// <summary>Build a fresh interpreter + a JIT (per <paramref name="opts"/>) over identical RAM
    /// + initial state, run BOTH to the JMP-self park (or the cycle cap), and diff final state +
    /// cycles + the entire RAM image. Returns null on agreement; a diff description on divergence.</summary>
    private static string? Diff(byte[] image, ushort startPc, byte a, byte x, byte y, byte p,
        long cycleCap, JitOptions opts)
    {
        (RegState refState, byte[] refRam) = RunInterpreter(image, startPc, a, x, y, p, cycleCap);
        (RegState jitState, byte[] jitRam) = RunJit(image, startPc, a, x, y, p, cycleCap, opts);
        if (!refState.Equals(jitState))
            return $"state diverged: interp={refState} jit={jitState}";
        for (int addr = 0; addr < 0x10000; addr++)
            if (refRam[addr] != jitRam[addr])
                return $"RAM[{addr:X4}] interp={refRam[addr]:X2} jit={jitRam[addr]:X2}";
        return null;
    }

    private readonly record struct RegState(byte A, byte X, byte Y, byte S, byte P, ushort PC, long Cycles);

    private static (RegState, byte[]) RunInterpreter(byte[] image, ushort startPc,
        byte a, byte x, byte y, byte p, long cycleCap)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = (byte[])image.Clone();
        space.MapMemory(0x0000, ram, writable: true);
        // Nop policy: an SMC patch can leave a byte that decodes as an undefined opcode; the
        // interpreter and the JIT (which falls back to inner.Step for undefined opcodes) BOTH treat
        // it as a 2-cycle NOP, so the parity comparison stays meaningful instead of throwing.
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop)
            { PC = startPc, S = 0xFD, P = p, A = a, X = x, Y = y };
        while (cpu.CycleCount < cycleCap)
        {
            ushort before = cpu.PC;
            cpu.Step();
            if (cpu.PC == before) break;   // JMP-self park
        }
        return (new RegState(cpu.A, cpu.X, cpu.Y, cpu.S, cpu.P, cpu.PC, cpu.CycleCount), ram);
    }

    private static (RegState, byte[]) RunJit(byte[] image, ushort startPc,
        byte a, byte x, byte y, byte p, long cycleCap, JitOptions opts)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = (byte[])image.Clone();
        space.MapMemory(0x0000, ram, writable: true);
        var inner = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop)
            { PC = startPc, S = 0xFD, P = p, A = a, X = x, Y = y };
        var jit = new JittedCpu(inner, space, opts);
        while (inner.CycleCount < cycleCap)
        {
            ushort before = inner.PC;
            long budget = 1;               // one instruction — exact park detection (M2-i idiom)
            jit.Run(ref budget);
            if (inner.PC == before) break; // JMP-self park
        }
        return (new RegState(inner.A, inner.X, inner.Y, inner.S, inner.P, inner.PC, inner.CycleCount), ram);
    }

    // ── A deterministic SMC-biased seed-class (the M2-i fuzzer's class, ported inline) ───────────
    // Each seed produces a small program at $0200 that stores opcode bytes into its OWN code region
    // (intra-block SMC) AND into other code blocks (cross-block SMC), then parks. This is the exact
    // class the M2-i fuzzer found diverging (28/600) before the intra-block SMC guard; it must now
    // be 0 divergences WITH chaining on. 64 seeds (the committed CI N).
    private const ushort CodeBase = 0x0200;
    private const int CodeLen = 0x60;

    private static byte[] SmcSeedProgram(int seed)
    {
        var rng = new Random(seed);
        var ram = new byte[0x10000];
        int pc = CodeBase;
        // A few "live" opcodes interleaved with stores that patch code bytes ahead of PC.
        byte[] safeOps = [0xEA, 0xE8, 0xC8, 0xCA, 0x88, 0x18, 0x38, 0xAA, 0xA8, 0x98, 0x8A];
        while (pc < CodeBase + CodeLen - 6)
        {
            int choice = rng.Next(10);
            if (choice < 4)
            {
                // LDA #imm / STA abs-into-code  (an SMC patch of a code byte ahead of PC)
                byte patch = safeOps[rng.Next(safeOps.Length)];
                ushort target = (ushort)(CodeBase + rng.Next(CodeLen));
                ram[pc++] = 0xA9; ram[pc++] = patch;                 // LDA #patch
                ram[pc++] = 0x8D; ram[pc++] = (byte)(target & 0xFF); // STA target
                ram[pc++] = (byte)(target >> 8);
            }
            else if (choice < 6)
            {
                // a forward branch (chainable both arms) on a deterministic flag
                ram[pc++] = 0xD0; ram[pc++] = 0x01;                  // BNE +1
            }
            else
            {
                ram[pc++] = safeOps[rng.Next(safeOps.Length)];       // a straight-line op
            }
        }
        // Park: JMP *
        ram[pc] = 0x4C; ram[pc + 1] = (byte)(pc & 0xFF); ram[pc + 2] = (byte)(pc >> 8);
        return ram;
    }

    [Fact]
    public void M2i_intra_block_SMC_seed_class_stays_green_with_chaining_on()
    {
        // THE HEADLINE PIN: the SMC seed-class the M2-i fuzzer caught (intra- + cross-block patches)
        // re-run with chaining ON must be ZERO divergences vs the interpreter (full state + cycles +
        // RAM). 64 seeds (the committed CI N).
        var opts = new JitOptions();   // chaining ON (the default)
        var divergences = new System.Collections.Generic.List<string>();
        for (int seed = 0; seed < 64; seed++)
        {
            byte[] image = SmcSeedProgram(seed);
            var rng = new Random(seed ^ 0x5A5A);
            string? diff = Diff(image, CodeBase,
                a: (byte)rng.Next(256), x: (byte)rng.Next(256), y: (byte)rng.Next(256),
                p: (byte)((rng.Next(256) & 0xEF) | 0x20), cycleCap: 200_000, opts);
            if (diff is not null) divergences.Add($"seed {seed}: {diff}");
        }
        Assert.Empty(divergences);
    }

    [Fact]
    public void Cross_block_SMC_hunter_seed_class_stays_green_with_chaining()
    {
        // The cross-block class: a driver block patches a JSR-target block's code, then calls it.
        // The !Dirty.Any chain gate + the per-page eviction must keep this parity-correct with
        // chaining on. Built from the same seeds with a JSR-into-patched-target shape.
        var opts = new JitOptions();
        var divergences = new System.Collections.Generic.List<string>();
        for (int seed = 0; seed < 64; seed++)
        {
            byte[] image = CrossBlockSmcProgram(seed);
            var rng = new Random(seed ^ 0x3C3C);
            string? diff = Diff(image, CodeBase,
                a: (byte)rng.Next(256), x: (byte)rng.Next(256), y: (byte)rng.Next(256),
                p: (byte)((rng.Next(256) & 0xEF) | 0x20), cycleCap: 200_000, opts);
            if (diff is not null) divergences.Add($"seed {seed}: {diff}");
        }
        Assert.Empty(divergences);
    }

    private static byte[] CrossBlockSmcProgram(int seed)
    {
        var rng = new Random(seed);
        var ram = new byte[0x10000];
        byte[] safeOps = [0xEA, 0xE8, 0xC8, 0xCA, 0x88, 0x18, 0x38, 0xAA, 0xA8];
        // Driver at $0200: patch a byte of the subroutine at $0300, then JSR $0300, then park.
        ushort patchTarget = (ushort)(0x0300 + rng.Next(0x10));
        byte patch = safeOps[rng.Next(safeOps.Length)];
        byte[] driver =
        [
            0xA9, patch,                                   // LDA #patch
            0x8D, (byte)(patchTarget & 0xFF), (byte)(patchTarget >> 8), // STA target (cross-block SMC)
            0x20, 0x00, 0x03,                              // JSR $0300
            0x4C, 0x08, 0x02,                              // JMP $0208 (park)
        ];
        Array.Copy(driver, 0, ram, 0x0200, driver.Length);
        // Subroutine at $0300: a handful of safe ops + RTS.
        int sp = 0x0300;
        for (int i = 0; i < 0x10; i++) ram[sp++] = safeOps[rng.Next(safeOps.Length)];
        ram[sp] = 0x60;   // RTS
        return ram;
    }

    [Fact]
    public void Chain_does_not_proceed_after_a_Recompile_exit()
    {
        // A block that self-modifies a later opcode in its OWN page: the SMC guard fires and exits
        // Recompile from the MIDDLE of the block — control never reaches the block's chain edge, so
        // no chain step is taken across it. The dispatcher re-decodes the modified bytes. We assert
        // (a) parity to the interpreter and (b) the guard exit did not chain.
        var image = new byte[0x10000];
        image[0x30] = 0x01;
        byte[] prog =
        [
            0xA9, 0xC6,             // LDA #$C6 (DEC-zp opcode value)
            0x8D, 0x07, 0x02,       // STA $0207 (patch the opcode at $0207 -> own page $02; SMC guard)
            0xA9, 0x05,             // LDA #$05
            0xE6, 0x30,             // $0207 INC $30 -> patched to DEC $30
            0x85, 0x31,             // STA $31
            0x4C, 0x0B, 0x02,       // JMP $020B (park)
        ];
        Array.Copy(prog, 0, image, 0x0200, prog.Length);

        // (a) Parity to the interpreter (the patched DEC runs, not the stale INC).
        Assert.Null(Diff(image, 0x0200, 0x00, 0x00, 0x00, 0x24, 200, new JitOptions()));

        // (b) Run the first (self-modifying) block only and confirm the Recompile exit did not chain.
        var space = NewRamSpace();
        Poke(space, 0x0200, prog);
        space.Write8(0x30, 0x01);
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, space);
        long budget = 1;
        jit.Run(ref budget);
        Assert.Equal(0, jit.ChainStepCount);
    }

    [Fact]
    public void Chain_does_not_proceed_while_Dirty_Any()
    {
        // A predecessor block stores to a DIFFERENT block's code page (sets a dirty mark but does
        // NOT trip its OWN-page guard), then reaches a chainable JMP exit. The !Dirty.Any chain gate
        // must break the chain so the dispatcher's per-page eviction drops the victim before it is
        // chained into. Pinned via parity to the interpreter (the patched successor runs its new
        // opcode), not the stale block.
        //   $0200 LDA #$EA / STA $0300 (patch page $03's opcode) / JMP $0300
        //   $0300 INC $31 (patched to NOP $EA) / JMP $0303 (park)
        var refImage = new byte[0x10000];
        refImage[0x31] = 0x07;
        byte[] driver = [0xA9, 0xEA, 0x8D, 0x00, 0x03, 0x4C, 0x00, 0x03];   // $0200
        Array.Copy(driver, 0, refImage, 0x0200, driver.Length);
        byte[] target = [0xE6, 0x31, 0x4C, 0x03, 0x03];                      // $0300 INC $31 / JMP $0303
        Array.Copy(target, 0, refImage, 0x0300, target.Length);

        Assert.Null(Diff(refImage, 0x0200, 0x00, 0x00, 0x00, 0x24, 5_000, new JitOptions()));
    }

    [Fact]
    public void Smc_to_a_chained_successor_recompiles_it()
    {
        // P at $0200 chains into S at $0300. Then a write patches S's opcode; the next dispatch's
        // InvalidateIfDirty must evict S, sever the inbound link, and recompile S from the modified
        // bytes on the next chain edge (resolve-by-PC). Driven on the cache primitives directly so
        // the assertion is precisely the chain-sever + recompile contract (Ground truth C).
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0x4C, 0x00, 0x03);   // LDA #1 / JMP $0300  (P)
        Poke(space, 0x0300, 0xE8, 0x4C, 0x00, 0x03);          // INX / JMP $0300     (S)
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions();
        var compiler = new BlockCompiler(inner, space, new Fastmem(space, opts), opts);
        var cache = new BlockCache(space.PageCount);

        CompiledBlock p = cache.GetOrCompile(0x0200, compiler);
        cache.ResolveChain(0x0300, p, compiler);              // P chains into S; S compiled + linked
        Assert.Equal(2, compiler.CompileCount);
        Assert.Contains(p, cache.Chains.InboundTo(0x0300));   // inbound link recorded

        // Patch S's opcode (page $03 dirtied) + invalidate.
        space.Write8(0x0300, 0xCA);                           // INX -> DEX
        cache.Dirty.Mark(0x03);
        cache.InvalidateIfDirty();
        Assert.Empty(cache.Chains.InboundTo(0x0300));         // inbound link severed on eviction

        // P re-reaches its chain edge: ResolveChain misses (S was evicted) and recompiles S.
        cache.ResolveChain(0x0300, p, compiler);
        Assert.Equal(3, compiler.CompileCount);               // S recompiled from the modified bytes
    }

    [Fact]
    public void DisableChaining_plus_SMC_behaves_exactly_as_M2i()
    {
        // The same SMC seed-class with DisableChaining = true matches the interpreter (the flag
        // truly falls back to the M2-i dispatch path — every block returns to the dispatcher).
        var opts = new JitOptions { DisableChaining = true };
        for (int seed = 0; seed < 16; seed++)
        {
            byte[] image = SmcSeedProgram(seed);
            var rng = new Random(seed ^ 0x5A5A);
            string? diff = Diff(image, CodeBase,
                a: (byte)rng.Next(256), x: (byte)rng.Next(256), y: (byte)rng.Next(256),
                p: (byte)((rng.Next(256) & 0xEF) | 0x20), cycleCap: 200_000, opts);
            Assert.Null(diff);
        }
    }
}
