using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CpuEmulator.Benchmarks;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Tasks M3 + M5: pins the comparison-table generator — the headline deliverable. It builds
/// an in-memory <see cref="ComparisonModel"/> from our tier rows + third-party head-to-head rows + the
/// cited registry, picks the "best existing" non-ours row, computes the Tier-1-vs-best ratio in the
/// shared unit (guest-MIPS when both sides have it, else cycles/sec), and renders BOTH markdown and a
/// machine-readable comparison.json. M5 pins the cycle-only fallback (6502/Z80 refs report cycles/sec
/// but not instructions, so ranking + ratio fall back to cycles/sec).</summary>
public class ComparisonTableWriterTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────────
    private static BenchHarness.Row Tier0(string wl, string arch, long cycles, long instructions, double wall) =>
        new("our Tier-0 interpreter", wl, AdapterResult.MeasuredWithInstructions(cycles, instructions, wall, "t0"), arch);

    private static BenchHarness.Row Tier1(string wl, string arch, long cycles, long instructions, double wall) =>
        new("our Tier-1 JIT (chaining on)", wl, AdapterResult.MeasuredWithInstructions(cycles, instructions, wall, "t1"), arch);

    private static BenchHarness.Row Tier0Cyc(string wl, string arch, long cycles, double wall) =>
        new("our Tier-0 interpreter", wl, AdapterResult.Measured(cycles, wall, "t0"), arch);

    private static BenchHarness.Row Tier1Cyc(string wl, string arch, long cycles, double wall) =>
        new("our Tier-1 JIT (chaining on)", wl, AdapterResult.Measured(cycles, wall, "t1"), arch);

    // ── M3: 68000 head-to-head + ratio + markers ─────────────────────────────────────────────────
    [Fact]
    public void Build_picks_the_head_to_head_ref_as_best_existing_and_computes_the_mips_ratio()
    {
        const string wl = "m68k-W1 mixed-kernel";
        // Tier-0: 20 MIPS; Tier-1 (all-fallback): 10 MIPS; head-to-head ref: 40 MIPS (the best non-ours).
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", cycles: 100_000_000, instructions: 20_000_000, wall: 1.0),
            Tier1(wl, "m68000", cycles:  60_000_000, instructions: 10_000_000, wall: 1.0),
        };
        var adapterRows = new List<BenchHarness.Row>
        {
            new("Musashi-live (C)", wl, AdapterResult.MeasuredWithInstructions(200_000_000, 40_000_000, 1.0, "ref"), "m68000"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited: []);

        var cpu = Assert.Single(model.Cpus);
        Assert.Equal("m68000", cpu.Cpu);
        Assert.True(cpu.TimingAxisPartial);
        var w = Assert.Single(cpu.Workloads);
        Assert.Equal("Musashi-live (C)", w.BestExisting);

        // The head-to-head ref is present + flagged HeadToHead.
        Assert.Contains(w.Rows, r => r.Subject == "Musashi-live (C)" && r.Kind == ComparisonRowKind.HeadToHead);
        // Tier-1 row is flagged all-fallback (z80/m68000 JIT).
        var t1 = Assert.Single(w.Rows, r => r.Subject.Contains("Tier-1") && r.Kind == ComparisonRowKind.Ours);
        Assert.True(t1.AllFallback);

        // Ratio = Tier-1 MIPS / best MIPS = 10 / 40 = 0.25.
        Assert.NotNull(w.Tier1VsBest);
        Assert.Equal(10.0 / 40.0, w.Tier1VsBest!.Value, 6);
    }

    [Fact]
    public void RenderMarkdown_shows_head_to_head_marker_all_fallback_dagger_and_names()
    {
        const string wl = "m68k-W1 mixed-kernel";
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", 100_000_000, 20_000_000, 1.0),
            Tier1(wl, "m68000", 60_000_000, 10_000_000, 1.0),
        };
        var adapterRows = new List<BenchHarness.Row>
        {
            new("Musashi-live (C)", wl, AdapterResult.MeasuredWithInstructions(200_000_000, 40_000_000, 1.0, "ref"), "m68000"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited: []);
        string md = ComparisonTableWriter.RenderMarkdown(model);

        Assert.Contains("‡", md);                       // head-to-head marker
        Assert.Contains("†", md);                       // all-fallback Tier-1 marker
        Assert.Contains(wl, md);                         // workload name
        Assert.Contains("Musashi-live (C)", md);         // best-existing subject
        Assert.Contains("Comparison", md);               // the section heading
    }

    // ── M3: cited-only fallback (no head-to-head ref ran) ─────────────────────────────────────────
    [Fact]
    public void Build_uses_a_cited_row_as_best_existing_when_no_head_to_head_ref_and_null_numbers_give_null_ratio()
    {
        const string wl = "m68k-W2 arithmetic-kernel";
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", 100_000_000, 20_000_000, 1.0),
            Tier1(wl, "m68000", 60_000_000, 10_000_000, 1.0),
        };
        var cited = new List<ReferenceNumber>
        {
            new("m68000", "Musashi (C)", GuestMips: null, CyclesPerSecond: null,
                Note: "ctx", Source: "https://example.test/musashi", MeasuredOn: "n/a", CitedDate: "2026-06-17"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows: [], cited: cited);

        var cpu = Assert.Single(model.Cpus);
        var w = Assert.Single(cpu.Workloads);
        Assert.Equal("Musashi (C)", w.BestExisting);
        Assert.Null(w.Tier1VsBest);   // cited numbers are null → no ratio computable

        Assert.Contains(w.Rows, r => r.Subject == "Musashi (C)" && r.Kind == ComparisonRowKind.Cited && r.Source is not null);

        string md = ComparisonTableWriter.RenderMarkdown(model);
        Assert.Contains("[cited]", md);
        Assert.Contains("—", md);   // the null ratio renders as an em-dash
    }

    // ── M5: 6502 cycle-only fallback ──────────────────────────────────────────────────────────────
    [Fact]
    public void Build_ranks_a_cycle_only_6502_head_to_head_ref_by_cycles_and_ratios_in_cycles()
    {
        const string wl = "W2 arithmetic-kernel";
        // Our 6502 tiers report cycles only (no instructions → GuestMips null).
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0Cyc(wl, "mos6502", cycles: 200_000_000, wall: 1.0),
            Tier1Cyc(wl, "mos6502", cycles: 100_000_000, wall: 1.0),
        };
        // fake6502 (C): cycles only, faster than our tiers → the best non-ours by cycles/sec.
        var adapterRows = new List<BenchHarness.Row>
        {
            new("fake6502 (C)", wl, AdapterResult.Measured(400_000_000, 1.0, "fake6502"), "mos6502"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited: []);

        var cpu = Assert.Single(model.Cpus);
        Assert.Equal("mos6502", cpu.Cpu);
        Assert.False(cpu.TimingAxisPartial);   // only m68000 is partial
        var w = Assert.Single(cpu.Workloads);
        Assert.Equal("fake6502 (C)", w.BestExisting);

        // Neither side has GuestMips → ratio computed in cycles/sec: Tier-1 100M / best 400M = 0.25.
        Assert.NotNull(w.Tier1VsBest);
        Assert.Equal(100_000_000.0 / 400_000_000.0, w.Tier1VsBest!.Value, 6);

        // The fake6502 row is head-to-head with null GuestMips.
        var ref6502 = Assert.Single(w.Rows, r => r.Subject == "fake6502 (C)");
        Assert.Equal(ComparisonRowKind.HeadToHead, ref6502.Kind);
        Assert.Null(ref6502.GuestMips);

        string md = ComparisonTableWriter.RenderMarkdown(model);
        Assert.Contains("‡", md);              // head-to-head marker
        Assert.Contains("fake6502 (C)", md);
    }

    // ── M5: the 6502 section's legend must NOT advertise the all-fallback † (the 6502 JIT is real) ──
    [Fact]
    public void RenderMarkdown_omits_the_all_fallback_dagger_legend_for_the_6502_section()
    {
        const string wl = "W2 arithmetic-kernel";
        // 6502 tiers (cycle-only) + a head-to-head 6502 ref — NONE of these is all-fallback.
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0Cyc(wl, "mos6502", 200_000_000, 1.0),
            Tier1Cyc(wl, "mos6502", 100_000_000, 1.0),
        };
        var adapterRows = new List<BenchHarness.Row>
        {
            new("fake6502 (C)", wl, AdapterResult.Measured(400_000_000, 1.0, "fake6502"), "mos6502"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited: []);
        string md = ComparisonTableWriter.RenderMarkdown(model);

        // The 6502 JIT is real (never all-fallback), so the section must not show a † cell NOR advertise
        // the † legend fragment — a reader must not see a legend item for a marker that never appears.
        Assert.DoesNotContain("†", md);
        Assert.DoesNotContain("all-fallback", md);
        // The other legend fragments are still present.
        Assert.Contains("‡ = measured here", md);
        Assert.Contains("[cited] = published context", md);
    }

    [Fact]
    public void RenderMarkdown_keeps_the_all_fallback_dagger_legend_for_the_68000_section()
    {
        const string wl = "m68k-W2 arithmetic-kernel";
        // The 68000 Tier-1 IS all-fallback → the † legend fragment must appear.
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", 100_000_000, 20_000_000, 1.0),
            Tier1(wl, "m68000", 60_000_000, 10_000_000, 1.0),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows: [], cited: []);
        string md = ComparisonTableWriter.RenderMarkdown(model);

        Assert.Contains("†", md);
        Assert.Contains("all-fallback", md);
    }

    // ── M3: JSON round-trip ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void RenderJson_emits_valid_schema_with_hyphenated_kinds_and_cited_source()
    {
        const string wl = "m68k-W2 arithmetic-kernel";
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", 100_000_000, 20_000_000, 1.0),
            Tier1(wl, "m68000", 60_000_000, 10_000_000, 1.0),
        };
        var cited = new List<ReferenceNumber>
        {
            new("m68000", "Musashi (C)", null, null, "ctx", "https://example.test/musashi", "n/a", "2026-06-17"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows: [], cited: cited);
        string json = ComparisonTableWriter.RenderJson(model);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("generatedUtc", out _));
        Assert.True(root.TryGetProperty("host", out var host));
        Assert.True(host.TryGetProperty("cpu", out _) && host.TryGetProperty("os", out _) && host.TryGetProperty("dotnet", out _));

        var cpus = root.GetProperty("cpus");
        var cpu0 = cpus[0];
        Assert.Equal("m68000", cpu0.GetProperty("cpu").GetString());
        Assert.Equal("68000 cycles", cpu0.GetProperty("cycleUnit").GetString());
        Assert.True(cpu0.GetProperty("timingAxisPartial").GetBoolean());

        var w0 = cpu0.GetProperty("workloads")[0];
        var rows = w0.GetProperty("rows");
        var kinds = rows.EnumerateArray().Select(r => r.GetProperty("kind").GetString()).ToList();
        Assert.Contains("ours", kinds);
        Assert.Contains("cited", kinds);
        // The cited row carries a non-null source (provenance survives serialization).
        var citedRow = rows.EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "cited");
        Assert.False(string.IsNullOrEmpty(citedRow.GetProperty("source").GetString()));
    }

    [Fact]
    public void RenderJson_serializes_head_to_head_kind_as_the_hyphenated_string()
    {
        const string wl = "m68k-W1 mixed-kernel";
        var tierRows = new List<BenchHarness.Row>
        {
            Tier0(wl, "m68000", 100_000_000, 20_000_000, 1.0),
            Tier1(wl, "m68000", 60_000_000, 10_000_000, 1.0),
        };
        var adapterRows = new List<BenchHarness.Row>
        {
            new("Musashi-live (C)", wl, AdapterResult.MeasuredWithInstructions(200_000_000, 40_000_000, 1.0, "ref"), "m68000"),
        };

        var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited: []);
        string json = ComparisonTableWriter.RenderJson(model);

        Assert.Contains("\"head-to-head\"", json);
    }
}
