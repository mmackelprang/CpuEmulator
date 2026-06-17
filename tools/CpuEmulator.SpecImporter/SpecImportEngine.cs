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
        string        semanticsPath = "mos6502-semantics.json",
        string        outputPath    = "src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs")
    {
        return SpecFileEmitter.Emit(dataset, map, datasetPath, semanticsPath, outputPath);
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

        var (source, report) = SpecFileEmitter.Emit(dataset, map, datasetPath, semanticsPath, outputPath);

        File.WriteAllText(outputPath, source);
        return report;
    }

    /// <summary>Run the FieldGrammar arm on pre-loaded objects (the disjoint 68000 pipeline).</summary>
    public static (string Source, FieldGrammarReport Report) RunFieldGrammar(
        FieldGrammarFamily[] families,
        FieldGrammarConfig   config,
        string datasetPath = "data/m68000-fieldgrammar.json",
        string outputPath  = "src/CpuEmulator.Cpus.M68000/M68000Spec.cs")
        => FieldGrammarEmitter.Emit(families, config, datasetPath, outputPath);

    /// <summary>Run the FieldGrammar arm from file paths; loads, validates, emits, writes.</summary>
    public static FieldGrammarReport RunFieldGrammarFromFiles(
        string datasetPath, string configPath, string outputPath)
    {
        var families = FieldGrammarDataset.Load(datasetPath);
        var config   = FieldGrammarConfig.Load(configPath);
        var (source, report) = FieldGrammarEmitter.Emit(families, config, datasetPath, outputPath);
        File.WriteAllText(outputPath, source);
        return report;
    }

    /// <summary>Run the x86 arm on pre-loaded objects (the disjoint 8086 pipeline — M5.4).</summary>
    public static (string Source, X86Report Report) RunX86(
        X86Dataset dataset,
        X86Config  config,
        string datasetPath = "tools/CpuEmulator.SpecImporter/data/m8086-opcodes.json",
        string configPath  = "tools/CpuEmulator.SpecImporter/data/m8086-x86-config.json",
        string outputPath  = "src/CpuEmulator.Cpus.M8086/M8086Spec.cs")
        => X86Emitter.Emit(dataset, config, datasetPath, configPath, outputPath);

    /// <summary>Run the x86 arm from file paths; loads, validates, emits, writes.</summary>
    public static X86Report RunX86FromFiles(
        string datasetPath, string configPath, string outputPath)
    {
        var dataset = X86Dataset.Load(datasetPath);
        var config  = X86Config.Load(configPath);
        var (source, report) = X86Emitter.Emit(dataset, config, datasetPath, configPath, outputPath);
        File.WriteAllText(outputPath, source);
        return report;
    }
}
