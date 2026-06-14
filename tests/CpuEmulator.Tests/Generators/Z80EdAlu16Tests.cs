using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdAlu16Tests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edalu16")]
        public static class EdAlu16Spec
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
                Insn(0xED, 0x42, "SBC", AddrMode.Register, [EdAdcSbc16("SBC", "BC")]),
                Insn(0xED, 0x4A, "ADC", AddrMode.Register, [EdAdcSbc16("ADC", "BC")]),
            ];
        }

        public sealed partial class EdAlu16Cpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public EdAlu16Cpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdAlu16Cpu");
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
    public void SBC_HL_BC_subtracts_with_carry_sets_N_and_WZ()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x42);     // SBC HL,BC
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x0005); Set(cpu, t, "BC", 0x0002);
        Set(cpu, t, "F", 0x00);                       // C(carry-in)=0
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0003u, (uint)Get(cpu, t, "HL"));   // 5 - 2 - 0 = 3
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x02, f & 0x02);                 // N = 1 (subtract)
        Assert.Equal(0x00, f & 0x40);                 // Z = 0
        Assert.Equal(0x00, f & 0x01);                 // C = 0 (no borrow)
        Assert.Equal(0x0006u, (uint)Get(cpu, t, "WZ"));   // WZ = preHL + 1 = 5 + 1
    }

    [Fact]
    public void ADC_HL_BC_adds_with_carry_clears_N()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x4A);     // ADC HL,BC
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x1000); Set(cpu, t, "BC", 0x0234);
        Set(cpu, t, "F", 0x01);                       // carry-in = 1
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1235u, (uint)Get(cpu, t, "HL"));   // 0x1000 + 0x234 + 1
        Assert.Equal(0x00, (byte)Get(cpu, t, "F") & 0x02);   // N = 0 (add)
        Assert.Equal(0x1001u, (uint)Get(cpu, t, "WZ"));   // WZ = preHL + 1
    }
}
