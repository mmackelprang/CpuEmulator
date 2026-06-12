// CpuEmulator.SpecImporter — CLI entry point.
// Usage:
//   dotnet run --project tools/CpuEmulator.SpecImporter -- \
//     --dataset <path>  --semantics <path>  --out <path>  [--report]
//
// Exit codes: 0 = success, 1 = usage/IO/validation error.
// Stack traces are never printed; only a clean single-line error message.

using System.IO;
using CpuEmulator.SpecImporter;

// Make Program a real class so tests can call Main in-proc.
public static class Program
{
    public static int Main(string[] args)
    {
        string? datasetPath   = null;
        string? semanticsPath = null;
        string? outputPath    = null;
        bool    report        = false;

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
                case "--report":
                    report = true;
                    break;
                default:
                    return Fail($"Unknown argument: {args[i]}");
            }
        }

        if (datasetPath is null)   return Fail("Missing required argument: --dataset");
        if (semanticsPath is null) return Fail("Missing required argument: --semantics");
        if (outputPath is null)    return Fail("Missing required argument: --out");

        try
        {
            var importReport = SpecImportEngine.RunFromFiles(datasetPath, semanticsPath, outputPath);
            Console.WriteLine(importReport.ToString());

            if (report)
            {
                // Per-mnemonic inventory of missing semantics would go here in a
                // future iteration; for now the summary line is the report.
                Console.WriteLine($"  (todoSemantics={importReport.TodoSemantics} todoMode={importReport.TodoMode})");
            }

            return 0;
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
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}
