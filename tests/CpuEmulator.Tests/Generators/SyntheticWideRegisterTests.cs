using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 (ADR 0003 Decision 1) — the 32-bit register proof. A GENERATOR fixture (NOT a shipped
/// CPU) declaring a 32-bit register, compiled via GeneratorTestHost and DRIVEN at runtime: a full 32-bit
/// value round-trips through GetRegister/SetRegister. The 6502/Z80 declare only 8/16-bit registers, so
/// none of this perturbs them (byte-identical .g.cs — proven by SyntheticWideRegisterByteIdentityTests +
/// RegeneratedSpecTests).</summary>
public class SyntheticWideRegisterTests
{
    // A minimal synthetic CPU with a 32-bit data register D0, a 32-bit PC, and a 16-bit status. No
    // instructions (the register foundation is what is under test). The partial supplies the bus + hooks
    // the generator requires.
    private const string WideSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("widetest")]
        public static class WideTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32),
                new("SR", 16, RegisterRole.Status),
                new("PC", 32, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class WideTestCpu
        {
            private readonly IAddressSpace _bus;
            public WideTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Spec_with_a_32bit_register_generates_with_no_diagnostics()
    {
        var result = GeneratorTestHost.Run(WideSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
    }
}
