using System.IO;
using System.Linq;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class FieldGrammarDatasetTests
{
    private const string TwoFamilies = """
        [
          { "operation": "ADD", "mask": "0xF000", "match": "0xD000",
            "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Standard",
            "eaShift": 0, "legalEa": "DataAddressing",
            "source": "M68000PRM table 3-1" },
          { "operation": "MOVE", "mask": "0xC000", "match": "0x0000",
            "sizeShift": 12, "sizeWidth": 2, "sizeEncoding": "Move",
            "eaShift": 0, "legalEa": "All",
            "source": "M68000PRM 4-116" }
        ]
        """;

    [Fact]
    public void Parses_well_formed_families_in_order()
    {
        var families = FieldGrammarDataset.Parse(TwoFamilies);
        Assert.Equal(2, families.Length);
        Assert.Equal("ADD", families[0].Operation);
        Assert.Equal((ushort)0xF000, families[0].Mask);
        Assert.Equal((ushort)0xD000, families[0].Match);
        Assert.Equal("Move", families[1].SizeEncoding);
        Assert.Equal(12, families[1].SizeShift);
    }

    [Fact]
    public void Rejects_match_bits_outside_mask()
    {
        // match has bit 0 set but mask does not cover it ⇒ unreachable family.
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarDataset.Parse("""
            [ { "operation": "X", "mask": "0xF000", "match": "0xD001",
                "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Standard",
                "eaShift": 0, "legalEa": "DataAddressing" } ]
            """));
        Assert.Contains("match", ex.Message);
    }

    [Fact]
    public void Rejects_size_field_out_of_bounds()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarDataset.Parse("""
            [ { "operation": "X", "mask": "0xF000", "match": "0xD000",
                "sizeShift": 15, "sizeWidth": 4, "sizeEncoding": "Standard",
                "eaShift": 0, "legalEa": "DataAddressing" } ]
            """));
        Assert.Contains("size field", ex.Message);
    }

    [Fact]
    public void Rejects_an_unknown_size_encoding()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarDataset.Parse("""
            [ { "operation": "X", "mask": "0xF000", "match": "0xD000",
                "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Bogus",
                "eaShift": 0, "legalEa": "DataAddressing" } ]
            """));
        Assert.Contains("sizeEncoding", ex.Message);
    }

    [Fact]
    public void Rejects_an_unknown_legal_ea_category()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarDataset.Parse("""
            [ { "operation": "X", "mask": "0xF000", "match": "0xD000",
                "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Standard",
                "eaShift": 0, "legalEa": "Nonsense" } ]
            """));
        Assert.Contains("legalEa", ex.Message);
    }

    [Fact]
    public void Rejects_an_empty_dataset()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FieldGrammarDataset.Parse("[]"));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void The_committed_m68000_dataset_loads_clean()
    {
        string repoRoot = TestRepo.FindRepoRoot();
        var families = FieldGrammarDataset.Load(Path.Combine(repoRoot,
            "tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json"));
        Assert.NotEmpty(families);
        Assert.Contains(families, f => f.Operation == "MOVE");
        Assert.Contains(families, f => f.Operation == "ADD");
        // Every family's match fits within its mask (the validator enforces it; assert it held on the real data).
        Assert.All(families, f => Assert.Equal(0, f.Match & ~f.Mask));
    }
}
