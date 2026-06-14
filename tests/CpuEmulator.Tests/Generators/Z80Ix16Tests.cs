using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.4e-2: the IX/IY 16-bit ops reuse the EXISTING base emit arms with the IX operand NAME the
/// derivation produces (Add16("IX",rp), Load16("IX"), Store16("IX"), LoadMem16("IX"), Inc16/Dec16("IX"),
/// Push16/Pop16("IX"), Transfer("IX","SP")) plus the G7 prefix-aware arms (EX (SP),IX ; JP (IX)). This
/// proves the base-arm reuse synthetically (the real regen lands at Task 7).</summary>
public class Z80Ix16Tests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ix16")]
        public static class Ix16Spec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0x09, "ADD", AddrMode.Register, [Add16("IX","BC")]),
                Insn(0xDD, 0x21, "LD",  AddrMode.ImmediateExtended, [Load16("IX")]),
                Insn(0xDD, 0x22, "LD",  AddrMode.ExtendedAddress, [Store16("IX")]),
                Insn(0xDD, 0x2A, "LD",  AddrMode.ExtendedAddress, [LoadMem16("IX")]),
                Insn(0xDD, 0x23, "INC", AddrMode.Register, [Inc16("IX")]),
                Insn(0xDD, 0x2B, "DEC", AddrMode.Register, [Dec16("IX")]),
                Insn(0xDD, 0xE5, "PUSH", AddrMode.Register, [Push16("IX")]),
                Insn(0xDD, 0xE1, "POP",  AddrMode.Register, [Pop16("IX")]),
                Insn(0xDD, 0xE3, "EX",  AddrMode.RegisterIndirect, [ExSpHl()]),
                Insn(0xDD, 0xE9, "JP",  AddrMode.RegisterIndirect, [JumpIndirect()]),
                Insn(0xDD, 0xF9, "LD",  AddrMode.Register, [Transfer("IX","SP")]),
            ];
        }

        public sealed partial class Ix16Cpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public Ix16Cpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.Ix16Cpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;
    private static void Run(object cpu, Type t, params byte[] prog)
    {
        // prog written at PC=0; caller sets registers first via the returned tuple.
    }

    [Fact]
    public void ADD_IX_BC_sets_IX_and_WZ_is_preIX_plus1()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x09);   // ADD IX,BC
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x1000); Set(cpu, t, "BC", 0x0234); Set(cpu, t, "WZ", 0xFFFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "IX"));
        Assert.Equal(0x1001u, (uint)Get(cpu, t, "WZ"));   // pre-op IX + 1
    }

    [Fact]
    public void LD_IX_nn_loads_immediate_no_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x21); bus.Write8(2, 0x83); bus.Write8(3, 0xBF);  // LD IX,0xBF83
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0xABCD);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xBF83u, (uint)Get(cpu, t, "IX"));
        Assert.Equal(0xABCDu, (uint)Get(cpu, t, "WZ"));   // unchanged
    }

    [Fact]
    public void LD_nn_IX_stores_and_WZ_is_nn_plus1()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x22); bus.Write8(2, 0x00); bus.Write8(3, 0x40);  // LD (0x4000),IX
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0xDDB0);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xB0, bus.Read8(0x4000));
        Assert.Equal(0xDD, bus.Read8(0x4001));
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // nn + 1
    }

    [Fact]
    public void LD_IX_nnmem_loads_and_WZ_is_nn_plus1()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x2A); bus.Write8(2, 0x00); bus.Write8(3, 0x06);  // LD IX,(0x0600)
        bus.Write8(0x0600, 0xEF); bus.Write8(0x0601, 0x01);
        Set(cpu, t, "PC", 0);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x01EFu, (uint)Get(cpu, t, "IX"));
        Assert.Equal(0x0601u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void INC_DEC_IX_no_flags()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x23);   // INC IX
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0xC8A3); Set(cpu, t, "F", 0x00); Set(cpu, t, "WZ", 0x0EC4);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xC8A4u, (uint)Get(cpu, t, "IX"));
        Assert.Equal(0x00, (byte)Get(cpu, t, "F"));        // INC rr writes no flags
        Assert.Equal(0x0EC4u, (uint)Get(cpu, t, "WZ"));    // unchanged
    }

    [Fact]
    public void PUSH_POP_IX_roundtrip()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xE5);   // PUSH IX
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0xABCD); Set(cpu, t, "SP", 0x8000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x7FFEu, (uint)Get(cpu, t, "SP"));
        Assert.Equal(0xCD, bus.Read8(0x7FFE));
        Assert.Equal(0xAB, bus.Read8(0x7FFF));
    }

    [Fact]
    public void EX_SP_IX_swaps_and_WZ_is_new_IX()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xE3);   // EX (SP),IX
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x7612); Set(cpu, t, "SP", 0x9000);
        bus.Write8(0x9000, 0xC0); bus.Write8(0x9001, 0x2B);   // (SP) word = 0x2BC0
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x2BC0u, (uint)Get(cpu, t, "IX"));        // IX <- (SP)
        Assert.Equal(0x12, bus.Read8(0x9000));                 // (SP) <- old IX lo
        Assert.Equal(0x76, bus.Read8(0x9001));                 // (SP+1) <- old IX hi
        Assert.Equal(0x2BC0u, (uint)Get(cpu, t, "WZ"));        // WZ = new IX
    }

    [Fact]
    public void JP_IX_sets_PC_no_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xE9);   // JP (IX)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x1860); Set(cpu, t, "WZ", 0x1171);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1860u, (uint)Get(cpu, t, "PC"));
        Assert.Equal(0x1171u, (uint)Get(cpu, t, "WZ"));        // unchanged
    }

    [Fact]
    public void LD_SP_IX_copies_no_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xF9);   // LD SP,IX
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0xFB28); Set(cpu, t, "WZ", 0xBF9A);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xFB28u, (uint)Get(cpu, t, "SP"));
        Assert.Equal(0xBF9Au, (uint)Get(cpu, t, "WZ"));        // unchanged
    }
}
