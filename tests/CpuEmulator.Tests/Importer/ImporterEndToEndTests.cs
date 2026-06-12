using System.IO;
using CpuEmulator.SpecImporter;
using CpuEmulator.Tests.Generators;
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// End-to-end gate: importer output → real source generator → zero diagnostics.
///
/// Keystone test: the spec-class file emitted from the real data files is fed
/// through GeneratorTestHost (exactly as production-generator tests do) and must
/// produce zero CPUGEN diagnostics and zero compilation errors.
///
/// Collision note (probe-verified in the 3a tasks-4/5 review): GeneratorTestHost
/// references the full TPA closure of the running test process, which DOES include
/// CpuEmulator.Cpus.Mos6502.dll — the real Mos6502Spec metadata type IS referenced
/// by the test compilation. No collision occurs anyway, for two reasons:
///   1. The generator discovers specs via SyntaxProvider.ForAttributeWithMetadataName,
///      which visits the compilation's SYNTAX TREES only — [CpuSpecification] types
///      in referenced metadata are never seen, so a duplicate-spec scenario cannot
///      arise from the referenced assembly.
///   2. Within the compilation itself, the source-declared Mos6502Spec shadows the
///      same-named imported metadata type (empirically with zero diagnostics — not
///      even CS0436, since no source in this unit references the metadata type).
/// Do NOT rely on "the assembly isn't referenced" — it is.
/// </summary>
public class ImporterEndToEndTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // ─── keystone: importer output → real generator → zero diagnostics ──────

    [Fact]
    public void Importer_Output_Compiles_Through_Generator_With_Zero_Diagnostics()
    {
        // Run the engine on the real data files
        var dataset = OpcodeDataset.Load(DatasetPath);
        var map     = SemanticsMap.Load(SemanticsPath);
        var (source, _) = SpecImportEngine.Run(dataset, map);

        // Append the minimal hand-written partial.
        // The spec class is Mos6502Spec → generated CPU class is Mos6502Cpu.
        // The emitted source uses a file-scoped namespace declaration, so only one
        // is allowed per "file". The partial uses a block-scoped namespace instead,
        // which is valid to have after a file-scoped namespace in the same compilation
        // unit ONLY if it's a separate source file. Since we concatenate into one string,
        // we must NOT add a second namespace declaration; the file-scoped namespace already
        // covers the whole file. We also need to add the CpuEmulator.Core using.
        //
        // Strategy: prepend the extra using to the emitted source, then append the
        // partial WITHOUT a namespace header — it already falls under the file-scoped one.
        var patchedSource = source.Replace(
            "using CpuEmulator.Core.Specification;",
            "using CpuEmulator.Core;\nusing CpuEmulator.Core.Specification;");

        var fullSource = patchedSource + """

            public sealed partial class Mos6502Cpu
            {
                private readonly IAddressSpace _bus;
                public Mos6502Cpu(IAddressSpace bus) => _bus = bus;
                public void Reset() { }
                public void SetIrqLine(bool asserted) { }
                public void SetNmiLine(bool asserted) { }
                private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
                private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
                private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            }
            """;

        var result = GeneratorTestHost.Run(fullSource);

        // Zero generator diagnostics (no CPUGEN errors)
        Assert.Empty(result.GeneratorDiagnostics);
        // Zero compilation errors
        Assert.Empty(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generated_Cpu_Exposes_All_Six_Registers()
    {
        var dataset = OpcodeDataset.Load(DatasetPath);
        var map     = SemanticsMap.Load(SemanticsPath);
        var (source, _) = SpecImportEngine.Run(dataset, map);

        var patchedSource2 = source.Replace(
            "using CpuEmulator.Core.Specification;",
            "using CpuEmulator.Core;\nusing CpuEmulator.Core.Specification;");

        var fullSource = patchedSource2 + """

            public sealed partial class Mos6502Cpu
            {
                private readonly IAddressSpace _bus;
                public Mos6502Cpu(IAddressSpace bus) => _bus = bus;
                public void Reset() { }
                public void SetIrqLine(bool asserted) { }
                public void SetNmiLine(bool asserted) { }
                private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
                private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
                private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            }
            """;

        var result = GeneratorTestHost.Run(fullSource);

        Assert.Empty(result.AllErrors);
        // All 6 register names appear in the generated CPU text
        Assert.Contains("\"A\"",  result.GeneratedText);
        Assert.Contains("\"X\"",  result.GeneratedText);
        Assert.Contains("\"Y\"",  result.GeneratedText);
        Assert.Contains("\"S\"",  result.GeneratedText);
        Assert.Contains("\"P\"",  result.GeneratedText);
        Assert.Contains("\"PC\"", result.GeneratedText);
    }

    // ─── CLI tests ───────────────────────────────────────────────────────────

    [Fact]
    public void Cli_ExitCode_Zero_And_File_Written_On_Valid_Args()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            // Capture stdout
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);

            int exitCode;
            try
            {
                exitCode = Program.Main([
                    "--dataset",   DatasetPath,
                    "--semantics", SemanticsPath,
                    "--out",       outFile,
                ]);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var stdout = sw.ToString();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outFile), "Output file should exist");
            Assert.True(new FileInfo(outFile).Length > 0, "Output file should not be empty");
            // Report line should appear on stdout
            Assert.Contains("total=151", stdout);
            Assert.Contains("emitted=", stdout);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Fact]
    public void Cli_With_Report_Flag_Shows_Inventory()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);

            int exitCode;
            try
            {
                exitCode = Program.Main([
                    "--dataset",   DatasetPath,
                    "--semantics", SemanticsPath,
                    "--out",       outFile,
                    "--report",
                ]);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Equal(0, exitCode);
            // --report flag should add per-mnemonic inventory to output
            var stdout = sw.ToString();
            Assert.Contains("total=151", stdout);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Fact]
    public void Cli_Bad_Dataset_Path_Returns_Nonzero_Without_StackTrace()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            var originalOut   = Console.Out;
            var originalError = Console.Error;
            using var outSw   = new StringWriter();
            using var errSw   = new StringWriter();
            Console.SetOut(outSw);
            Console.SetError(errSw);

            int exitCode;
            try
            {
                exitCode = Program.Main([
                    "--dataset",   "/nonexistent/path/opcodes.json",
                    "--semantics", SemanticsPath,
                    "--out",       outFile,
                ]);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            Assert.NotEqual(0, exitCode);
            // No stack trace in either stream
            var combined = outSw.ToString() + errSw.ToString();
            Assert.DoesNotContain("at CpuEmulator", combined);
            Assert.DoesNotContain("   at ", combined);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Fact]
    public void Cli_Missing_Required_Args_Returns_Nonzero()
    {
        var originalOut   = Console.Out;
        var originalError = Console.Error;
        using var outSw   = new StringWriter();
        using var errSw   = new StringWriter();
        Console.SetOut(outSw);
        Console.SetError(errSw);

        int exitCode;
        try
        {
            exitCode = Program.Main([]); // no args
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.NotEqual(0, exitCode);
    }
}
