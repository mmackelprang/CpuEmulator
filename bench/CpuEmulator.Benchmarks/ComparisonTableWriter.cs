using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.Benchmarks;

/// <summary>The provenance of one comparison row. Serialized as the hyphenated strings the plan §5
/// JSON shape pins: <c>"head-to-head"</c> (measured here on the same workload bytes + host),
/// <c>"cited"</c> (published context from the registry, see footnotes), <c>"ours"</c> (one of our two
/// tiers).</summary>
[JsonConverter(typeof(ComparisonRowKindConverter))]
public enum ComparisonRowKind
{
    /// <summary>A third-party subject measured here, head-to-head (same workload bytes, same host).</summary>
    HeadToHead,

    /// <summary>A published-context figure from the cited registry (not measured here).</summary>
    Cited,

    /// <summary>One of our two tiers (Tier-0 interpreter / Tier-1 JIT).</summary>
    Ours,
}

/// <summary>Serializes <see cref="ComparisonRowKind"/> as the hyphenated strings the plan §5 JSON
/// shape requires (the exact strings matter for downstream consumers).</summary>
public sealed class ComparisonRowKindConverter : JsonConverter<ComparisonRowKind>
{
    public override ComparisonRowKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "head-to-head" => ComparisonRowKind.HeadToHead,
            "cited" => ComparisonRowKind.Cited,
            "ours" => ComparisonRowKind.Ours,
            var other => throw new JsonException($"unknown comparison row kind '{other}'"),
        };

    public override void Write(Utf8JsonWriter writer, ComparisonRowKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ComparisonRowKind.HeadToHead => "head-to-head",
            ComparisonRowKind.Cited => "cited",
            ComparisonRowKind.Ours => "ours",
            _ => throw new JsonException($"unknown comparison row kind '{value}'"),
        });
}

/// <summary>One row of the comparison table for a (CPU, workload): a subject, its provenance, and its
/// two throughput axes. <see cref="GuestMips"/> is null for a cycle-only subject (the 6502/Z80 refs +
/// our 6502/Z80 tiers — ranked by <see cref="CyclesPerSecond"/> within their CPU only). A cited row
/// carries the registry <see cref="Source"/>; head-to-head + ours leave it null.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Kind">Provenance — head-to-head / cited / ours.</param>
/// <param name="GuestMips">Cross-CPU-comparable guest-MIPS, or null when not reported.</param>
/// <param name="CyclesPerSecond">The CPU's own cycle unit (NOT cross-CPU comparable).</param>
/// <param name="AllFallback">true for a Tier-1 row whose JIT is all-fallback (z80/m68000) — the
/// committed "before" for the re-measure; false otherwise (incl. the 6502 JIT, which is real).</param>
/// <param name="Source">The citation URL for a cited row; null for head-to-head + ours.</param>
public readonly record struct ComparisonRow(
    string Subject,
    ComparisonRowKind Kind,
    double? GuestMips,
    double CyclesPerSecond,
    bool AllFallback,
    string? Source);

/// <summary>The comparison view of one workload within one CPU: every row (ours + head-to-head +
/// cited), the chosen best-existing subject, and the Tier-1-vs-best ratio (computed in the shared
/// unit — guest-MIPS when both sides have it, else cycles/sec; null when not computable).</summary>
public sealed record ComparisonWorkload(
    string Workload,
    IReadOnlyList<ComparisonRow> Rows,
    string? BestExisting,
    double? Tier1VsBest);

/// <summary>The comparison view of one CPU: its cycle unit, whether its timing axis is partial (the
/// 68000 only), and its workloads in first-seen order.</summary>
public sealed record ComparisonCpu(
    string Cpu,
    string CycleUnit,
    bool TimingAxisPartial,
    IReadOnlyList<ComparisonWorkload> Workloads);

/// <summary>The host the comparison was generated on (mirrors the ReportWriter Environment block).</summary>
public sealed record ComparisonHost(string Cpu, string Os, string Dotnet);

/// <summary>The full machine-readable comparison model (serialized to bench/results/comparison.json
/// and rendered to the REPORT.md comparison section).</summary>
public sealed record ComparisonModel(
    int SchemaVersion,
    string GeneratedUtc,
    ComparisonHost Host,
    IReadOnlyList<ComparisonCpu> Cpus);

/// <summary>Builds + renders the headline comparison — our emulator vs the best existing. It folds our
/// two tiers, the third-party head-to-head refs, and the cited published-numbers registry into one
/// <see cref="ComparisonModel"/>, then renders BOTH markdown (the REPORT.md section) and a
/// machine-readable comparison.json from that single model. guest-MIPS is the cross-CPU-comparable
/// headline; cycles/sec is the within-CPU fallback for cycle-only subjects (the 6502/Z80 refs report
/// cycles/sec but not instructions, so ranking + the ratio fall back to cycles/sec there).</summary>
public static class ComparisonTableWriter
{
    // ── Build ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Fold the measured rows + the cited registry into the comparison model. Groups by CPU
    /// (the ReportWriter order: 6502, Z80, 68000, then others), then by workload (first-seen order).
    /// Picks the best-existing non-ours row per workload and computes the Tier-1-vs-best ratio in the
    /// shared unit. Only rows whose <c>Result.Ran</c> contribute (a cited row is always included as the
    /// best-existing fallback when no head-to-head ref ran for that CPU).</summary>
    public static ComparisonModel Build(
        IReadOnlyList<BenchHarness.Row> tierRows,
        IReadOnlyList<BenchHarness.Row> adapterRows,
        IReadOnlyList<ReferenceNumber> cited)
    {
        var allRows = tierRows.Concat(adapterRows).ToList();

        var cpus = new List<ComparisonCpu>();
        foreach (string arch in ArchitectureOrder(allRows))
        {
            // The 68000 timing axis is PARTIAL (M4.5d-2 gating) and the 8086 timing axis is RUDIMENTARY
            // (M5 charges one cycle per bus access; a cycle-exact 8086 model is post-M5) — both lead with
            // instructions/sec, so both flag the partial-cycle-axis caveat.
            bool timingPartial = string.Equals(arch, "m68000", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(arch, "m8086", StringComparison.OrdinalIgnoreCase);

            // Does ANY head-to-head third-party subject run for this CPU at all? Cited rows are the
            // best-existing fallback ONLY when no head-to-head ref ran for the CPU (plan §M3).
            bool cpuHasHeadToHead = adapterRows.Any(r =>
                string.Equals(r.Architecture, arch, StringComparison.OrdinalIgnoreCase) && r.Result.Ran);

            var citedForCpu = cited
                .Where(c => string.Equals(c.Cpu, arch, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Workload order: first-seen across our tiers then adapters (preserves the runner order).
            var workloadOrder = new List<string>();
            foreach (var r in allRows.Where(r => string.Equals(r.Architecture, arch, StringComparison.OrdinalIgnoreCase)))
                if (!workloadOrder.Contains(r.Workload)) workloadOrder.Add(r.Workload);

            var workloads = new List<ComparisonWorkload>();
            foreach (string wl in workloadOrder)
            {
                var rows = new List<ComparisonRow>();

                // Our tiers (only the runs that Ran).
                foreach (var r in tierRows.Where(r =>
                             string.Equals(r.Architecture, arch, StringComparison.OrdinalIgnoreCase) &&
                             r.Workload == wl && r.Result.Ran))
                {
                    bool isTier1 = IsTier1(r.Subject);
                    // AllFallback = (arch is z80, m68000, or m8086) AND the row is the Tier-1/JIT row. The
                    // 6502 JIT is real, so its Tier-1 row is NOT all-fallback. The 8086 Tier-1 is all-fallback
                    // in M5.6 (every op routes through inner.Step — the populated-but-forced-fallback
                    // descriptor table; the committed "before" the M6 PR-B/C/D emit subtracts from).
                    bool allFallback = isTier1 &&
                        (string.Equals(arch, "z80", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arch, "m68000", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arch, "m8086", StringComparison.OrdinalIgnoreCase));
                    var n = NormalizedThroughput.From(r.Result);
                    rows.Add(new ComparisonRow(r.Subject, ComparisonRowKind.Ours, n.GuestMips, n.CyclesPerSecond, allFallback, Source: null));
                }

                // Third-party head-to-head refs (only the runs that Ran).
                foreach (var r in adapterRows.Where(r =>
                             string.Equals(r.Architecture, arch, StringComparison.OrdinalIgnoreCase) &&
                             r.Workload == wl && r.Result.Ran))
                {
                    var n = NormalizedThroughput.From(r.Result);
                    rows.Add(new ComparisonRow(r.Subject, ComparisonRowKind.HeadToHead, n.GuestMips, n.CyclesPerSecond, AllFallback: false, Source: null));
                }

                // Cited rows — the best-existing fallback for this CPU when no head-to-head ref ran.
                // One cited row per registry entry is added to EACH workload of the CPU.
                if (!cpuHasHeadToHead)
                    foreach (var c in citedForCpu)
                        rows.Add(new ComparisonRow(c.Subject, ComparisonRowKind.Cited, c.GuestMips, c.CyclesPerSecond ?? 0.0, AllFallback: false, Source: c.Source));

                // Best-existing = the highest-throughput NON-OURS row (head-to-head preferred; else cited).
                var nonOurs = rows.Where(r => r.Kind != ComparisonRowKind.Ours).ToList();
                ComparisonRow? best = SelectBest(nonOurs);
                string? bestExisting = best?.Subject;

                // Tier-1-vs-best ratio in the shared unit.
                double? ratio = null;
                if (best is { } b)
                {
                    var tier1 = rows.FirstOrDefault(r => r.Kind == ComparisonRowKind.Ours && IsTier1(r.Subject));
                    if (tier1.Subject is not null)   // a Tier-1 row exists
                        ratio = ComputeRatio(tier1, b);
                }

                workloads.Add(new ComparisonWorkload(wl, rows, bestExisting, ratio));
            }

            cpus.Add(new ComparisonCpu(arch, CycleUnit(arch), timingPartial, workloads));
        }

        return new ComparisonModel(
            SchemaVersion: 1,
            GeneratedUtc: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Host: new ComparisonHost(CpuName(), RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription),
            Cpus: cpus);
    }

    /// <summary>Choose the best non-ours candidate: rank by guest-MIPS when the candidate reports it,
    /// else by cycles/sec. A cited row with null/zero numbers still wins (becomes the placeholder
    /// best-existing) when it is the ONLY candidate. Returns null when there is no non-ours row.</summary>
    private static ComparisonRow? SelectBest(IReadOnlyList<ComparisonRow> candidates)
    {
        if (candidates.Count == 0) return null;
        // Score: prefer guest-MIPS when present, else cycles/sec. A cited-with-null candidate scores 0,
        // so a real head-to-head ref always out-ranks it; when it is the only candidate it still wins.
        return candidates
            .OrderByDescending(r => r.GuestMips ?? r.CyclesPerSecond)
            .First();
    }

    /// <summary>The Tier-1-vs-best ratio in the shared unit: guest-MIPS when BOTH sides report it, else
    /// cycles/sec when both are positive, else null (not computable — e.g. a cited row with null
    /// numbers).</summary>
    private static double? ComputeRatio(ComparisonRow tier1, ComparisonRow best)
    {
        if (tier1.GuestMips is { } tm && best.GuestMips is { } bm && bm > 0)
            return tm / bm;
        if (tier1.CyclesPerSecond > 0 && best.CyclesPerSecond > 0)
            return tier1.CyclesPerSecond / best.CyclesPerSecond;
        return null;
    }

    private static bool IsTier1(string subject) =>
        subject.Contains("Tier-1", StringComparison.OrdinalIgnoreCase) ||
        subject.Contains("JIT", StringComparison.OrdinalIgnoreCase);

    // ── RenderMarkdown ──────────────────────────────────────────────────────────────────────────
    /// <summary>Render the <c>## Comparison</c> section: per CPU a sub-heading + a table
    /// (Workload | Best existing | our Tier-0 | our Tier-1 | Tier-1 vs best), a legend, cited footnotes,
    /// and the 68000 cycle-axis caveat.</summary>
    public static string RenderMarkdown(ComparisonModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Comparison — our emulator vs the best existing");
        sb.AppendLine();
        sb.AppendLine("> guest-MIPS (millions of guest instructions / host wall-second) is the");
        sb.AppendLine("> cross-CPU-comparable headline; cycles/sec is the CPU's own unit (NOT cross-CPU");
        sb.AppendLine("> comparable). A cycle-only subject (no instruction count) is ranked by cycles/sec");
        sb.AppendLine("> within its CPU only.");
        sb.AppendLine();

        foreach (var cpu in model.Cpus)
        {
            sb.AppendLine($"### {ArchLabel(cpu.Cpu)} — guest-MIPS (cross-CPU-comparable); {UnitLabel(cpu.Cpu)}/sec in its own model");
            sb.AppendLine();
            sb.AppendLine("| Workload | Best existing | our Tier-0 (interp) | our Tier-1 (JIT) | Tier-1 vs best |");
            sb.AppendLine("|---|---|---|---|---|");

            bool anyCited = false;
            foreach (var wl in cpu.Workloads)
            {
                var t0 = wl.Rows.FirstOrDefault(r => r.Kind == ComparisonRowKind.Ours && !IsTier1(r.Subject));
                var t1 = wl.Rows.FirstOrDefault(r => r.Kind == ComparisonRowKind.Ours && IsTier1(r.Subject));
                var bestRow = wl.BestExisting is null
                    ? (ComparisonRow?)null
                    : wl.Rows.FirstOrDefault(r => r.Kind != ComparisonRowKind.Ours && r.Subject == wl.BestExisting);
                if (bestRow is { Kind: ComparisonRowKind.Cited }) anyCited = true;

                string bestCell = bestRow is { } b ? BestCell(b) : "—";
                string t0Cell = t0.Subject is not null ? TierCell(t0) : "—";
                string t1Cell = t1.Subject is not null ? TierCell(t1) : "—";
                string ratioCell = wl.Tier1VsBest is { } r ? $"{r:F2}×" : "—";

                sb.AppendLine($"| {Escape(wl.Workload)} | {bestCell} | {t0Cell} | {t1Cell} | {ratioCell} |");
            }
            sb.AppendLine();

            // Legend (once per CPU sub-table). The † fragment is emitted ONLY when this CPU actually
            // has an all-fallback Tier-1 row (z80 / m68000) — the 6502 JIT is real, so its section
            // never shows a † and must not advertise one in the legend.
            bool anyAllFallback = cpu.Workloads.SelectMany(w => w.Rows).Any(r => r.AllFallback);
            string legend = "‡ = measured here, head-to-head (same workload bytes, same host). " +
                            "[cited] = published context (see footnotes).";
            if (anyAllFallback)
                legend += " † = Tier-1 is all-fallback (no hot-op IL emit yet); the committed \"before\" for the re-measure.";
            sb.AppendLine(legend);
            sb.AppendLine();

            // Cited footnotes (link each cited source present in this CPU's tables).
            if (anyCited)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var wl in cpu.Workloads)
                    foreach (var row in wl.Rows.Where(r => r.Kind == ComparisonRowKind.Cited && r.Source is not null))
                        if (seen.Add(row.Subject))
                            sb.AppendLine($"- _[cited] {Escape(row.Subject)} — {Escape(row.Source!)}_");
                sb.AppendLine();
            }

            // The 68000 cycle-axis caveat reminder. The "best-existing" clause is data-aware: once a
            // head-to-head reference (Musashi) actually runs, the cited placeholder is gone (the gate in
            // Build suppresses it), so the caveat states the head-to-head ref is present rather than
            // promising a number that "has not landed yet".
            if (cpu.TimingAxisPartial)
            {
                bool hasHeadToHead = cpu.Workloads
                    .SelectMany(w => w.Rows)
                    .Any(r => r.Kind == ComparisonRowKind.HeadToHead);
                sb.AppendLine("> _68000 cycles/sec is reported with the M4.5d-2-coverage caveat (the timing axis is");
                sb.AppendLine("> PARTIAL on `main`); the trustworthy cross-CPU headline is **guest-MIPS**.");
                sb.AppendLine(hasHeadToHead
                    ? "> The best-existing column is the **head-to-head Musashi** reference, measured here on the"
                    : "> The cited best-existing row is a published-context placeholder until the head-to-head Musashi");
                sb.AppendLine(hasHeadToHead
                    ? "> same workload bytes + host (plan Task M4a)._"
                    : "> number lands (plan Task M4)._");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>The "Best existing" cell: subject + its number + a marker (‡ head-to-head / [cited]).
    /// A cited row with null numbers renders just the subject + [cited] (no number).</summary>
    private static string BestCell(ComparisonRow r)
    {
        if (r.Kind == ComparisonRowKind.Cited)
        {
            string num = NumberText(r);
            return num.Length == 0 ? $"{Escape(r.Subject)} [cited]" : $"{Escape(r.Subject)} {num} [cited]";
        }
        // Head-to-head.
        string n = NumberText(r);
        return n.Length == 0 ? $"{Escape(r.Subject)} ‡" : $"{Escape(r.Subject)} {n} ‡";
    }

    /// <summary>A tier cell: guest-MIPS ("N1 MIPS") when reported, else cycles/sec ("N0"); a † suffix
    /// when the Tier-1 row is all-fallback.</summary>
    private static string TierCell(ComparisonRow r)
    {
        string body = r.GuestMips is { } m
            ? $"{m.ToString("N1", CultureInfo.InvariantCulture)} MIPS"
            : r.CyclesPerSecond.ToString("N0", CultureInfo.InvariantCulture);
        return r.AllFallback ? $"{body} †" : body;
    }

    /// <summary>The number text for a best-existing cell: guest-MIPS when reported, else cycles/sec when
    /// positive, else empty (a cited row with no number).</summary>
    private static string NumberText(ComparisonRow r)
    {
        if (r.GuestMips is { } m) return $"{m.ToString("N1", CultureInfo.InvariantCulture)} MIPS";
        if (r.CyclesPerSecond > 0) return r.CyclesPerSecond.ToString("N0", CultureInfo.InvariantCulture);
        return string.Empty;
    }

    // ── RenderJson ────────────────────────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serialize the model to the plan §5 JSON shape (camelCase keys; the
    /// <see cref="ComparisonRowKind"/> hyphenated-string mapping is on the enum's converter).</summary>
    public static string RenderJson(ComparisonModel model) => JsonSerializer.Serialize(model, JsonOptions);

    /// <summary>Write the comparison JSON to bench/results/comparison.json (mirrors
    /// <c>ReportWriter.WriteDefault</c> / <c>LocateBenchDir</c>); returns the path written.</summary>
    public static string WriteComparisonJsonDefault(string json)
    {
        string benchDir = LocateBenchDir();
        string resultsDir = Path.Combine(benchDir, "results");
        Directory.CreateDirectory(resultsDir);
        string path = Path.Combine(resultsDir, "comparison.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ── shared helpers (mirror ReportWriter) ─────────────────────────────────────────────────────
    /// <summary>The architectures present, in the ReportWriter display order (6502, Z80, 68000, then
    /// others alphabetically) so the section is deterministic.</summary>
    private static IEnumerable<string> ArchitectureOrder(IEnumerable<BenchHarness.Row> rows)
    {
        var present = rows.Select(r => r.Architecture).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int Rank(string a) => a.ToLowerInvariant() switch { "mos6502" => 0, "z80" => 1, "m68000" => 2, _ => 3 };
        return present.OrderBy(Rank).ThenBy(a => a, StringComparer.OrdinalIgnoreCase);
    }

    private static string ArchLabel(string arch) => arch.ToLowerInvariant() switch
    {
        "mos6502" => "6502",
        "z80" => "Z80",
        "m68000" => "68000",
        _ => arch,
    };

    /// <summary>The human cycle unit for a CPU (the JSON <c>cycleUnit</c> field + the markdown
    /// sub-heading): "machine cycles" / "T-states" / "68000 cycles" / else "cycles".</summary>
    private static string CycleUnit(string arch) => arch.ToLowerInvariant() switch
    {
        "mos6502" => "machine cycles",
        "z80" => "T-states",
        "m68000" => "68000 cycles",
        _ => "cycles",
    };

    /// <summary>The short unit label for the markdown sub-heading ("T-states" for Z80, "cycles" else) —
    /// mirrors ReportWriter.UnitLabel.</summary>
    private static string UnitLabel(string arch) => arch.ToLowerInvariant() switch
    {
        "z80" => "T-states",
        _ => "cycles",
    };

    private static string CpuName()
    {
        string? w = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return !string.IsNullOrEmpty(w) ? w : RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ").Trim();

    private static string LocateBenchDir()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "bench");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "bench");
    }
}
