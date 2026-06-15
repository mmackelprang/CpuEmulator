// CpuEmulator.SpecImporter — CLI entry point.
// Usage:
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --dataset <path>  --semantics <path>  --out <path>  [--report]
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --validate-only  --dataset <path>  --semantics <path>  [--report]
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --dataset <path>  --diff <other-dataset.json>
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --validate-only  --dataset <path>  --semantics <path> \
//     --diff <other>  --review-report <path.md>
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --field-grammar <dataset>  --config <config>  --out <spec>
//
// The --field-grammar arm is the disjoint 68000 field-pattern pipeline (M4.4a): it ingests a
// per-family FieldGrammar dataset + a state-model config and emits a spec whose populated
// Decode68k FieldGrammar makes the generator emit the word-granular decode walk. It NEVER touches
// the opcode-row arm above (the 6502/Z80 specs stay byte-identical).
//
// Exit codes:
//   0 = success
//   1 = usage error / IO error
//   2 = validation failure (dataset or semantics schema error)
//   3 = diff disagreements (cross-source field differences found)

using System.IO;
using CpuEmulator.SpecImporter;

// Make Program a real class so tests can call Main in-proc.
public static class Program
{
    public static int Main(string[] args)
    {
        string? datasetPath       = null;
        string? semanticsPath     = null;
        string? outputPath        = null;
        string? diffPath          = null;
        string? reviewReportPath  = null;
        bool    report            = false;
        bool    validateOnly      = false;
        string? fieldGrammarPath  = null;
        string? configPath        = null;

        // Plain loop argument parsing — no external packages required.
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dataset":
                    if (++i >= args.Length) return Fail("--dataset requires a value.");
                    datasetPath = args[i];
                    break;
                case "--semantics":
                    if (++i >= args.Length) return Fail("--semantics requires a value.");
                    semanticsPath = args[i];
                    break;
                case "--out":
                    if (++i >= args.Length) return Fail("--out requires a value.");
                    outputPath = args[i];
                    break;
                case "--diff":
                    if (++i >= args.Length) return Fail("--diff requires a value.");
                    diffPath = args[i];
                    break;
                case "--review-report":
                    if (++i >= args.Length) return Fail("--review-report requires a value.");
                    reviewReportPath = args[i];
                    break;
                case "--report":
                    report = true;
                    break;
                case "--validate-only":
                    validateOnly = true;
                    break;
                case "--field-grammar":
                    if (++i >= args.Length) return Fail("--field-grammar requires a value.");
                    fieldGrammarPath = args[i];
                    break;
                case "--config":
                    if (++i >= args.Length) return Fail("--config requires a value.");
                    configPath = args[i];
                    break;
                default:
                    return Fail($"Unknown argument: {args[i]}");
            }
        }

        // ── FieldGrammar arm (the 68000 field-pattern pipeline; disjoint from the opcode-row arm) ──
        if (fieldGrammarPath is not null)
        {
            if (configPath is null) return Fail("--field-grammar requires --config.");
            if (outputPath is null) return Fail("--field-grammar requires --out.");
            try
            {
                var fgReport = SpecImportEngine.RunFieldGrammarFromFiles(fieldGrammarPath, configPath, outputPath);
                Console.WriteLine(fgReport.ToString());
                return 0;
            }
            catch (FileNotFoundException ex) { return Fail($"File not found: {ex.FileName ?? ex.Message}"); }
            catch (DirectoryNotFoundException) { return Fail("File not found."); }
            catch (InvalidDataException ex) { return Fail($"Data error: {ex.Message}"); }
        }

        // --config is only meaningful in the FieldGrammar arm (handled above). If it reached here,
        // --field-grammar was absent, so the flag has no effect — reject it rather than silently ignore
        // (the arg loop's contract is that unrecognized flag combinations fail loudly).
        if (configPath is not null)
            return Fail("--config is only valid with --field-grammar.");

        // Mutual-exclusion check: --validate-only and --out are incompatible
        // (validate-only mode never writes a spec file by design).
        if (validateOnly && outputPath is not null)
            return Fail("--validate-only and --out are mutually exclusive.");

        // ── --validate-only mode ──────────────────────────────────────────
        if (validateOnly)
        {
            if (datasetPath is null)   return Fail("Missing required argument: --dataset");
            if (semanticsPath is null) return Fail("Missing required argument: --semantics");

            return RunValidateOnly(datasetPath, semanticsPath, diffPath, reviewReportPath, report);
        }

        // ── --diff-only mode (--dataset + --diff, no --out or --semantics required) ──
        if (diffPath is not null && outputPath is null && semanticsPath is null
            && reviewReportPath is null)
        {
            if (datasetPath is null) return Fail("Missing required argument: --dataset");
            return RunDiffOnly(datasetPath, diffPath);
        }

        // ── normal generation mode ───────────────────────────────────────
        if (datasetPath is null)   return Fail("Missing required argument: --dataset");
        if (semanticsPath is null) return Fail("Missing required argument: --semantics");

        // --diff is compatible with normal generation
        // --review-report is compatible with normal generation
        return RunGenerate(datasetPath, semanticsPath, outputPath, diffPath, reviewReportPath,
            report);
    }

    // ── mode implementations ─────────────────────────────────────────────

    private static int RunValidateOnly(
        string  datasetPath,
        string  semanticsPath,
        string? diffPath,
        string? reviewReportPath,
        bool    report)
    {
        OpcodeEntry[] dataset;
        SemanticsMap  map;

        // Load and validate dataset.
        try
        {
            dataset = OpcodeDataset.Load(datasetPath);
        }
        catch (FileNotFoundException ex)
        {
            return FailValidation($"File not found: {ex.FileName ?? ex.Message}");
        }
        catch (DirectoryNotFoundException)
        {
            return FailValidation($"File not found: {datasetPath}");
        }
        catch (InvalidDataException ex)
        {
            return FailValidation($"Data error: {ex.Message}");
        }

        // Load and validate semantics.
        try
        {
            map = SemanticsMap.Load(semanticsPath);
        }
        catch (FileNotFoundException ex)
        {
            return FailValidation($"File not found: {ex.FileName ?? ex.Message}");
        }
        catch (DirectoryNotFoundException)
        {
            return FailValidation($"File not found: {semanticsPath}");
        }
        catch (InvalidDataException ex)
        {
            return FailValidation($"Data error: {ex.Message}");
        }

        // Run the import engine (get the report; suppress file write).
        var (_, importReport) = SpecImportEngine.Run(dataset, map, datasetPath, semanticsPath);

        // Print standard report.
        Console.WriteLine(importReport.ToString());

        // Print provenance coverage.
        int citedCount = dataset.Count(e => e.Source is { Length: > 0 });
        Console.WriteLine($"provenance: {citedCount}/{dataset.Length} rows carry source citations");

        // Print missing-semantics inventory when --report is set.
        if (report)
        {
            Console.WriteLine("missing-semantics inventory (mnemonic: dataset rows):");
            foreach (var (mnemonic, rows) in importReport.MissingSemanticsInventory)
                Console.WriteLine($"  {mnemonic}: {rows}");
        }

        // ── optional --diff under --validate-only ─────────────────────────
        DiffResult? diffResult = null;
        if (diffPath is not null)
        {
            var diffExit = RunDiff(datasetPath, diffPath, dataset, out diffResult);
            if (diffExit != 0 && diffResult is null)
                return diffExit; // IO/validation error loading diff dataset
        }

        // ── optional --review-report ─────────────────────────────────────
        if (reviewReportPath is not null)
        {
            var reportContent = ReviewReportGenerator.Generate(
                map.Architecture, dataset, importReport, diffResult);
            try
            {
                File.WriteAllText(reviewReportPath, reportContent);
            }
            catch (Exception ex)
            {
                return Fail($"Cannot write review report: {ex.Message}");
            }
        }

        // Exit 3 when diff found disagreements (even if both datasets are individually valid).
        if (diffResult is { HasDifferences: true })
            return 3;

        return 0;
    }

    private static int RunDiffOnly(string datasetPath, string diffPath)
    {
        OpcodeEntry[] dataset;
        try
        {
            dataset = OpcodeDataset.Load(datasetPath);
        }
        catch (FileNotFoundException ex)
        {
            return FailValidation($"File not found: {ex.FileName ?? ex.Message}");
        }
        catch (DirectoryNotFoundException)
        {
            return FailValidation($"File not found: {datasetPath}");
        }
        catch (InvalidDataException ex)
        {
            return FailValidation($"Data error: {ex.Message}");
        }

        var exitCode = RunDiff(datasetPath, diffPath, dataset, out var diffResult);
        if (diffResult is null)
            return exitCode; // IO/validation error

        return diffResult.HasDifferences ? 3 : 0;
    }

    private static int RunGenerate(
        string  datasetPath,
        string  semanticsPath,
        string? outputPath,
        string? diffPath,
        string? reviewReportPath,
        bool    report)
    {
        OpcodeEntry[]? dataset    = null;
        SemanticsMap?  map        = null;
        ImportReport?  importReport = null;

        try
        {
            if (outputPath is not null)
            {
                importReport = SpecImportEngine.RunFromFiles(datasetPath, semanticsPath, outputPath);
                Console.WriteLine(importReport.ToString());
            }
            else
            {
                // No --out: just run the engine without writing (for --diff / --review-report).
                dataset = OpcodeDataset.Load(datasetPath);
                map     = SemanticsMap.Load(semanticsPath);
                var (_, rpt) = SpecImportEngine.Run(dataset, map, datasetPath, semanticsPath);
                importReport = rpt;
                Console.WriteLine(importReport.ToString());
            }

            if (report)
            {
                // Per-mnemonic inventory of dataset rows still awaiting semantics
                // (plan: the report includes a per-mnemonic missing-semantics inventory).
                Console.WriteLine("missing-semantics inventory (mnemonic: dataset rows):");
                foreach (var (mnemonic, rows) in importReport.MissingSemanticsInventory)
                    Console.WriteLine($"  {mnemonic}: {rows}");
            }
        }
        catch (FileNotFoundException ex)
        {
            return Fail($"File not found: {ex.FileName ?? ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return Fail($"Data error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"Unexpected error: {ex.Message}");
        }

        // ── optional --diff ──────────────────────────────────────────────
        DiffResult? diffResult = null;
        if (diffPath is not null)
        {
            // Need dataset for diff; may not be loaded yet (if --out was used).
            if (dataset is null)
            {
                try { dataset = OpcodeDataset.Load(datasetPath); }
                catch (FileNotFoundException ex)
                {
                    return FailValidation($"File not found: {ex.FileName ?? ex.Message}");
                }
                catch (InvalidDataException ex)
                {
                    return FailValidation($"Data error: {ex.Message}");
                }
            }
            var diffExit = RunDiff(datasetPath, diffPath, dataset, out diffResult);
            if (diffExit != 0 && diffResult is null)
                return diffExit;
        }

        // ── optional --review-report ─────────────────────────────────────
        if (reviewReportPath is not null && importReport is not null)
        {
            if (dataset is null)
            {
                try { dataset = OpcodeDataset.Load(datasetPath); }
                catch (Exception ex) { return Fail($"Unexpected error: {ex.Message}"); }
            }
            if (map is null)
            {
                try { map = SemanticsMap.Load(semanticsPath); }
                catch (Exception ex) { return Fail($"Unexpected error: {ex.Message}"); }
            }
            var reportContent = ReviewReportGenerator.Generate(
                map.Architecture, dataset, importReport, diffResult);
            try
            {
                File.WriteAllText(reviewReportPath, reportContent);
            }
            catch (Exception ex)
            {
                return Fail($"Cannot write review report: {ex.Message}");
            }
        }

        if (diffResult is { HasDifferences: true })
            return 3;

        return 0;
    }

    /// <summary>
    /// Loads the diff dataset and runs the comparison. On success, sets diffResult and
    /// prints the disagreement table. On IO/validation error, returns a non-zero exit code
    /// with diffResult = null.
    /// </summary>
    private static int RunDiff(
        string        primaryPath,
        string        diffPath,
        OpcodeEntry[] primary,
        out DiffResult? diffResult)
    {
        diffResult = null;
        OpcodeEntry[] other;
        try
        {
            other = OpcodeDataset.Load(diffPath);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"error: File not found: {ex.FileName ?? ex.Message}");
            return 2;
        }
        catch (DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"error: File not found: {diffPath}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"error: Data error: {ex.Message}");
            return 2;
        }

        var result = DatasetDiff.Compare(primary, other);
        diffResult = result;

        // Print the summary line.
        Console.WriteLine(
            $"diff: {result.Disagreements.Count} disagreement(s), " +
            $"{result.MissingInOther.Count} missing opcode(s), " +
            $"{result.ExtraInOther.Count} extra opcode(s)");

        if (result.Disagreements.Count > 0)
        {
            // Header row
            Console.WriteLine($"{"opcode",-8}  {"field",-16}  {"left",-20}  {"right",-20}");
            foreach (var d in result.Disagreements)
                Console.WriteLine($"{d.Opcode,-8}  {d.Field,-16}  {d.Left,-20}  {d.Right,-20}");
        }

        if (result.MissingInOther.Count > 0)
            Console.WriteLine($"missing from other: {string.Join(", ", result.MissingInOther)}");

        if (result.ExtraInOther.Count > 0)
            Console.WriteLine($"extra in other: {string.Join(", ", result.ExtraInOther)}");

        return result.HasDifferences ? 3 : 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static int FailValidation(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }
}
