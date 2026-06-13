using System.Reflection;
using CpuEmulator.Core;

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
}
