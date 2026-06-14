using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdNegTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edneg")]
        public static class EdNegSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0x44, "NEG", AddrMode.Implied, [EdNeg()]),
            ];
        }

        public sealed partial class EdNegCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public EdNegCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) => 0;
            private void WriteIo(uint p, byte v) { }
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Mem) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdNegCpu");
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
    public void NEG_of_1_is_FF_sets_S_C_N()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x44);                 // NEG
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x01); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xFF, (byte)Get(cpu, t, "A"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x80, f & 0x80);                 // S
        Assert.Equal(0x01, f & 0x01);                 // C = (A!=0)
        Assert.Equal(0x02, f & 0x02);                 // N = 1
        Assert.Equal(0x10, f & 0x10);                 // H (borrow from bit4)
    }

    [Fact]
    public void NEG_of_0_is_0_sets_Z_clears_C()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x44);
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x00); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, (byte)Get(cpu, t, "A"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                 // Z = 1
        Assert.Equal(0x00, f & 0x01);                 // C = 0
        Assert.Equal(0x02, f & 0x02);                 // N = 1
    }

    [Fact]
    public void NEG_of_80_overflows()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x44);
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x80); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x80, (byte)Get(cpu, t, "A"));   // -(-128) = -128
        Assert.Equal(0x04, (byte)Get(cpu, t, "F") & 0x04);   // P/V = 1 (A==0x80)
    }
}
