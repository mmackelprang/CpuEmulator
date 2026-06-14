using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbResSetTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("cbrs")]
        public static class CbrsSpec
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
                Insn(0xCB, 0x80, "RES", AddrMode.Bit, [CbBit("RES", 0, "B")]),
                Insn(0xCB, 0xC0, "SET", AddrMode.Bit, [CbBit("SET", 0, "B")]),
                Insn(0xCB, 0x86, "RES", AddrMode.Bit, [CbBit("RES", 0, "(HL)")]),
                Insn(0xCB, 0xC6, "SET", AddrMode.Bit, [CbBit("SET", 0, "(HL)")]),
            ];
        }

        public sealed partial class CbrsCpu
        {
            private readonly IAddressSpace _bus;
            public CbrsCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.CbrsCpu");
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
    public void RES_0_B_clears_bit0_no_flag_change()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0x80);   // RES 0,B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0xFF); Set(cpu, t, "F", 0xC5);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xFE, (byte)Get(cpu, t, "B"));  // bit 0 cleared
        Assert.Equal(0xC5, (byte)Get(cpu, t, "F"));  // F unchanged
    }

    [Fact]
    public void SET_0_B_sets_bit0_no_flag_change()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0xC0);   // SET 0,B
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0x00); Set(cpu, t, "F", 0x3A);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, (byte)Get(cpu, t, "B"));
        Assert.Equal(0x3A, (byte)Get(cpu, t, "F"));
    }

    [Fact]
    public void SET_0_HL_writes_memory()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCB); bus.Write8(1, 0xC6);   // SET 0,(HL)
        bus.Write8(0x5000, 0x00);
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x5000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01, bus.Read8(0x5000));
    }
}
