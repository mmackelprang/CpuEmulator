using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedAluTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixalu")]
        public static class IxaluSpec
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
                Insn(0xDD, 0x86, "ADD", AddrMode.Indexed, [DdFdAluIndexed("ADD")]),
                Insn(0xDD, 0xBE, "CP",  AddrMode.Indexed, [DdFdAluIndexed("CP")]),
            ];
        }

        public sealed partial class IxaluCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxaluCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxaluCpu");
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
    public void ADD_A_IXplusd_adds_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x86); bus.Write8(2, 0x02);   // ADD A,(IX+2)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x5000); Set(cpu, t, "A", 0x10);
        bus.Write8(0x5002, 0x22);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x32, (byte)Get(cpu, t, "A"));                      // 0x10 + 0x22
        Assert.Equal(0x00, (byte)Get(cpu, t, "F") & 0x02);              // N = 0 (add)
        Assert.Equal(0x5002u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void CP_A_IXplusd_compares_without_storing()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xBE); bus.Write8(2, 0x00);   // CP (IX+0)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x6000); Set(cpu, t, "A", 0x42);
        bus.Write8(0x6000, 0x42);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x42, (byte)Get(cpu, t, "A"));                      // A unchanged
        Assert.Equal(0x40, (byte)Get(cpu, t, "F") & 0x40);              // Z = 1 (equal)
        Assert.Equal(0x02, (byte)Get(cpu, t, "F") & 0x02);              // N = 1 (subtract)
        Assert.Equal(0x6000u, (uint)Get(cpu, t, "WZ"));
    }
}
