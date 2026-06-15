using System.Linq;
using CpuEmulator.SpecImporter;
using CpuEmulator.Tests.Generators;       // GeneratorTestHost
using Microsoft.CodeAnalysis;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class FieldGrammarEmitterTests
{
    private static FieldGrammarConfig Config() => FieldGrammarConfig.Parse("""
        { "architecture": "m68000", "namespace": "Demo", "specClassName": "DemoSpec",
          "registers": [
            { "name": "D0", "bits": 32 }, { "name": "A0", "bits": 32 },
            { "name": "SSP", "bits": 32, "role": "StackPointer" },
            { "name": "PC", "bits": 32, "role": "ProgramCounter" },
            { "name": "SR", "bits": 16, "role": "Status" } ],
          "flags": [ { "name": "C", "bit": 0 }, { "name": "V", "bit": 1 }, { "name": "Z", "bit": 2 },
                     { "name": "N", "bit": 3 }, { "name": "X", "bit": 4 }, { "name": "S", "bit": 13 } ] }
        """);

    private static FieldGrammarFamily[] Families() => FieldGrammarDataset.Parse("""
        [ { "operation": "ADD",  "mask": "0xF000", "match": "0xD000",
            "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Standard", "eaShift": 0, "legalEa": "DataAddressing" },
          { "operation": "MOVE", "mask": "0xC000", "match": "0x0000",
            "sizeShift": 12, "sizeWidth": 2, "sizeEncoding": "Move", "eaShift": 0, "legalEa": "All" } ]
        """);

    [Fact]
    public void Emits_a_FieldGrammar_declaration_with_FetchUnit_Word()
    {
        var (source, report) = FieldGrammarEmitter.Emit(Families(), Config(),
            datasetPath: "data/m68000-fieldgrammar.json", outputPath: "src/.../M68000Spec.cs");
        Assert.Contains("public static readonly FieldGrammar Decode68k = new(", source);
        Assert.Contains("FetchUnit.Word", source);
        Assert.Contains("FieldOp(Mask: 0xF000, Match: 0xD000", source);  // ADD: mask 0xF000, match 0xD000
        Assert.Contains("SizeEncoding.Move", source);
        Assert.Contains("EaCategory.DataAddressing", source);
        Assert.Contains("public static readonly InstructionDef[] Instructions = [];", source);
        Assert.Equal(2, report.Families);
    }

    [Fact]
    public void The_emitted_source_compiles_clean_through_the_generator()
    {
        var (source, _) = FieldGrammarEmitter.Emit(Families(), Config(),
            "data/m68000-fieldgrammar.json", "src/.../M68000Spec.cs");
        // The emitted spec is a partial source; pair it with a minimal CPU partial so the generator runs.
        string full = source + """

            namespace Demo;
            public sealed partial class DemoCpu
            {
                private readonly CpuEmulator.Core.IAddressSpace _bus;
                public DemoCpu(CpuEmulator.Core.IAddressSpace bus) { _bus = bus; }
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
        var result = GeneratorTestHost.Run(full);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
