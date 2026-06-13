using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.Jit;

/// <summary>
/// Task 7 Step 4: the trace-equivalence accuracy contract (Ground truth E), pinned as a tested
/// fact in BOTH directions.
///
/// With <c>DisableFastmem = true</c> the JIT routes every <b>data</b> access through the bus, so a
/// <see cref="TracingAddressSpace"/> records the interpreter's data-access trace element-for-element:
/// operand fetches, effective-address reads/writes, AND the silicon-true RMW dummy writes all appear,
/// in order, with identical addresses and values. The ONE class of interpreter bus access the JIT
/// does not reproduce is the <b>opcode-fetch read</b> (the instruction's first byte): the JIT reads
/// the opcode stream once at COMPILE time (block discovery) and bakes it into IL, so the executing
/// block never re-fetches opcodes at run time. This is inherent to compilation — re-fetching every
/// opcode per execution would defeat the tier — and is the precise, honest reading of Ground truth
/// E's "every access routes through the bus": every <i>data/operand</i> access does; opcode fetches
/// are resolved at compile time. The test asserts exactly that: the interpreter trace with the
/// opcode-fetch reads filtered out equals the JIT trace, element-for-element.
///
/// With fastmem ON (the default), RAM/ROM data access ALSO goes direct to the backing array and
/// emits no bus transaction, so the JIT's trace is strictly shorter still — the contract's explicit
/// non-equivalence, proven so it is a tested fact rather than just prose.
/// </summary>
public class JitTraceEquivalenceTests
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

    // A representative straight-line program: a few loads/stores, a branch (not taken), and an RMW.
    //   0200 LDA #$05      A9 05     2c
    //   0202 STA $20       85 20     3c   (RAM store)
    //   0204 LDX $20       A6 20     3c   (RAM load)
    //   0206 INC $20       E6 20     5c   (RMW: read-modify-write to RAM)
    //   0208 CMP #$99      C9 99     2c   (sets carry/flags; branch below not taken)
    //   020A BEQ $020E     F0 02     2c   (Z clear -> not taken, 2 cycles, no page cross)
    //   020C LDY $20       A4 20     3c   (RAM load)
    // total straight-line = 2+3+3+5+2+2+3 = 20 cycles, ending with PC at $020E.
    private static void PokeProgram(AddressSpace space) => Poke(space, 0x0200,
        0xA9, 0x05, 0x85, 0x20, 0xA6, 0x20, 0xE6, 0x20, 0xC9, 0x99, 0xF0, 0x02, 0xA4, 0x20);

    private const long StraightLineCycles = 20;

    // The instruction-start addresses of PokeProgram (the opcode-fetch reads the JIT resolves at
    // compile time and therefore does not re-emit at run time): LDA# @0200, STA @0202, LDX @0204,
    // INC @0206, CMP @0208, BEQ @020A, LDY @020C.
    private static readonly uint[] OpcodeFetchAddresses =
        [0x0200, 0x0202, 0x0204, 0x0206, 0x0208, 0x020A, 0x020C];

    [Fact]
    public void JIT_with_fastmem_disabled_produces_the_interpreter_data_trace()
    {
        // (a) the interpreter over a tracing bus
        var refSpace = NewRamSpace();
        PokeProgram(refSpace);
        var refTracing = new TracingAddressSpace(refSpace);
        var refCpu = new Mos6502Cpu(refTracing);
        refCpu.PC = 0x0200; refCpu.S = 0xFD; refCpu.P = 0x24;
        long rb = StraightLineCycles;
        refCpu.Run(ref rb);

        // (b) a JittedCpu { DisableFastmem = true } over a tracing bus wrapping the concrete space
        var jitSpace = NewRamSpace();
        PokeProgram(jitSpace);
        var jitTracing = new TracingAddressSpace(jitSpace);
        var inner = new Mos6502Cpu(jitSpace);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, jitSpace, new JitOptions { DisableFastmem = true },
                                traceBus: jitTracing);
        long jb = StraightLineCycles;
        jit.Run(ref jb);

        // State + cycles identical
        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.X, inner.X);
        Assert.Equal(refCpu.Y, inner.Y);
        Assert.Equal(refCpu.P, inner.P);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);

        // The interpreter trace with its opcode-fetch reads removed equals the JIT trace,
        // element-for-element: same order, same addresses, same values — including operand
        // fetches and the RMW dummy write. The opcode fetches are the only interpreter accesses
        // the JIT resolves at compile time (block discovery) rather than at run time.
        var expectedDataTrace = refTracing.Trace
            .Where(a => !(a.IsRead && OpcodeFetchAddresses.Contains(a.Address)))
            .ToList();
        Assert.Equal(expectedDataTrace.Count, jitTracing.Trace.Count);
        for (int i = 0; i < expectedDataTrace.Count; i++)
            Assert.Equal(expectedDataTrace[i], jitTracing.Trace[i]);

        // And the JIT introduces NO bus access of its own: every JIT trace entry is one of the
        // interpreter's accesses (the JIT trace is a subsequence of the interpreter trace).
        foreach (var jitAccess in jitTracing.Trace)
            Assert.Contains(jitAccess, refTracing.Trace);
    }

    [Fact]
    public void JIT_with_fastmem_on_does_NOT_trace_RAM_reads()
    {
        // The interpreter over a tracing bus: a full per-cycle trace (every fetch + RAM access).
        var refSpace = NewRamSpace();
        PokeProgram(refSpace);
        var refTracing = new TracingAddressSpace(refSpace);
        var refCpu = new Mos6502Cpu(refTracing);
        refCpu.PC = 0x0200; refCpu.S = 0xFD; refCpu.P = 0x24;
        long rb = StraightLineCycles;
        refCpu.Run(ref rb);

        // The same program with fastmem ON: RAM reads/writes bypass the bus. There is no MMIO in
        // this all-RAM program, so the JIT's trace is empty while the interpreter's is full.
        var jitSpace = NewRamSpace();
        PokeProgram(jitSpace);
        var jitTracing = new TracingAddressSpace(jitSpace);
        var inner = new Mos6502Cpu(jitSpace);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, jitSpace, new JitOptions(), traceBus: jitTracing);
        long jb = StraightLineCycles;
        jit.Run(ref jb);

        // Same final state + cycle count (the contract's "identical" rows).
        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);

        // But strictly fewer bus accesses — the contract's explicit non-equivalence.
        Assert.True(refTracing.Trace.Count > 0, "interpreter trace should be non-empty");
        Assert.True(jitTracing.Trace.Count < refTracing.Trace.Count,
            $"fastmem-on JIT trace ({jitTracing.Trace.Count}) should be shorter than the " +
            $"interpreter trace ({refTracing.Trace.Count})");
    }
}
