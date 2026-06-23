using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.2 Ground truth F.1 (Tasks 4-5) — the PORT I/O proof. A GENERATOR fixture (NOT a
/// shipped CPU, NOT the Z80) declaring an Io space + a PortIn/PortOut op, compiled via
/// GeneratorTestHost.CompileAndLoadType and DRIVEN at runtime: a PortIn reads the Io bus, a PortOut
/// writes the Io bus, NEVER the program/data bus. Divergent bytes on the two buses make a
/// wrong-bus body read the wrong value and fail. The 6502 declares no Io space and no port op, so
/// none of this perturbs it (byte-identical .g.cs, Ground truth E).</summary>
public class SyntheticPortIoTests
{
    private const string PortTestCpuSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("porttest")]
        public static class PortTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                // IN A,(n): read the Io bus at the (n) port operand into A. Length 2 (opcode + port byte).
                Insn(0xDB, "IN", AddrMode.IoPortImmediate, [PortIn("A")]),
                // OUT (n),A: write A to the Io bus at the (n) port operand. Length 2.
                Insn(0xD3, "OUT", AddrMode.IoPortImmediate, [PortOut("A")]),
                Insn(0xEA, "NOP", AddrMode.Implied, []),   // a benign terminator
            ];
        }

        // The hand-written partial: captures BOTH buses; ReadIo/WriteIo route to the Io space (A.4).
        public sealed partial class PortTestCpu
        {
            private readonly IAddressSpace _bus;
            private readonly IAddressSpace _ioBus;
            public PortTestCpu(IAddressSpace bus, IAddressSpace ioBus) { _bus = bus; _ioBus = ioBus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private byte ReadIo(uint port) { _cycles++; return _ioBus.Read8(port); }       // the Io targeting
            private void WriteIo(uint port, byte v) { _cycles++; _ioBus.Write8(port, v); } // the Io targeting
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    private static readonly Lazy<Type> s_cpu =
        new(() => GeneratorTestHost.CompileAndLoadType(PortTestCpuSpec, "SyntheticCpu.PortTestCpu"));

    /// <summary>Build a PortTestCpu over a program bus and an Io bus, both 16-bit RAM.</summary>
    private static (object cpu, AddressSpace program, AddressSpace io) NewCpu()
    {
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0, new byte[0x10000], writable: true);
        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        io.MapMemory(0, new byte[0x10000], writable: true);
        object cpu = Activator.CreateInstance(s_cpu.Value, program, io)!;
        return (cpu, program, io);
    }

    private static void SetA(object cpu, byte value) => s_cpu.Value.GetField("A")!.SetValue(cpu, value);
    private static byte GetA(object cpu) => (byte)s_cpu.Value.GetField("A")!.GetValue(cpu)!;
    private static void SetPc(object cpu, ushort pc) => s_cpu.Value.GetField("PC")!.SetValue(cpu, pc);
    private static long Cycles(object cpu) => (long)s_cpu.Value.GetProperty("CycleCount")!.GetValue(cpu)!;
    private static void Step(object cpu) => s_cpu.Value.GetMethod("Step")!.Invoke(cpu, null);

    // ── The abstraction generates clean ──────────────────────────────────────────────────────

    [Fact]
    public void Spec_generates_a_compiling_class_with_a_Port_arm()
    {
        var result = GeneratorTestHost.Run(PortTestCpuSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class PortTestCpu", result.GeneratedText);
        // The Port body calls the Io bus (ReadIo/WriteIo), never the program bus (ReadBus/WriteBus).
        Assert.Contains("ReadIo(port)", result.GeneratedText);
        Assert.Contains("WriteIo(port,", result.GeneratedText);
    }

    // ── IN reads the Io bus, not the program bus ──────────────────────────────────────────────

    [Fact]
    public void IN_reads_the_Io_bus_not_the_program_bus()
    {
        var (cpu, program, io) = NewCpu();
        // Program: IN A,(0x10) at 0x0200. Then divergent bytes at the SAME address on each bus.
        program.Write8(0x0200, 0xDB);     // IN opcode
        program.Write8(0x0201, 0x10);     // port operand (n) = 0x10
        program.Write8(0x0010, 0x99);     // program[0x10] = 0x99 (the WRONG value)
        io.Write8(0x0010, 0x42);          // Io[0x10]      = 0x42 (the RIGHT value — a port read)
        SetPc(cpu, 0x0200);
        SetA(cpu, 0x00);

        Step(cpu);

        // A must hold the Io-space byte (0x42); a body that hit the program bus would read 0x99.
        Assert.Equal(0x42, GetA(cpu));
    }

    [Fact]
    public void OUT_writes_the_Io_bus_not_the_program_bus()
    {
        var (cpu, program, io) = NewCpu();
        program.Write8(0x0200, 0xD3);     // OUT (n),A opcode
        program.Write8(0x0201, 0x10);     // port operand (n) = 0x10
        SetPc(cpu, 0x0200);
        SetA(cpu, 0x42);

        Step(cpu);

        Assert.Equal(0x42, io.Read8(0x0010));        // the Io space received the write
        Assert.Equal(0x00, program.Read8(0x0010));   // the program space is untouched
    }

    [Fact]
    public void Port_op_charges_the_Io_cycle()
    {
        var (cpu, program, io) = NewCpu();
        program.Write8(0x0200, 0xDB);     // IN A,(0x10)
        program.Write8(0x0201, 0x10);
        SetPc(cpu, 0x0200);

        long before = Cycles(cpu);
        Step(cpu);

        // opcode fetch (ReadBus) + (n) operand fetch (ReadBus) + the Io read (ReadIo) = 3 cycles.
        Assert.Equal(3, Cycles(cpu) - before);
    }

    // ── Task 5: the JIT never fastmems the Io space (Ground truth D) ──────────────────────────

    [Fact]
    public void Fastmem_never_serves_the_Io_space()
    {
        // Fastmem is built from ONE AddressSpace — the memory (Program) bus. The Io space is a
        // DIFFERENT object the Fastmem ctor never sees, so no Io page can ever be in PageBacking by
        // construction. Pin it: a Fastmem over a Program bus reflects the PROGRAM backing only.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        var programRam = new byte[0x10000];
        program.MapMemory(0, programRam, writable: true);

        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        var ioRam = new byte[0x10000];
        io.MapMemory(0, ioRam, writable: true);

        var fastmem = new Fastmem(program, new JitOptions());

        // Every backing page Fastmem holds is the Program RAM array, NEVER the Io RAM array — the
        // Io AddressSpace is not even an input to the Fastmem ctor, so its backing cannot leak in.
        foreach (var backing in fastmem.PageBacking)
            Assert.NotSame(ioRam, backing);
        // And the Program backing IS present (sanity: fastmem did bind the bus it was given).
        Assert.Contains(fastmem.PageBacking, b => ReferenceEquals(b, programRam));
    }

    /// <summary>The IN A,(n) descriptor for the direct EmitPort probe — Port class, IoPortImmediate,
    /// PortIn("A") (the 6502 has a register named A, so RegField resolves under the J1-deferred,
    /// Mos6502Cpu-typed BlockCompiler).</summary>
    private static OpcodeDescriptor PortInADescriptor() => new(
        0xDB, "IN", JitMode.IoPortImmediate, JitOpClass.Port,
        LengthRule.Fixed, FixedLength: 2, BaseCycles: 3, PageCrossPenalty: false,
        NeedsFallback: false, EndsBlock: false,
        Ops: [new JitOp("PortIn", "A", "", 0, false)]);

    private static OpcodeDescriptor PortOutADescriptor() => new(
        0xD3, "OUT", JitMode.IoPortImmediate, JitOpClass.Port,
        LengthRule.Fixed, FixedLength: 2, BaseCycles: 3, PageCrossPenalty: false,
        NeedsFallback: false, EndsBlock: false,
        Ops: [new JitOp("PortOut", "A", "", 0, false)]);

    [Fact]
    public void EmitPort_arm_hits_the_Io_bus_for_PortIn()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // JIT-only proof; skip where dynamic code is disabled (AOT)

        // Program bus (the fastmem-bound one, arg 1): IN A,(0x10) — PC points at the (n) operand.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0, new byte[0x10000], writable: true);
        // The probe charges the opcode fetch + EmitIncrementPC(1) (as a real block does), so PC must
        // start at the OPCODE position 0x0200; after the increment it lands on the (n) operand 0x0201.
        program.Write8(0x0201, 0x10);   // the (n) port operand byte
        program.Write8(0x0010, 0x99);   // program[0x10] = 0x99 — the WRONG value (a fastmem read)
        var inner = new Mos6502Cpu(program);
        inner.PC = 0x0200;
        inner.A = 0x00;

        // Io bus (arg 7): a SEPARATE AddressSpace holding the RIGHT value at the port.
        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        io.MapMemory(0, new byte[0x10000], writable: true);
        io.Write8(0x0010, 0x42);        // Io[0x10] = 0x42 — what a correct Io callout reads

        var fastmem = new Fastmem(program, new JitOptions());
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, program, fastmem, new JitOptions());
        BlockDelegate<Mos6502Cpu> probe = compiler.CompilePortProbe(PortInADescriptor());

        long budget = 100;
        probe(inner, program, fastmem, new DirtyMap(program.PageCount),
            (uint _, ref long _, out BlockExit e) => e = BlockExit.Normal,
            ref budget, out _, io);

        // A holds the Io byte (0x42) — the emitted arm called ioBus.Read8, NOT a fastmem/LoadByteFromBus
        // read of program[0x10] (which would have given 0x99).
        Assert.Equal(0x42, inner.A);
    }

    [Fact]
    public void EmitPort_arm_hits_the_Io_bus_for_PortOut()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;

        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0, new byte[0x10000], writable: true);
        program.Write8(0x0201, 0x10);   // the (n) port operand (PC starts at the opcode 0x0200)
        var inner = new Mos6502Cpu(program);
        inner.PC = 0x0200;
        inner.A = 0x42;

        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        io.MapMemory(0, new byte[0x10000], writable: true);

        var fastmem = new Fastmem(program, new JitOptions());
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, program, fastmem, new JitOptions());
        BlockDelegate<Mos6502Cpu> probe = compiler.CompilePortProbe(PortOutADescriptor());

        long budget = 100;
        probe(inner, program, fastmem, new DirtyMap(program.PageCount),
            (uint _, ref long _, out BlockExit e) => e = BlockExit.Normal,
            ref budget, out _, io);

        Assert.Equal(0x42, io.Read8(0x0010));        // the Io bus received the write
        Assert.Equal(0x00, program.Read8(0x0010));   // the program/fastmem space is untouched
    }
}
