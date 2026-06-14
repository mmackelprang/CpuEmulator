using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdIoTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edio")]
        public static class EdioSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0x40, "IN",  AddrMode.Register, [EdIn("B")]),
                Insn(0xED, 0x41, "OUT", AddrMode.Register, [EdOut("B")]),
                Insn(0xED, 0x70, "IN",  AddrMode.Register, [EdIn("none")]),
                Insn(0xED, 0x71, "OUT", AddrMode.Register, [EdOut("zero")]),
            ];
        }

        public sealed partial class EdioCpu
        {
            private readonly IAddressSpace _bus;
            private readonly byte[] _io = new byte[0x10000];
            public byte Q;
            public EdioCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) => _io[p & 0xFFFF];
            private void WriteIo(uint p, byte v) => _io[p & 0xFFFF] = v;
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Mem, byte[] Io) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdioCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        var io = (byte[])t.GetField("_io", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cpu)!;
        return (cpu, t, bus, io);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void IN_B_C_reads_port_BC_sets_flags_and_WZ()
    {
        var (cpu, t, mem, io) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x40);     // IN B,(C)
        Set(cpu, t, "PC", 0); Set(cpu, t, "BC", 0x1234); Set(cpu, t, "F", 0x01);  // C preserved
        io[0x1234] = 0x80;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x80, (byte)Get(cpu, t, "B"));   // input -> B
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x80, f & 0x80);                 // S = 1 (bit7 of input)
        Assert.Equal(0x00, f & 0x40);                 // Z = 0
        Assert.Equal(0x00, f & 0x10);                 // H = 0
        Assert.Equal(0x00, f & 0x02);                 // N = 0
        Assert.Equal(0x01, f & 0x01);                 // C preserved
        Assert.Equal(0x1235u, (uint)Get(cpu, t, "WZ"));  // WZ = BC+1
    }

    [Fact]
    public void OUT_C_B_writes_port_BC_no_flags_sets_WZ()
    {
        var (cpu, t, mem, io) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x41);     // OUT (C),B
        Set(cpu, t, "PC", 0); Set(cpu, t, "BC", 0x5678); Set(cpu, t, "F", 0x5A);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x56, io[0x5678]);               // B (high of BC) -> port
        Assert.Equal(0x5A, (byte)Get(cpu, t, "F"));   // F unchanged
        Assert.Equal(0x5679u, (uint)Get(cpu, t, "WZ"));  // WZ = BC+1
    }

    [Fact]
    public void OUT_C_zero_writes_zero()
    {
        var (cpu, t, mem, io) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x71);     // OUT (C),0
        Set(cpu, t, "PC", 0); Set(cpu, t, "BC", 0x00AA);
        io[0x00AA] = 0xFF;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, io[0x00AA]);               // wrote 0
    }
}
