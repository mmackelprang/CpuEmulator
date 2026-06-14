using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbBitTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("cbbit")]
        public static class CbbitSpec
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
                Prefixes: [new PrefixByte(0xCB)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xCB, 0x40, "BIT", AddrMode.Bit, [CbBit("BIT", 0, "B")]),
                Insn(0xCB, 0x78, "BIT", AddrMode.Bit, [CbBit("BIT", 7, "B")]),
                Insn(0xCB, 0x46, "BIT", AddrMode.Bit, [CbBit("BIT", 0, "(HL)")]),
            ];
        }

        public sealed partial class CbbitCpu
        {
            private readonly IAddressSpace _bus;
            public CbbitCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.CbbitCpu");
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
    public void BIT_0_B_when_bit_clear_sets_Z_and_H_clears_S()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x40);   // BIT 0,B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0x00); Set(cpu, t, "F", 0x01);  // C=1 pre-set
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);               // Z = 1 (bit clear)
        Assert.Equal(0x10, f & 0x10);               // H = 1
        Assert.Equal(0x00, f & 0x02);               // N = 0
        Assert.Equal(0x04, f & 0x04);               // P/V = Z = 1
        Assert.Equal(0x01, f & 0x01);               // C preserved
        Assert.Equal(0x00, f & 0x80);               // S = 0
        Assert.Equal(0x00, (byte)Get(cpu, t, "B")); // not written back
    }

    [Fact]
    public void BIT_7_B_when_bit_set_sets_S()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x78);   // BIT 7,B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0x80); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x40);               // Z = 0 (bit set)
        Assert.Equal(0x80, f & 0x80);               // S = 1 (y==7 && bit set)
    }

    [Fact]
    public void BIT_0_HL_takes_XY_from_W_high_byte_of_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x46);   // BIT 0,(HL)
        bus.Write8(0x4000, 0x01);                   // bit 0 set
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000);
        Set(cpu, t, "WZ", 0x2800);                 // W = 0x28 → bits 5 (0x20) + 3 (0x08) set
        Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x40);               // Z = 0 (bit 0 set)
        Assert.Equal(0x20, f & 0x20);               // Y from W bit5 = 1
        Assert.Equal(0x08, f & 0x08);               // X from W bit3 = 1
    }
}
