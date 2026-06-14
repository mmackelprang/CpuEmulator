using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedIncDecTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixid")]
        public static class IxidSpec
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
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0x34, "INC", AddrMode.Indexed, [DdFdIncDecIndexed(false)]),
                Insn(0xDD, 0x35, "DEC", AddrMode.Indexed, [DdFdIncDecIndexed(true)]),
            ];
        }

        public sealed partial class IxidCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxidCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxidCpu");
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
    public void INC_IXplusd_rmw_sets_flags_and_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x34); bus.Write8(2, 0x03);   // INC (IX+3)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x7000); Set(cpu, t, "F", 0x01);  // C set (preserved)
        bus.Write8(0x7003, 0x7F);                                        // 0x7F -> 0x80 (overflow set)
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x80, bus.Read8(0x7003));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x80, f & 0x80);                                    // S = 1
        Assert.Equal(0x10, f & 0x10);                                    // H = 1 (0x0F -> carry out of low nibble)
        Assert.Equal(0x04, f & 0x04);                                    // P/V = overflow (0x7F->0x80)
        Assert.Equal(0x00, f & 0x02);                                    // N = 0 (INC)
        Assert.Equal(0x01, f & 0x01);                                    // C preserved
        Assert.Equal(0x7003u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void DEC_IXplusd_rmw_sets_N_and_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x35); bus.Write8(2, 0x01);   // DEC (IX+1)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x8000);
        bus.Write8(0x8001, 0x01);                                        // 0x01 -> 0x00 (zero set)
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, bus.Read8(0x8001));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                                    // Z = 1
        Assert.Equal(0x02, f & 0x02);                                    // N = 1 (DEC)
        Assert.Equal(0x8001u, (uint)Get(cpu, t, "WZ"));
    }
}
