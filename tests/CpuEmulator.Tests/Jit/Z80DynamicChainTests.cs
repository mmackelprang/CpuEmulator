using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0023 D2 — the Z80 dynamic return edges (RET / RET cc taken) now CHAIN on the runtime
/// popped PC instead of round-tripping to the dispatcher. This is the headline arm: on the Spectrum 48K
/// boot every CALL chained in (static entry) but every matching RET round-tripped out; chaining the RET
/// lifts chain:disp from 0.28 and crosses the interpreter.
///
/// Two gates per ADR §7.2:
/// (a) PARITY — the same program run chaining-ON and chaining-OFF (JitOptions.DisableChaining) reaches a
///     byte-identical CPU state + cycles + RAM (the differential cross-check the ADR calls red→green: a
///     RET that chains must produce the SAME result as a RET that round-trips).
/// (b) NON-VACUITY — the RET edge actually chains (ChainStepCount rises), so the parity gate is not a
///     no-op (it would false-pass if RET still round-tripped).</summary>
public class Z80DynamicChainTests
{
    private static AddressSpace NewRamBus()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        return bus;
    }

    private static void Poke(AddressSpace bus, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++) bus.Write8((uint)(at + i), bytes[i]);
    }

    /// <summary>Run a program through JittedCpu&lt;Z80Cpu&gt; with the given chaining option and return
    /// (cpu, bus, ChainStepCount). SP/PC seeded by the caller via <paramref name="seed"/>.</summary>
    private static (Z80Cpu cpu, AddressSpace bus, long chains) RunJit(
        System.Action<AddressSpace> poke, System.Action<Z80Cpu> seed, long budget, JitOptions opts)
    {
        var bus = NewRamBus();
        poke(bus);
        var inner = new Z80Cpu(bus);
        seed(inner);
        var jit = new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus, options: opts);
        long b = budget;
        jit.Run(ref b);
        return (inner, bus, jit.ChainStepCount);
    }

    private static (Z80Cpu cpu, AddressSpace bus) RunInterp(
        System.Action<AddressSpace> poke, System.Action<Z80Cpu> seed, long budget)
    {
        var bus = NewRamBus();
        poke(bus);
        var cpu = new Z80Cpu(bus);
        seed(cpu);
        long b = budget;
        cpu.Run(ref b);
        return (cpu, bus);
    }

    private static void AssertSameState(Z80Cpu expected, Z80Cpu actual, AddressSpace eBus, AddressSpace aBus)
    {
        foreach (string r in expected.RegisterNames)
            Assert.Equal(expected.GetRegister(r), actual.GetRegister(r));
        Assert.Equal(expected.CycleCount, actual.CycleCount);
        for (uint a = 0; a <= 0xFFFF; a++)
            Assert.Equal(eBus.Read8(a), aBus.Read8(a));
    }

    // ── CALL/RET: the canonical return edge. CALL chains in; RET now chains out. ──────────────────
    private static void PokeCallRet(AddressSpace bus)
    {
        // 0x8000  CALL 0x9000          (CD 00 90) — chains to the subroutine
        // 0x8003  LD A,0x42            (3E 42)    — the return lands here
        // 0x8005  HALT                 (76)
        // 0x9000  LD B,0x07            (06 07)
        // 0x9002  RET                  (C9)       — pops 0x8003; DYNAMIC → now chains
        Poke(bus, 0x8000, 0xCD, 0x00, 0x90);
        Poke(bus, 0x8003, 0x3E, 0x42);
        Poke(bus, 0x8005, 0x76);
        Poke(bus, 0x9000, 0x06, 0x07);
        Poke(bus, 0x9002, 0xC9);
    }

    private static void SeedTop(Z80Cpu cpu)
    {
        cpu.SetRegister("PC", 0x8000);
        cpu.SetRegister("SP", 0xFFF0);
    }

    [Fact]
    public void Ret_chains_on_the_popped_PC()
    {
        var (cpu, _, chains) = RunJit(PokeCallRet, SeedTop, budget: 200, opts: new JitOptions());
        Assert.True(chains > 0, "the CALL chained in AND the RET chained out (dynamic edge now chains)");
        Assert.Equal(0x42ul, cpu.GetRegister("A"));   // executed past the return — control flowed back
        Assert.Equal(0x07ul, cpu.GetRegister("B"));
    }

    [Fact]
    public void Ret_chaining_is_byte_identical_to_round_tripping()
    {
        // (a) chaining ON vs OFF — the dynamic RET edge must produce the SAME result either way.
        var (on, onBus, onChains) = RunJit(PokeCallRet, SeedTop, 200, new JitOptions());
        var (off, offBus, offChains) =
            RunJit(PokeCallRet, SeedTop, 200, new JitOptions { DisableChaining = true });
        AssertSameState(off, on, offBus, onBus);
        Assert.True(onChains > offChains, "chaining-ON took more chain steps than chaining-OFF (the RET chained)");

        // (b) and both match the interpreter oracle.
        var (refCpu, refBus) = RunInterp(PokeCallRet, SeedTop, 200);
        AssertSameState(refCpu, on, refBus, onBus);
    }

    // ── RET cc (conditional): taken arm chains on the popped PC; not-taken still chains the static FT. ──
    private static void PokeCallRetCc(AddressSpace bus)
    {
        // 0x8000  CALL 0x9000          (CD 00 90)
        // 0x8003  LD A,0x55            (3E 55)
        // 0x8005  HALT                 (76)
        // 0x9000  OR A                 (B7)       — clears Z (A is non-zero after the CALL? seed A!=0)
        // 0x9001  RET NZ               (C0)       — Z clear → taken; pops 0x8003; DYNAMIC → now chains
        // 0x9002  HALT                 (76)       — guard (only reached if RET NZ not taken)
        Poke(bus, 0x8000, 0xCD, 0x00, 0x90);
        Poke(bus, 0x8003, 0x3E, 0x55);
        Poke(bus, 0x8005, 0x76);
        Poke(bus, 0x9000, 0xB7);
        Poke(bus, 0x9001, 0xC0);
        Poke(bus, 0x9002, 0x76);
    }

    private static void SeedTopAnz(Z80Cpu cpu)
    {
        cpu.SetRegister("PC", 0x8000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("A", 0x01);   // non-zero so OR A clears Z → RET NZ taken
    }

    [Fact]
    public void Ret_cc_taken_chains_and_is_byte_identical()
    {
        var (on, onBus, onChains) = RunJit(PokeCallRetCc, SeedTopAnz, 200, new JitOptions());
        var (off, offBus, _) =
            RunJit(PokeCallRetCc, SeedTopAnz, 200, new JitOptions { DisableChaining = true });
        var (refCpu, refBus) = RunInterp(PokeCallRetCc, SeedTopAnz, 200);

        Assert.True(onChains > 0, "RET NZ taken chained on the popped PC");
        Assert.Equal(0x55ul, on.GetRegister("A"));   // the return landed and ran LD A,0x55
        AssertSameState(off, on, offBus, onBus);      // chaining ON == OFF
        AssertSameState(refCpu, on, refBus, onBus);   // == interpreter oracle
    }
}
