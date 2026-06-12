using System.Text;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Generates a markdown extraction-review report from a loaded dataset,
/// optional diff result, and import report (for the missing-semantics inventory).
/// All I/O stays in Program.cs; this class is pure string generation.
/// </summary>
public static class ReviewReportGenerator
{
    /// <summary>
    /// Generates the review report markdown.
    /// </summary>
    /// <param name="architecture">Architecture name (e.g. "mos6502"), from SemanticsMap.</param>
    /// <param name="dataset">The loaded and validated opcode dataset.</param>
    /// <param name="report">Import report (for missing-semantics inventory).</param>
    /// <param name="diff">Optional diff result; when supplied and non-empty,
    ///     a Disagreements section is emitted.</param>
    public static string Generate(
        string       architecture,
        OpcodeEntry[] dataset,
        ImportReport report,
        DiffResult?  diff = null)
    {
        var sb = new StringBuilder();

        // ── title + timestamp ─────────────────────────────────────────────
        sb.AppendLine($"# Extraction Review: {architecture}");
        sb.AppendLine();
        // Date only (no time-of-day) — reproducible in tests; time varies.
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        // ── provenance coverage ───────────────────────────────────────────
        sb.AppendLine("## Provenance Coverage");
        sb.AppendLine();
        int cited = dataset.Count(e => e.Source is { Length: > 0 });
        sb.AppendLine($"{cited}/{dataset.Length} dataset rows carry `source` citations.");
        sb.AppendLine();

        // ── rows lacking source ───────────────────────────────────────────
        sb.AppendLine("## Rows Lacking Source");
        sb.AppendLine();

        var uncited = dataset.Where(e => !(e.Source is { Length: > 0 })).ToList();
        if (uncited.Count == 0)
        {
            sb.AppendLine("All rows carry source citations.");
        }
        else
        {
            sb.AppendLine("| Opcode | Mnemonic | Mode |");
            sb.AppendLine("|---|---|---|");
            foreach (var entry in uncited)
                sb.AppendLine($"| {entry.Opcode} | {entry.Mnemonic} | {entry.Mode} |");
        }
        sb.AppendLine();

        // ── disagreements (only when diff was given and has differences) ──
        if (diff is { Disagreements.Count: > 0 })
        {
            sb.AppendLine("## Disagreements");
            sb.AppendLine();
            sb.AppendLine("| Opcode | Field | Left | Right |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var d in diff.Disagreements)
                sb.AppendLine($"| {d.Opcode} | {d.Field} | {d.Left} | {d.Right} |");
            sb.AppendLine();
        }
        else if (diff is { HasDifferences: true })
        {
            // Missing/extra opcodes but no field-level disagreements.
            sb.AppendLine("## Disagreements");
            sb.AppendLine();
            if (diff.MissingInOther.Count > 0)
                sb.AppendLine($"Missing from other dataset: {string.Join(", ", diff.MissingInOther)}");
            if (diff.ExtraInOther.Count > 0)
                sb.AppendLine($"Extra in other dataset: {string.Join(", ", diff.ExtraInOther)}");
            sb.AppendLine();
        }

        // ── missing semantics ─────────────────────────────────────────────
        sb.AppendLine("## Missing Semantics");
        sb.AppendLine();
        if (report.MissingSemanticsInventory.Count == 0)
        {
            sb.AppendLine("All mnemonics have semantics defined.");
        }
        else
        {
            sb.AppendLine("| Mnemonic | Dataset Rows |");
            sb.AppendLine("|---|---|");
            foreach (var (mnemonic, rows) in report.MissingSemanticsInventory)
                sb.AppendLine($"| {mnemonic} | {rows} |");
        }
        sb.AppendLine();

        return sb.ToString();
    }
}
