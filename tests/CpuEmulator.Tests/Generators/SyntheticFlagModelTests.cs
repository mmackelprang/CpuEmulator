using CpuEmulator.Core;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4a Task 2 — the composable flag micro-op family (Ground truth C), proven against a SYNTHETIC
/// CPU (mirrors <see cref="SyntheticRegisterSetTests"/>). A synthetic spec declares a Z80-like
/// <c>FlagLayout</c> (S=7 Z=6 Y=5 H=4 X=3 P=2 N=1 C=0) and an op using
/// <c>[SetSZ("A"), SetParity("A"), SetXY("A"), SetAddSub(false)]</c>; the generated CPU sets the
/// F register's flag bits per the per-spec layout. The proof is RUNTIME (the generated bits), not
/// generated text — the same load-bearing proof the synthetic decode CPU uses.
/// </summary>
public class SyntheticFlagModelTests
{
    // A minimal spec: A (Status companion F), a flag-setting op. Z80-like flag layout declared.
    private const string FlagTestSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticFlag;

        [CpuSpecification("flagtest")]
        public static class FlagTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("F", 8, RegisterRole.Status),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly FlagLayout Flags = new(
                [ new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                  new("X", 3), new("P", 2), new("N", 1), new("C", 0) ]);

            public static readonly InstructionDef[] Instructions =
            [
                // 0x00 sets S/Z from A, P from parity of A, X/Y from A bits 3/5, N=0.
                Insn(0x00, "SETF", AddrMode.Implied, [SetSZ("A"), SetParity("A"), SetXY("A"), SetAddSub(false)]),
            ];
        }

        public sealed partial class FlagTestCpu
        {
            private readonly IAddressSpace _bus;
            public FlagTestCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Synthetic_flag_spec_generates_a_compiling_class()
    {
        var result = GeneratorTestHost.Run(FlagTestSpec);
        Assert.Empty(result.AllErrors);
    }

    [Theory]
    // A, expected F. Z80 layout: S=0x80 Z=0x40 Y=0x20 H=0x10 X=0x08 P=0x04 N=0x02 C=0x01.
    [InlineData(0x00, 0x44)]   // A=0: Z(0x40) + P(0x04, parity of 0 is even). S/X/Y clear, N=0.
    [InlineData(0x80, 0x80)]   // A=0x80: S(0x80); 1 one-bit ⇒ odd parity ⇒ P clear. X/Y clear.
    [InlineData(0xFF, 0xAC)]   // A=0xFF: S(0x80)+Y(0x20)+X(0x08)+P(0x04)=0xAC; 8 ones ⇒ even parity.
    [InlineData(0x28, 0x2C)]   // A=0x28 (bits 3+5 set): Y(0x20)+X(0x08)+P(0x04, 2 ones even)=0x2C.
    public void Composable_flag_ops_set_F_per_layout(int a, int expectedF)
    {
        var cpuType = GeneratorTestHost.CompileAndLoadType(FlagTestSpec, "SyntheticFlag.FlagTestCpu");

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0000, 0x00);   // the SETF opcode at PC=0

        var cpu = (dynamic)System.Activator.CreateInstance(cpuType, (IAddressSpace)space)!;
        cpu.SetRegister("A", (ulong)a);
        cpu.SetRegister("F", (ulong)0);
        cpu.SetRegister("PC", (ulong)0);
        cpu.Step();

        Assert.Equal((ulong)expectedF, cpu.GetRegister("F"));
    }
}
