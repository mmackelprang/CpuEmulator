using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbRotateTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("cbrot")]
        public static class CbrotSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
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
                Insn(0xCB, 0x00, "RLC", AddrMode.Bit, [CbRotate("RLC", "B")]),
                Insn(0xCB, 0x07, "RLC", AddrMode.Bit, [CbRotate("RLC", "A")]),
                Insn(0xCB, 0x06, "RLC", AddrMode.Bit, [CbRotate("RLC", "(HL)")]),
                Insn(0xCB, 0x38, "SRL", AddrMode.Bit, [CbRotate("SRL", "B")]),
            ];
        }

        public sealed partial class CbrotCpu
        {
            private readonly IAddressSpace _bus;
            public CbrotCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.CbrotCpu");
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
    public void RLC_B_rotates_and_computes_full_flags()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x00);   // RLC B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0x85); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        // 0x85 = 1000_0101 → RLC → 0000_1011 = 0x0B, C = old bit7 = 1.
        Assert.Equal(0x0B, (byte)Get(cpu, t, "B"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x01, f & 0x01);               // C
        Assert.Equal(0x00, f & 0x10);               // H = 0
        Assert.Equal(0x00, f & 0x02);               // N = 0
        // 0x0B has 3 set bits → odd parity → P/V = 0.
        Assert.Equal(0x00, f & 0x04);
        Assert.Equal(2, (long)Get(cpu, t, "PC"));   // 2-byte instruction
    }

    [Fact]
    public void RLC_HL_reads_ops_writes_memory()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x06);   // RLC (HL)
        bus.Write8(0x4000, 0x80);
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, bus.Read8(0x4000));      // 0x80 RLC = 0x01
        Assert.Equal(0x01, (byte)Get(cpu, t, "F") & 0x01);  // C
    }

    [Fact]
    public void SRL_B_shifts_right_zero_into_bit7()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x38);   // SRL B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0x01); Set(cpu, t, "F", 0x00);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, (byte)Get(cpu, t, "B"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x01, f & 0x01);               // C = old bit0 = 1
        Assert.Equal(0x40, f & 0x40);               // Z = 1 (result 0)
    }
}
