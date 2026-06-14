using System.Reflection;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80RotateAccTests
{
    // A self-contained spec + hand-written partial for a tiny "rota" CPU exposing RLCA/RRCA/RLA/RRA.
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("rota")]
        public static class RotaSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x07, "RLCA", AddrMode.Implied, [Rlca()]),
                Insn(0x0F, "RRCA", AddrMode.Implied, [Rrca()]),
                Insn(0x17, "RLA", AddrMode.Implied, [Rla()]),
                Insn(0x1F, "RRA", AddrMode.Implied, [Rra()]),
            ];
        }

        public sealed partial class RotaCpu
        {
            private readonly byte[] _mem;
            private long _x;
            public RotaCpu(byte[] mem) { _mem = mem; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            private byte ReadBus(uint a) { _x++; return _mem[a & 0xFFFF]; }
            private void WriteBus(uint a, byte v) { _x++; _mem[a & 0xFFFF] = v; }
            private void HandleUndefinedOpcode(byte op) { _x++; }
        }
        """;

    private static (object Cpu, Type T) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.RotaCpu");
        var cpu = System.Activator.CreateInstance(t, new object[] { new byte[0x10000] })!;
        return (cpu, t);
    }

    private static void Set(object cpu, Type t, string reg, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { reg, v });
    private static ulong Get(object cpu, Type t, string reg) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { reg })!;

    private static (byte A, byte F) Run(byte opcode, byte a, byte f)
    {
        var (cpu, t) = Build();
        var memField = t.GetField("_mem", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var mem = (byte[])memField.GetValue(cpu)!;
        mem[0] = opcode;
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", a); Set(cpu, t, "F", f);
        t.GetMethod("Step")!.Invoke(cpu, null);
        return ((byte)Get(cpu, t, "A"), (byte)Get(cpu, t, "F"));
    }

    [Fact]
    public void RLCA_rotates_left_circular_sets_C_from_bit7_preserves_SZP()
    {
        // A=0x80 (bit7 set), F seeded with S+Z+P set (0xC4) — those must survive RLCA.
        var (a, f) = Run(0x07, 0x80, 0xC4);
        Assert.Equal(0x01, a);                       // 0x80 rotated left circular = 0x01
        Assert.Equal(0x01, f & 0x01);                // C = old bit7 = 1
        Assert.Equal(0x00, f & 0x10);                // H = 0
        Assert.Equal(0x00, f & 0x02);                // N = 0
        Assert.Equal(0xC4 & 0xC4, f & 0xC4);         // S(0x80)+Z(0x40)+P(0x04) preserved
        Assert.Equal(a & 0x28, f & 0x28);            // X(0x08)/Y(0x20) from new A
    }

    [Fact]
    public void RRCA_rotates_right_circular_sets_C_from_bit0()
    {
        var (a, f) = Run(0x0F, 0x01, 0x00);
        Assert.Equal(0x80, a);                       // 0x01 rotated right circular = 0x80
        Assert.Equal(0x01, f & 0x01);                // C = old bit0 = 1
    }

    [Fact]
    public void RLA_rotates_left_through_carry()
    {
        // A=0x80, C=1 in F → result = (0x80<<1)|1 = 0x01, new C = old bit7 = 1.
        var (a, f) = Run(0x17, 0x80, 0x01);
        Assert.Equal(0x01, a);
        Assert.Equal(0x01, f & 0x01);
    }

    [Fact]
    public void RRA_rotates_right_through_carry()
    {
        // A=0x01, C=1 → result = (0x01>>1)|(1<<7) = 0x80, new C = old bit0 = 1.
        var (a, f) = Run(0x1F, 0x01, 0x01);
        Assert.Equal(0x80, a);
        Assert.Equal(0x01, f & 0x01);
    }
}
