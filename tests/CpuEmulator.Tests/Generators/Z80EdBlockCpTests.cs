using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockCpTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edcp")]
        public static class EdcpSpec
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
                Insn(0xED, 0xA1, "CPI",  AddrMode.Implied, [EdBlock("CPI")]),
                Insn(0xED, 0xA9, "CPD",  AddrMode.Implied, [EdBlock("CPD")]),
                Insn(0xED, 0xB1, "CPIR", AddrMode.Implied, [EdBlock("CPIR")]),
            ];
        }

        public sealed partial class EdcpCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public EdcpCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) => _bus.Read8(a);
            private void WriteBus(uint a, byte v) => _bus.Write8(a, v);
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdcpCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void CPI_compares_sets_N_HL_inc_BC_dec_WZ_plus1()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA1);     // CPI
        bus.Write8(0x6000, 0x20);                     // (HL)
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0005);
        Set(cpu, t, "A", 0x20); Set(cpu, t, "WZ", 0x1000); Set(cpu, t, "F", 0x01);  // C preserved
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x6001u, (uint)Get(cpu, t, "HL"));
        Assert.Equal(0x0004u, (uint)Get(cpu, t, "BC"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                 // Z = 1 (A == (HL))
        Assert.Equal(0x02, f & 0x02);                 // N = 1
        Assert.Equal(0x04, f & 0x04);                 // P/V = (BC-1 != 0)
        Assert.Equal(0x01, f & 0x01);                 // C preserved
        Assert.Equal(0x00, f & 0x10);                 // H = 0 (no borrow)
        Assert.Equal(0x1001u, (uint)Get(cpu, t, "WZ"));// WZ + 1
    }

    [Fact]
    public void CPD_decrements_HL_WZ_minus1_and_sets_S_H_via_borrow()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA9);     // CPD
        bus.Write8(0x6000, 0x05);                     // (HL)
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0001);
        Set(cpu, t, "A", 0x00); Set(cpu, t, "WZ", 0x2000); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5FFFu, (uint)Get(cpu, t, "HL"));// HL-1
        Assert.Equal(0x0000u, (uint)Get(cpu, t, "BC"));// BC-1
        byte f = (byte)Get(cpu, t, "F");
        // A - (HL) = 0x00 - 0x05 = 0xFB: S=1, Z=0, half-borrow (0-5<0)=1, N=1, P/V=(BC-1==0)=0.
        Assert.Equal(0x80, f & 0x80);                 // S = 1
        Assert.Equal(0x00, f & 0x40);                 // Z = 0
        Assert.Equal(0x10, f & 0x10);                 // H = 1 (half-borrow)
        Assert.Equal(0x02, f & 0x02);                 // N = 1
        Assert.Equal(0x00, f & 0x04);                 // P/V = 0 (BC exhausted)
        Assert.Equal(0x1FFFu, (uint)Get(cpu, t, "WZ"));// WZ - 1
    }

    [Fact]
    public void CPIR_rewinds_when_BC_not_zero_and_no_match()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x200, 0xED); bus.Write8(0x201, 0xB1);   // CPIR at 0x200
        bus.Write8(0x6000, 0x11);                           // (HL) != A -> no match
        Set(cpu, t, "PC", 0x200); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0003);
        Set(cpu, t, "A", 0x99);
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x40);                       // Z = 0 (no match)
        Assert.Equal(0x200u, (uint)Get(cpu, t, "PC"));      // rewound (BC-1=2 != 0, no match)
        Assert.Equal(0x201u, (uint)Get(cpu, t, "WZ"));      // WZ = instruction-PC + 1
    }

    [Fact]
    public void CPIR_advances_on_match_even_if_BC_nonzero()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x200, 0xED); bus.Write8(0x201, 0xB1);   // CPIR
        bus.Write8(0x6000, 0x99);                           // (HL) == A -> match
        Set(cpu, t, "PC", 0x200); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0003);
        Set(cpu, t, "A", 0x99); Set(cpu, t, "WZ", 0x1234);
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                       // Z = 1 (match)
        Assert.Equal(0x202u, (uint)Get(cpu, t, "PC"));      // advanced (matched -> stop)
        Assert.Equal(0x1235u, (uint)Get(cpu, t, "WZ"));     // WZ = WZ + 1 (not the rewind value)
    }

    [Fact]
    public void CPIR_advances_when_BC_exhausted_no_match()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x200, 0xED); bus.Write8(0x201, 0xB1);
        bus.Write8(0x6000, 0x11);                           // no match
        Set(cpu, t, "PC", 0x200); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0001);
        Set(cpu, t, "A", 0x99);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x202u, (uint)Get(cpu, t, "PC"));      // advanced (BC-1 == 0 -> stop)
    }
}
