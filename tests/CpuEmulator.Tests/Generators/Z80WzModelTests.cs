using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80WzModelTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("wzf")]
        public static class WzfSpec
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
                Prefixes: [], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xC3, "JP",  AddrMode.ExtendedAddress, [JumpAbs()]),
                Insn(0xCD, "CALL", AddrMode.ExtendedAddress, [CallAbs()]),
                Insn(0xC7, "RST", AddrMode.Implied, [Rst()]),
                Insn(0x18, "JR",  AddrMode.RelativeJump, [RelJump()]),
            ];
        }

        public sealed partial class WzfCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public WzfCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.WzfCpu");
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
    public void JP_nn_sets_WZ_to_nn()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xC3); bus.Write8(1, 0x34); bus.Write8(2, 0x12);   // JP 0x1234
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0xFFFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "WZ"));   // WZ = nn
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "PC"));
    }

    [Fact]
    public void CALL_nn_sets_WZ_to_nn()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xCD); bus.Write8(1, 0x78); bus.Write8(2, 0x56);   // CALL 0x5678
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5678u, (uint)Get(cpu, t, "WZ"));   // WZ = nn
    }

    [Fact]
    public void RST_sets_WZ_to_vector()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xC7);                            // RST 00
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE); Set(cpu, t, "WZ", 0xABCD);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0000u, (uint)Get(cpu, t, "WZ"));   // WZ = n = 0
    }

    [Fact]
    public void JR_d_sets_WZ_to_dest()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0x18); bus.Write8(1, 0x05);       // JR +5 → dest = 0x07
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0xFFFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0007u, (uint)Get(cpu, t, "PC"));
        Assert.Equal(0x0007u, (uint)Get(cpu, t, "WZ"));   // WZ = dest
    }
}
