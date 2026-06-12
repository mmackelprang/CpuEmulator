using System.IO;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Orchestrates the import pipeline:
///   Load dataset → load semantics map → emit spec source.
///
/// The engine is a thin façade over <see cref="SpecFileEmitter"/>; tests call it
/// directly (via the static <see cref="Run"/> overload) without touching the file system.
/// The CLI calls the file-path overload and handles I/O.
/// </summary>
public static class SpecImportEngine
{
    /// <summary>
    /// Runs the engine on pre-loaded data objects.
    /// Returns (emitted source, report).
    /// </summary>
    public static (string Source, ImportReport Report) Run(
        OpcodeEntry[] dataset,
        SemanticsMap  map,
        string        datasetPath   = "mos6502-opcodes.json",
        string        semanticsPath = "mos6502-semantics.json")
    {
        return SpecFileEmitter.Emit(dataset, map, datasetPath, semanticsPath);
    }

    /// <summary>
    /// Runs the full pipeline from file paths. Loads, validates, emits, and writes.
    /// Throws <see cref="InvalidDataException"/> on dataset/semantics errors.
    /// </summary>
    public static ImportReport RunFromFiles(
        string datasetPath,
        string semanticsPath,
        string outputPath)
    {
        var dataset = OpcodeDataset.Load(datasetPath);
        var map     = SemanticsMap.Load(semanticsPath);

        var (source, report) = SpecFileEmitter.Emit(dataset, map, datasetPath, semanticsPath);

        File.WriteAllText(outputPath, source);
        return report;
    }
}
