using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbResSetTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcbrs")]
        public static class DdCbResSetSpec
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
                Insn(0xDD, 0xCB, 0x86, "RES", AddrMode.Indexed, [DdCb("RES", 0, "-")]),
                Insn(0xDD, 0xCB, 0x80, "RES", AddrMode.Indexed, [DdCb("RES", 0, "B")]),
                Insn(0xDD, 0xCB, 0xC6, "SET", AddrMode.Indexed, [DdCb("SET", 0, "-")]),
            ];
        }

        public sealed partial class DdCbResSetCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public DdCbResSetCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.DdCbResSetCpu");
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
    public void RES_b_IXplusd_clears_bit_preserves_F_and_ends_Q_zero()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 86 = RES 0,(IX+d), z=6 (no copy). mem[EA]=0xFF -> clear bit 0 -> 0xFE. F preserved, Q=0.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x86);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000); Set(cpu, t, "F", 0x55); Set(cpu, t, "B", 0xAA);
        bus.Write8(0x4005, 0xFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xFE, bus.Read8(0x4005));                 // bit 0 cleared
        Assert.Equal(0x4005u, (uint)Get(cpu, t, "WZ"));        // WZ = EA
        Assert.Equal(0x55u, Get(cpu, t, "F"));                 // F PRESERVED (RES does not touch flags)
        Assert.Equal(0xAAu, Get(cpu, t, "B"));                 // B UNTOUCHED (z=6, no copy)
        Assert.Equal(0x00, (byte)t.GetField("Q")!.GetValue(cpu)!);  // Q = 0 (RES preserves F -> Q ends 0)
    }

    [Fact]
    public void LD_B_RES_b_IXplusd_copies_result_to_register()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 80 = LD B,RES 0,(IX+d), z=0 (copy B). mem[EA]=0xFF -> 0xFE; B AND mem get 0xFE.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x80);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000); Set(cpu, t, "B", 0xAA);
        bus.Write8(0x4005, 0xFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xFE, bus.Read8(0x4005));                 // memory gets the RMW result
        Assert.Equal(0xFEu, Get(cpu, t, "B"));                 // B gets the RMW result (undoc store-copy)
        Assert.Equal(0x00, (byte)t.GetField("Q")!.GetValue(cpu)!);  // Q = 0
    }

    [Fact]
    public void SET_b_IXplusd_sets_bit()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 C6 = SET 0,(IX+d). mem[EA]=0x00 -> set bit 0 -> 0x01.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0xC6);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000);
        bus.Write8(0x4005, 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, bus.Read8(0x4005));                 // bit 0 set
        Assert.Equal(0x4005u, (uint)Get(cpu, t, "WZ"));
    }
}
