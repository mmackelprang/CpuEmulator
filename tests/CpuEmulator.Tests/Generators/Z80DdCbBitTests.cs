using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbBitTests
{
    // Two BIT rows: BIT 0,(IX+d) at 0xDDCB46, and BIT 0,(IX+d) at 0xDDCB40 (z=0) — z is ignored for BIT,
    // so 0x40 and 0x46 must produce the IDENTICAL result (no store-copy).
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcbbit")]
        public static class DdCbBitSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0xCB, 0x46, "BIT", AddrMode.Indexed, [DdCb("BIT", 0, "-")]),
                Insn(0xDD, 0xCB, 0x40, "BIT", AddrMode.Indexed, [DdCb("BIT", 0, "-")]),
                Insn(0xDD, 0xCB, 0x7E, "BIT", AddrMode.Indexed, [DdCb("BIT", 7, "-")]),
            ];
        }

        public sealed partial class DdCbBitCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public DdCbBitCpu(IAddressSpace bus) { _bus = bus; }
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

    private static (object Cpu, System.Type T, IAddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.DdCbBitCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, System.Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, System.Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void BIT_b_IXplusd_XY_from_EA_high_byte_not_the_value()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 46 = BIT 0,(IX+d). IX=0x2800, d=0x05 -> EA=0x2805. EA>>8 = 0x28 = 0b00101000:
        // bit5 (Y) = 1, bit3 (X) = 1. mem[EA] = 0x00 (NO X/Y bits set in the value) — proving X/Y come
        // from the EA high byte, not the value. bit 0 of 0x00 is clear -> Z=1, P=1.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x46);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x2800); Set(cpu, t, "F", 0x01);  // C set (preserved)
        bus.Write8(0x2805, 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, bus.Read8(0x2805));                 // BIT does not write memory
        Assert.Equal(0x2805u, (uint)Get(cpu, t, "WZ"));        // WZ = EA
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                          // Z = 1 (bit 0 clear)
        Assert.Equal(0x10, f & 0x10);                          // H = 1
        Assert.Equal(0x04, f & 0x04);                          // P/V = Z = 1
        Assert.Equal(0x00, f & 0x02);                          // N = 0
        Assert.Equal(0x01, f & 0x01);                          // C preserved
        Assert.Equal(0x20, f & 0x20);                          // Y from EA>>8 bit5 = 1 (NOT the value)
        Assert.Equal(0x08, f & 0x08);                          // X from EA>>8 bit3 = 1 (NOT the value)
        Assert.Equal(0x00, f & 0x80);                          // S = 0 (bit 0, not bit 7)
        Assert.Equal(f, (byte)t.GetField("Q")!.GetValue(cpu)!);  // Q = written F (BIT writes flags)
    }

    [Fact]
    public void BIT_ignores_z_low_bits_0x40_equals_0x46()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 40 = BIT 0,(IX+d), z=0. Must be IDENTICAL to 0x46 (z is ignored for BIT, no store-copy).
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x40);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x2800); Set(cpu, t, "F", 0x01);
        Set(cpu, t, "B", 0xAA);
        bus.Write8(0x2805, 0x01);                              // bit 0 SET -> Z=0
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x40);                          // Z = 0 (bit 0 set)
        Assert.Equal(0xAAu, Get(cpu, t, "B"));                 // B UNTOUCHED — BIT never copies, even z=0
        Assert.Equal(0x2805u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void BIT_7_sets_S_when_bit7_set()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 7E = BIT 7,(IX+d). mem[EA] = 0x80 -> bit 7 set -> Z=0, S=1.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x7E);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x2800);
        bus.Write8(0x2805, 0x80);
        t.GetMethod("Step")!.Invoke(cpu, null);
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x40);                          // Z = 0 (bit 7 set)
        Assert.Equal(0x80, f & 0x80);                          // S = 1 (y==7 && bit set)
    }
}
