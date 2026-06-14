using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedLdTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixld")]
        public static class IxldSpec
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
                Insn(0xDD, 0x7E, "LD", AddrMode.Indexed, [DdFdLdIndexed("LOAD", "A")]),
                Insn(0xDD, 0x70, "LD", AddrMode.Indexed, [DdFdLdIndexed("STORE", "B")]),
                Insn(0xDD, 0x36, "LD", AddrMode.Indexed, [DdFdStoreImmIndexed()]),
            ];
        }

        public sealed partial class IxldCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxldCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxldCpu");
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
    public void LD_A_IXplusd_reads_EA_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x7E); bus.Write8(2, 0x05);   // LD A,(IX+5)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x2000); Set(cpu, t, "WZ", 0xFFFF);
        bus.Write8(0x2005, 0x99);                                        // (IX+5) = 0x99
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x99, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x2005u, (uint)Get(cpu, t, "WZ"));                  // WZ = EA
        Assert.Equal(0x3u, (uint)Get(cpu, t, "PC"));                     // PC advanced 3 (prefix+op+d)
    }

    [Fact]
    public void LD_IXplusd_B_writes_EA_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x70); bus.Write8(2, 0xFE);   // LD (IX-2),B
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x3000); Set(cpu, t, "B", 0x77);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x77, bus.Read8(0x2FFE));                           // IX + (sbyte)0xFE = 0x3000-2
        Assert.Equal(0x2FFEu, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void LD_IXplusd_n_reads_disp_then_imm()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x36); bus.Write8(2, 0x01); bus.Write8(3, 0xAB); // LD (IX+1),0xAB
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xAB, bus.Read8(0x4001));
        Assert.Equal(0x4u, (uint)Get(cpu, t, "PC"));                     // PC advanced 4 (prefix+op+d+n)
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));
    }
}
