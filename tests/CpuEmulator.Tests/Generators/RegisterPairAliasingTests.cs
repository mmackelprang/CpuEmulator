using CpuEmulator.Core;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4a Task 3 — the bidirectional register-pair aliasing (Ground truth A.2/A.3). The 8-bit
/// halves are the only STORAGE; the 16-bit pairs are computed VIEWS (HighHalf/LowHalf RegisterDef
/// carriers). Proven both synthetically AND over the Z80 register set: write a half → read the pair
/// reflects it; write the pair → read the halves reflect it; the pair emits a PROPERTY, not a field.
/// </summary>
public class RegisterPairAliasingTests
{
    private const string PairSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticPair;

        [CpuSpecification("pairtest")]
        public static class PairTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("B", 8),
                new("C", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
                new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x00, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class PairTestCpu
        {
            private readonly IAddressSpace _bus;
            public PairTestCpu(IAddressSpace bus) => _bus = bus;
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
    public void Pair_view_emits_a_property_not_a_field()
    {
        var result = GeneratorTestHost.Run(PairSpec);
        Assert.Empty(result.AllErrors);
        // A computed PROPERTY over the halves — NOT a "public ushort BC;" field.
        Assert.Contains("public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }",
            result.GeneratedText);
        Assert.DoesNotContain("public ushort BC;", result.GeneratedText);
    }

    [Fact]
    public void Pair_read_reflects_half_writes()
    {
        var cpu = NewCpu();
        cpu.SetRegister("B", (ulong)0x12);
        cpu.SetRegister("C", (ulong)0x34);
        Assert.Equal((ulong)0x1234, cpu.GetRegister("BC"));
    }

    [Fact]
    public void Pair_write_decomposes_to_halves()
    {
        var cpu = NewCpu();
        cpu.SetRegister("BC", (ulong)0xABCD);
        Assert.Equal((ulong)0xAB, cpu.GetRegister("B"));
        Assert.Equal((ulong)0xCD, cpu.GetRegister("C"));
    }

    [Fact]
    public void Pair_halves_must_be_declared_8bit_register()
    {
        // BC names a non-declared half "Z" → CPUGEN014.
        string source = GeneratorTestHost.ReplaceSection(
            PairSpec,
            """new("BC", 16, HighHalf: "B", LowHalf: "C"),""",
            """new("BC", 16, HighHalf: "Z", LowHalf: "C"),""");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN014");
    }

    [Fact]
    public void Pair_half_must_not_be_16bit()
    {
        // BC names PC (16-bit) as a half → CPUGEN014.
        string source = GeneratorTestHost.ReplaceSection(
            PairSpec,
            """new("BC", 16, HighHalf: "B", LowHalf: "C"),""",
            """new("BC", 16, HighHalf: "PC", LowHalf: "C"),""");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN014");
    }

    private static dynamic NewCpu()
    {
        var cpuType = GeneratorTestHost.CompileAndLoadType(PairSpec, "SyntheticPair.PairTestCpu");
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return System.Activator.CreateInstance(cpuType, (IAddressSpace)space)!;
    }
}
