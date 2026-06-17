using System.IO;
using System.Linq;
using CpuEmulator.Benchmarks;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task M2: pins the published-numbers registry parse + provenance enforcement. A cited row
/// MAY carry null numeric fields (we ship no fabricated number) but MUST carry a non-empty
/// <c>source</c> and a <c>cpu</c>/<c>subject</c> — a present-but-invalid registry throws
/// <see cref="InvalidDataException"/>. The committed registry loads + exposes the 68000/Musashi
/// cited placeholder so the comparison table's "best existing" column is reserved before any
/// head-to-head Musashi integration lands.</summary>
public class ReferenceNumbersTests
{
    [Fact]
    public void Parse_of_two_valid_rows_returns_both_with_cpu_subject_source()
    {
        const string json = """
        [
          { "cpu": "m68000", "subject": "Musashi (C)", "guestMips": null, "cyclesPerSecond": null,
            "note": "ctx", "source": "https://example.test/musashi", "measuredOn": "n/a", "citedDate": "2026-06-17" },
          { "cpu": "z80", "subject": "Foo (C)", "guestMips": 12.5, "cyclesPerSecond": 1000.0,
            "note": "ctx2", "source": "https://example.test/foo", "measuredOn": "host X", "citedDate": "2026-06-17" }
        ]
        """;

        var rows = ReferenceNumbers.Parse(json);

        Assert.Equal(2, rows.Count);
        var m = Assert.Single(rows, r => r.Subject == "Musashi (C)");
        Assert.Equal("m68000", m.Cpu);
        Assert.Equal("https://example.test/musashi", m.Source);
        Assert.Null(m.GuestMips);
        Assert.Null(m.CyclesPerSecond);

        var f = Assert.Single(rows, r => r.Subject == "Foo (C)");
        Assert.Equal("z80", f.Cpu);
        Assert.Equal(12.5, f.GuestMips);
        Assert.Equal(1000.0, f.CyclesPerSecond);
    }

    [Fact]
    public void Parse_of_a_row_missing_source_throws_naming_the_subject()
    {
        const string json = """
        [ { "cpu": "m68000", "subject": "NoSource (C)", "note": "ctx", "source": "" } ]
        """;

        var ex = Assert.Throws<InvalidDataException>(() => ReferenceNumbers.Parse(json));
        Assert.Contains("NoSource (C)", ex.Message);
    }

    [Fact]
    public void Parse_of_a_row_missing_cpu_throws()
    {
        const string json = """
        [ { "subject": "NoCpu (C)", "note": "ctx", "source": "https://example.test/x" } ]
        """;

        Assert.Throws<InvalidDataException>(() => ReferenceNumbers.Parse(json));
    }

    [Fact]
    public void Parse_of_literal_null_throws_present_but_invalid()
    {
        // A file whose content is literally `null` is present-but-invalid (not a JSON array) — it must
        // throw, NOT silently degrade to an empty registry (an intentionally-empty registry is `[]`).
        var ex = Assert.Throws<InvalidDataException>(() => ReferenceNumbers.Parse("null"));
        Assert.Contains("null", ex.Message);
    }

    [Fact]
    public void Parse_of_an_empty_array_returns_no_rows()
    {
        // The legitimately-empty registry: `[]` parses to zero rows without throwing.
        Assert.Empty(ReferenceNumbers.Parse("[]"));
    }

    [Fact]
    public void Load_finds_the_committed_m68000_Musashi_row_with_a_non_empty_source()
    {
        var rows = ReferenceNumbers.Load();

        var musashi = Assert.Single(rows, r =>
            r.Cpu == "m68000" && r.Subject == "Musashi (C)");
        Assert.False(string.IsNullOrWhiteSpace(musashi.Source));
    }
}
