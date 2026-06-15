namespace CpuEmulator.Tests.Generators;

/// <summary>Shared synthetic field-grammar spec for the M4.3b EA tests (Tasks 1-3). Factored from the
/// M4.3a <c>FgwSpec</c>/<c>FgwCpu</c> source (M68kFieldDecodeWalkTests) but EXTENDED to declare A0-A7 +
/// D0-D7 + SP/PC/SR so the emitted EA-compute can name the address registers (A0..A7). Because the spec
/// declares a <c>FieldGrammar</c>, the generator emits the field-decode walk (Task 1's surfaced extension
/// words) AND the EA-compute probe (Task 2's <c>ComputeEaProbe</c>) automatically — no per-task spec change.
///
/// A7 is a PLAIN 32-bit register here (synthetic — no SR-S-bit banking; the real A7-over-USP/SSP banking is
/// M68000Cpu's, exercised in M4.5). The single ADD field op (mask 0xF100 / match 0xD000, size 7-6 standard,
/// EA 5-0) mirrors the M4.3a grammar.</summary>
internal static class M68kEaTestSpecs
{
    private const string SharedSource = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("fgw")]
        public static class FgwSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32), new("D1", 32), new("D2", 32), new("D3", 32),
                new("D4", 32), new("D5", 32), new("D6", 32), new("D7", 32),
                new("A0", 32), new("A1", 32), new("A2", 32), new("A3", 32),
                new("A4", 32), new("A5", 32), new("A6", 32), new("A7", 32),
                new("SP", 32, RegisterRole.StackPointer), new("PC", 32, RegisterRole.ProgramCounter),
                new("SR", 16, RegisterRole.Status),
            ];
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4), new("S", 13)]);
            public static readonly FieldGrammar Decode68k = new(
                FetchUnit.Word,
                [ FieldOp(Mask: 0xF100, Match: 0xD000, Operation: "ADD",
                          SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
                          EaShift: 0, LegalEa: EaCategory.DataAddressing) ]);
            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class FgwCpu
        {
            private readonly IAddressSpace _bus;
            public FgwCpu(IAddressSpace bus) { _bus = bus; }
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

    /// <summary>The grammar CPU used by Task 1's extension-word value test.</summary>
    public const string AddGrammarCpu = SharedSource;

    /// <summary>The grammar CPU used by Task 2's EA-compute test (the emitted ComputeEaProbe appears
    /// automatically because the spec declares a FieldGrammar — same string as <see cref="AddGrammarCpu"/>).</summary>
    public const string EaProbeCpu = SharedSource;
}
