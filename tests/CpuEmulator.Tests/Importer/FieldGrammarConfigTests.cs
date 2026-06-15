using System.IO;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class FieldGrammarConfigTests
{
    [Fact]
    public void Parses_the_m68000_state_model()
    {
        var cfg = FieldGrammarConfig.Parse("""
            {
              "architecture": "m68000",
              "namespace": "CpuEmulator.Cpus.M68000",
              "specClassName": "M68000Spec",
              "registers": [
                { "name": "D0", "bits": 32 },
                { "name": "SSP", "bits": 32, "role": "StackPointer" },
                { "name": "PC", "bits": 32, "role": "ProgramCounter" },
                { "name": "SR", "bits": 16, "role": "Status" }
              ],
              "flags": [ { "name": "C", "bit": 0 }, { "name": "S", "bit": 13 } ]
            }
            """);
        Assert.Equal("m68000", cfg.Architecture);
        Assert.Equal("CpuEmulator.Cpus.M68000", cfg.Namespace);
        Assert.Equal("M68000Spec", cfg.SpecClassName);
        Assert.Equal(4, cfg.Registers.Length);
        Assert.Equal("StackPointer", cfg.Registers[1].Role);
        Assert.Equal(2, cfg.Flags.Length);
        Assert.Equal(13, cfg.Flags[1].Bit);
    }

    [Fact]
    public void Rejects_an_empty_architecture()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarConfig.Parse("""
            { "architecture": "", "namespace": "N", "specClassName": "S",
              "registers": [ { "name": "D0", "bits": 32 } ], "flags": [] }
            """));
        Assert.Contains("architecture", ex.Message);
    }

    [Fact]
    public void Rejects_zero_registers()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarConfig.Parse("""
            { "architecture": "m68000", "namespace": "N", "specClassName": "S",
              "registers": [], "flags": [] }
            """));
        Assert.Contains("registers", ex.Message);
    }
}
