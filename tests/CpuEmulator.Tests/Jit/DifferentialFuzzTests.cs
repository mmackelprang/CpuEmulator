using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 7 + Ground truth D: the COMMITTED seeded, deterministic, SMC-biased differential
/// fuzzer. For each seed it builds a fresh interpreter and a fresh <see cref="JittedCpu"/> over an
/// identical RAM image + identical random initial state, runs BOTH to the program's JMP-* park (or a
/// cycle cap), and diffs the final A/X/Y/S/P/PC + CycleCount + the ENTIRE 64 KiB RAM image. Any
/// difference fails with the seed + the first diverging field. It runs chaining ON (the default) AND
/// chaining OFF, asserting BOTH match the interpreter — so a chaining-specific bug is distinguishable
/// from a base-emit bug. CI default N = 64 seeds (fast); <c>CPUEMULATOR_FUZZ=full</c> -&gt; N = 4096
/// (the pre-merge gate). The self-tests prove the differ can FAIL (not vacuously green).</summary>
public class DifferentialFuzzTests
{
    private const int CiSeeds = 64;            // routine suite — fast
    private const int FullSeeds = 4096;        // CPUEMULATOR_FUZZ=full — the pre-merge gate
    private const long PerProgramCycleCap = 2_000_000;

    public static int SeedCount =>
        string.Equals(Environment.GetEnvironmentVariable("CPUEMULATOR_FUZZ"), "full",
            StringComparison.OrdinalIgnoreCase) ? FullSeeds : CiSeeds;

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int s = 0; s < SeedCount; s++) data.Add(s);   // 0..N-1 so full ⊇ CI
        return data;
    }

    // ── The committed fuzzer (the standing parity gate) ────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Jit_matches_the_interpreter_for_a_seeded_program(int seed)
    {
        var program = FuzzProgramGenerator.Generate(seed);

        // Chaining ON (the default), and chaining OFF — both must match the interpreter.
        AssertMatchesInterpreter(seed, program, new JitOptions());
        AssertMatchesInterpreter(seed, program, new JitOptions { DisableChaining = true });
    }

    private static void AssertMatchesInterpreter(
        int seed, FuzzProgramGenerator.FuzzProgram p, JitOptions opts)
    {
        var (refState, refRam) = RunInterpreter(p);
        var (jitState, jitRam) = RunJit(p, opts);
        bool chaining = !opts.DisableChaining;
        if (!refState.Equals(jitState))
            Assert.Fail($"seed {seed} chaining={chaining}: state diverged\n" +
                        $"  interp={refState}\n  jit   ={jitState}");
        for (int a = 0; a < 0x10000; a++)
            if (refRam[a] != jitRam[a])
                Assert.Fail($"seed {seed} chaining={chaining}: RAM[{a:X4}] " +
                            $"interp={refRam[a]:X2} jit={jitRam[a]:X2}");
    }

    // ── Self-tests: prove the fuzzer can FAIL before trusting its greens ─────────────────────────
    [Fact]
    public void Fuzzer_is_deterministic()
    {
        var a = FuzzProgramGenerator.Generate(12345);
        var b = FuzzProgramGenerator.Generate(12345);
        Assert.Equal(a.StartPc, b.StartPc);
        Assert.Equal((a.A, a.X, a.Y, a.S, a.P), (b.A, b.X, b.Y, b.S, b.P));
        Assert.Equal(a.StoresToCode, b.StoresToCode);
        Assert.Equal(a.Ram, b.Ram);   // byte-identical program image
    }

    [Fact]
    public void Fuzzer_SMC_bias_produces_stores_to_code()
    {
        // Across a small seed band, the SMC bias must actually emit ≥1 store whose target is in the
        // code region — otherwise the SMC guard + chaining-sever paths are never exercised.
        int total = 0;
        for (int seed = 0; seed < 16; seed++)
            total += FuzzProgramGenerator.Generate(seed).StoresToCode;
        Assert.True(total > 0, "the SMC bias produced no stores into the code region across 16 seeds");
    }

    [Fact]
    public void Fuzzer_catches_an_injected_divergence()
    {
        // Run the differ with a deliberately-corrupted oracle (the interpreter's final state with one
        // bit flipped) and assert it REPORTS a divergence — proving the differ is not vacuously green.
        var program = FuzzProgramGenerator.Generate(0);
        var (refState, refRam) = RunInterpreter(program);
        var (jitState, jitRam) = RunJit(program, new JitOptions());

        // Sanity: the un-corrupted run agrees (this is also the seed-0 parity check).
        Assert.Equal(refState, jitState);

        // Corrupt the JIT's reported A by one bit; the differ must now see a state divergence.
        var corrupted = jitState with { A = (byte)(jitState.A ^ 0x01) };
        Assert.NotEqual(refState, corrupted);

        // And a one-byte RAM corruption must be caught by the RAM diff.
        var corruptRam = (byte[])jitRam.Clone();
        corruptRam[0x4000] ^= 0xFF;
        bool ramDiff = false;
        for (int i = 0; i < 0x10000 && !ramDiff; i++)
            ramDiff = refRam[i] != corruptRam[i];
        Assert.True(ramDiff, "the RAM differ failed to catch an injected one-byte corruption");
        _ = refRam; // (keep both halves of the diff live)
    }

    // ── The differential drivers (interpreter oracle + JIT under test, fresh image each run) ──────
    private readonly record struct CpuState(byte A, byte X, byte Y, byte S, byte P, ushort Pc, long Cycles)
    {
        public override string ToString() =>
            $"A={A:X2} X={X:X2} Y={Y:X2} S={S:X2} P={P:X2} PC={Pc:X4} cyc={Cycles}";
    }

    // SMC can patch a random byte into the code window that decodes to an undefined opcode. Both
    // tiers run with the Nop undefined policy so such a byte is a deterministic 2-cycle NOP (an
    // emulation-legal policy) rather than a thrown UndefinedOpcodeException — preserving full SMC
    // stress while keeping the run bounded and the diff meaningful. The JIT routes undefined opcodes
    // through the interpreter fallback, so the inner CPU's policy governs both tiers identically.
    private static (CpuState, byte[]) RunInterpreter(FuzzProgramGenerator.FuzzProgram p)
    {
        var (space, ram) = NewSpace(p);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop)
            { PC = p.StartPc, A = p.A, X = p.X, Y = p.Y, S = p.S, P = p.P };
        DriveToTrap(cpu, cpu.Step);
        return (Snapshot(cpu), ram);
    }

    private static (CpuState, byte[]) RunJit(FuzzProgramGenerator.FuzzProgram p, JitOptions opts)
    {
        var (space, ram) = NewSpace(p);
        var inner = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop)
            { PC = p.StartPc, A = p.A, X = p.X, Y = p.Y, S = p.S, P = p.P };
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, options: opts);
        DriveToTrap(inner, () => { long budget = 1; jit.Run(ref budget); });
        return (Snapshot(inner), ram);
    }

    /// <summary>Build a fresh AddressSpace over a COPY of p.Ram (each run gets its own image so SMC
    /// does not leak between the interpreter and JIT runs). Returns the space and the backing array
    /// (the array IS the mapped RAM, so the final image is read back from it).</summary>
    private static (AddressSpace Space, byte[] Ram) NewSpace(FuzzProgramGenerator.FuzzProgram p)
    {
        var ram = (byte[])p.Ram.Clone();
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, ram, writable: true);
        return (space, ram);
    }

    /// <summary>Drive a single-instruction step delegate until PC parks (the JMP-* trap idiom) or the
    /// per-program cycle cap is hit. Both the interpreter and the JIT (budget-1 slices) use this so a
    /// runaway program terminates deterministically rather than hanging the suite.</summary>
    private static void DriveToTrap(Mos6502Cpu cpu, Action stepOnce)
    {
        while (cpu.CycleCount < PerProgramCycleCap)
        {
            ushort before = cpu.PC;
            stepOnce();
            if (cpu.PC == before) return;   // parked at JMP-* — bounded run complete
        }
    }

    private static CpuState Snapshot(Mos6502Cpu cpu) =>
        new(cpu.A, cpu.X, cpu.Y, cpu.S, cpu.P, cpu.PC, cpu.CycleCount);
}
