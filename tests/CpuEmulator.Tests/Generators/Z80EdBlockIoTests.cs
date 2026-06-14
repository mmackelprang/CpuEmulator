using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockIoTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edbio")]
        public static class EdbioSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0xA2, "INI",  AddrMode.Implied, [EdBlock("INI")]),
                Insn(0xED, 0xAA, "IND",  AddrMode.Implied, [EdBlock("IND")]),
                Insn(0xED, 0xA3, "OUTI", AddrMode.Implied, [EdBlock("OUTI")]),
                Insn(0xED, 0xB2, "INIR", AddrMode.Implied, [EdBlock("INIR")]),
                Insn(0xED, 0xB3, "OTIR", AddrMode.Implied, [EdBlock("OTIR")]),
            ];
        }

        public sealed partial class EdbioCpu
        {
            private readonly IAddressSpace _bus;
            private readonly byte[] _io = new byte[0x10000];
            public byte Q;
            public int Im;
            public EdbioCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) => _bus.Read8(a);
            private void WriteBus(uint a, byte v) => _bus.Write8(a, v);
            private byte ReadIo(uint p) => _io[p & 0xFFFF];
            private void WriteIo(uint p, byte v) => _io[p & 0xFFFF] = v;
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus, byte[] Io) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdbioCpu");
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
    public void INI_reads_port_to_HL_decrements_B_sets_WZ_origBCplus1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA2);     // INI
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x7000);
        Set(cpu, t, "BC", 0x0562);                    // B=0x05, C=0x62
        Set(cpu, t, "F", 0x00);
        io[0x0562] = 0x5B;                            // port (BC) byte
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5B, bus.Read8(0x7000));        // (HL) <- IN (C)
        Assert.Equal(0x0462u, (uint)Get(cpu, t, "BC"));// B-1
        Assert.Equal(0x7001u, (uint)Get(cpu, t, "HL"));// HL+1
        // WZ = (ORIGINAL BC) + 1 = 0x0562 + 1 = 0x0563  (vector-derived; uses B BEFORE the decrement).
        Assert.Equal(0x0563u, (uint)Get(cpu, t, "WZ"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f);                        // vector-derived flag byte for this case
        Assert.Equal(f, (byte)t.GetField("Q")!.GetValue(cpu)!);   // Q = F
    }

    [Fact]
    public void IND_decrements_HL_and_WZ_origBCminus1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xAA);     // IND
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x7000);
        Set(cpu, t, "BC", 0x0562);
        io[0x0562] = 0x5B;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5B, bus.Read8(0x7000));
        Assert.Equal(0x0462u, (uint)Get(cpu, t, "BC"));// B-1
        Assert.Equal(0x6FFFu, (uint)Get(cpu, t, "HL"));// HL-1
        // WZ = (ORIGINAL BC) - 1 = 0x0562 - 1 = 0x0561.
        Assert.Equal(0x0561u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void OUTI_writes_HL_to_port_decrements_B_sets_WZ_decBCplus1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA3);     // OUTI
        bus.Write8(0x7000, 0x5B);                     // (HL) -> output byte
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x7000);
        Set(cpu, t, "BC", 0x0562);
        Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5B, io[0x0462]);               // OUT (C) <- (HL); port = BC AFTER B-- = 0x0462
        Assert.Equal(0x0462u, (uint)Get(cpu, t, "BC"));// B-1
        Assert.Equal(0x7001u, (uint)Get(cpu, t, "HL"));// HL+1
        // WZ = (DECREMENTED BC) + 1 = 0x0462 + 1 = 0x0463  (vector-derived; OUT uses B AFTER the decrement).
        Assert.Equal(0x0463u, (uint)Get(cpu, t, "WZ"));
        byte f = (byte)Get(cpu, t, "F");
        // B_after=0x04, outbyte=0x5B (N=0), L_after=0x01, k=0x5B+0x01=0x5C (H=C=0),
        // P/V=parity((0x5C&7)^0x04)=parity(4^4=0)=even=1 -> P set. f=0x04.
        Assert.Equal(0x04, f);
    }

    [Fact]
    public void INI_reads_one_port_entry_with_correct_address()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA2);
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x8000);
        Set(cpu, t, "BC", 0x1234);
        io[0x1234] = 0x77;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x77, bus.Read8(0x8000));        // the port at BC=0x1234 was read into (HL)
        Assert.Equal(0x1134u, (uint)Get(cpu, t, "BC"));
    }

    [Fact]
    public void INIR_rewinds_PC_when_B_not_zero()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0x300, 0xED); bus.Write8(0x301, 0xB2);   // INIR at 0x300
        Set(cpu, t, "PC", 0x300); Set(cpu, t, "HL", 0x9000);
        Set(cpu, t, "BC", 0x0362);                          // B=3 -> B-1=2 != 0 -> repeat
        io[0x0362] = 0x10;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x300u, (uint)Get(cpu, t, "PC"));      // rewound
        Assert.Equal(0x301u, (uint)Get(cpu, t, "WZ"));      // WZ = instruction-PC + 1 (rewind overrides)
    }

    [Fact]
    public void INIR_advances_PC_on_final_iteration()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0x300, 0xED); bus.Write8(0x301, 0xB2);
        Set(cpu, t, "PC", 0x300); Set(cpu, t, "HL", 0x9000);
        Set(cpu, t, "BC", 0x0162);                          // B=1 -> B-1=0 -> stop
        io[0x0162] = 0x10;
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x302u, (uint)Get(cpu, t, "PC"));      // advanced
    }
}
