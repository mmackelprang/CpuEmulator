using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdLd16Tests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edld16")]
        public static class EdLd16Spec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0x43, "LD", AddrMode.ExtendedAddress, [EdLdNnRp("STORE", "BC")]),
                Insn(0xED, 0x4B, "LD", AddrMode.ExtendedAddress, [EdLdNnRp("LOAD", "BC")]),
            ];
        }

        public sealed partial class EdLd16Cpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public EdLd16Cpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdLd16Cpu");
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
    public void LD_nn_BC_stores_pair_little_endian_sets_WZ()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x43);     // LD (0x4000),BC
        mem.Write8(2, 0x00); mem.Write8(3, 0x40);
        Set(cpu, t, "PC", 0); Set(cpu, t, "BC", 0xBEEF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xEF, mem.Read8(0x4000));            // lo
        Assert.Equal(0xBE, mem.Read8(0x4001));            // hi
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // WZ = nn + 1
    }

    [Fact]
    public void LD_BC_nn_loads_pair_little_endian()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x4B);     // LD BC,(0x5010)
        mem.Write8(2, 0x10); mem.Write8(3, 0x50);
        mem.Write8(0x5010, 0x34); mem.Write8(0x5011, 0x12);
        Set(cpu, t, "PC", 0);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "BC"));
        Assert.Equal(0x5011u, (uint)Get(cpu, t, "WZ"));   // WZ = nn + 1
    }
}
