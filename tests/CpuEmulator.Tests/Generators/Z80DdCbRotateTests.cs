using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbRotateTests
{
    // The synthetic DDCB spec: a DD prefix compounding with CB (displacement-before-opcode), and two
    // rotate rows — RLC (IX+d) (z=6, no copy) at key 0xDDCB06, and LD B,RLC (IX+d) (z=0) at key 0xDDCB00.
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdCbSpec
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
                Insn(0xDD, 0xCB, 0x06, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "-")]),
                Insn(0xDD, 0xCB, 0x00, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "B")]),
            ];
        }

        public sealed partial class DdCbCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public DdCbCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.DdCbCpu");
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
    public void RLC_IXplusd_rotates_memory_sets_wz_and_does_not_copy_when_z_is_6()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 06 = RLC (IX+d), z=6 (no copy). IX=0x4000, d=0x05 -> EA=0x4005. mem[EA]=0x80 -> RLC -> 0x01, C=1.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x06);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000); Set(cpu, t, "B", 0x55);
        bus.Write8(0x4005, 0x80);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, bus.Read8(0x4005));                 // 0x80 RLC -> 0x01
        Assert.Equal(0x4005u, (uint)Get(cpu, t, "WZ"));        // WZ = EA
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x01, f & 0x01);                          // C = 1 (bit7 out)
        Assert.Equal(0x00, f & 0x40);                          // Z = 0
        Assert.Equal(0x00, f & 0x10);                          // H = 0
        Assert.Equal(0x00, f & 0x02);                          // N = 0
        Assert.Equal(0x55u, Get(cpu, t, "B"));                 // B UNTOUCHED (z=6, no copy)
        Assert.Equal(f, (byte)t.GetField("Q")!.GetValue(cpu)!);  // Q = written F (rotate writes flags)
    }

    [Fact]
    public void LD_B_RLC_IXplusd_rotates_memory_and_copies_result_to_B()
    {
        var (cpu, t, bus) = Build();
        // DD CB 05 00 = LD B,RLC (IX+d), z=0 (copy B). mem[EA]=0x80 -> RLC -> 0x01; B AND mem get 0x01.
        bus.Write8(0, 0xDD); bus.Write8(1, 0xCB); bus.Write8(2, 0x05); bus.Write8(3, 0x00);
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000); Set(cpu, t, "B", 0x55);
        bus.Write8(0x4005, 0x80);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, bus.Read8(0x4005));                 // memory gets the rotate result
        Assert.Equal(0x01u, Get(cpu, t, "B"));                 // B gets the rotate result (undoc store-copy)
        Assert.Equal(0x4005u, (uint)Get(cpu, t, "WZ"));
    }
}
