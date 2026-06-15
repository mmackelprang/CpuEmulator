using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 — the additivity guard at the synthetic level (the 6502/Z80 RegeneratedSpecTests are the
/// real CPUs' guard). A spec declaring ONLY 8/16-bit registers must emit NO `uint` field — the width
/// relaxation is purely additive; the 8/16 arms are unchanged.</summary>
public class SyntheticWideRegisterByteIdentityTests
{
    private const string NarrowSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("narrowtest")]
        public static class NarrowTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("F", 8, RegisterRole.Status),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class NarrowTestCpu
        {
            private readonly IAddressSpace _bus;
            public NarrowTestCpu(IAddressSpace bus) { _bus = bus; }
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
    public void An_8_16_only_spec_emits_no_uint_field()
    {
        var result = GeneratorTestHost.Run(NarrowSpec);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public byte A;", result.GeneratedText);
        Assert.Contains("public ushort PC;", result.GeneratedText);
        Assert.DoesNotContain("public uint ", result.GeneratedText);
    }
}
