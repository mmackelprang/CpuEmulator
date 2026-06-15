using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 (ADR 0003 Decision 1) — the 16-bit SR FlagLayout proof. The 68000's status register is
/// 16-bit: the CCR (X N Z V C) in bits 0–4, plus the supervisor (S) bit at 13. FlagBitDef.Bit must accept
/// 0–15 (the cap at SpecParser.cs:904 was 0–7). A synthetic spec placing the S flag above bit 7 must
/// compile clean. (The behavioral read/write proof — that the SR/CCR split round-trips — is the M68000
/// register-state test, Task 6.) Only Flag-enum-member names are used (C/V/Z/N/X + S); the interrupt
/// mask + T bit are raw SR bits, not named flags (Decision D5).</summary>
public class Sr16BitFlagLayoutTests
{
    private const string SrSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("srtest")]
        public static class SrTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32),
                new("SR", 16, RegisterRole.Status),
                new("PC", 32, RegisterRole.ProgramCounter),
            ];

            // CCR (low byte): C=0 V=1 Z=2 N=3 X=4. Supervisor (S) bit at 13 — the "above bit 7" case.
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4),
                new("S", 13)]);

            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class SrTestCpu
        {
            private readonly IAddressSpace _bus;
            public SrTestCpu(IAddressSpace bus) { _bus = bus; }
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
    public void Spec_with_flags_above_bit_7_generates_with_no_diagnostics()
    {
        var result = GeneratorTestHost.Run(SrSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
    }
}
