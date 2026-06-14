using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.4e-2: the undocumented IXh/IXl 8-bit ops reuse the EXISTING base emit arms with the half
/// register NAME the derivation substitutes (Load("IXh"), Transfer("IXh","A"), IncReg/DecReg("IXh")), and
/// the half-ALU forms (ADD A,IXh = DD 84) read the H/L source slot as IXh/IXl via the prefix-aware
/// SourceRegFromOpcode. The inert prefix (DD 04 = INC B) executes the base op leaving IX/WZ untouched.</summary>
public class Z80IxHalfOpTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixhalf")]
        public static class IxhalfSpec
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
                Insn(0xDD, 0x26, "LD",  AddrMode.Immediate, [Load("IXh")]),
                Insn(0xDD, 0x2E, "LD",  AddrMode.Immediate, [Load("IXl")]),
                Insn(0xDD, 0x24, "INC", AddrMode.Register, [IncReg("IXh")]),
                Insn(0xDD, 0x2D, "DEC", AddrMode.Register, [DecReg("IXl")]),
                Insn(0xDD, 0x7C, "LD",  AddrMode.Register, [Transfer("IXh","A")]),
                Insn(0xDD, 0x84, "ADD", AddrMode.Register, [Add8()]),
                Insn(0xDD, 0x04, "INC", AddrMode.Register, [IncReg("B")]),
            ];
        }

        public sealed partial class IxhalfCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxhalfCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxhalfCpu");
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
    public void LD_IXh_n_sets_high_byte()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x26); bus.Write8(2, 0x99);   // LD IXh,0x99
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x1122);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x9922u, (uint)Get(cpu, t, "IX"));                  // hi <- 0x99, lo kept
    }

    [Fact]
    public void INC_IXh_increments_high_byte_full_flags()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x24);   // INC IXh
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x38C3);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x39C3u, (uint)Get(cpu, t, "IX"));                  // hi 0x38 -> 0x39
        Assert.Equal(0x00, (byte)Get(cpu, t, "F") & 0x02);             // N = 0 (INC)
    }

    [Fact]
    public void LD_A_IXh_copies_high_byte()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x7C);   // LD A,IXh
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x5497);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x54, (byte)Get(cpu, t, "A"));
    }

    [Fact]
    public void ADD_A_IXh_reads_high_byte_as_source()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x84);   // ADD A,IXh
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x0F); Set(cpu, t, "IX", 0xE9C3);  // IXh = 0xE9
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xF8, (byte)Get(cpu, t, "A"));                     // 0x0F + 0xE9 = 0xF8
    }

    [Fact]
    public void Inert_prefix_INC_B_leaves_IX_and_WZ_untouched()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x04);   // DD 04 = INC B (inert prefix)
        Set(cpu, t, "PC", 0); Set(cpu, t, "B", 0xB8); Set(cpu, t, "IX", 0x4F4D); Set(cpu, t, "WZ", 0x2D6F);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xB9, (byte)Get(cpu, t, "B"));                     // B + 1
        Assert.Equal(0x4F4Du, (uint)Get(cpu, t, "IX"));                 // IX untouched
        Assert.Equal(0x2D6Fu, (uint)Get(cpu, t, "WZ"));                 // WZ unchanged
        Assert.Equal(0x2u, (uint)Get(cpu, t, "PC"));                    // PC + 2
    }
}
