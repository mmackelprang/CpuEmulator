using Microsoft.CodeAnalysis;

namespace CpuEmulator.Generators;

internal static class SpecDiagnostics
{
    private const string Category = "CpuEmulator.Spec";

    private static DiagnosticDescriptor Make(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingRegisters = Make(
        "CPUGEN001", "Missing Registers table",
        "Spec class '{0}' must declare a field 'Registers' initialized with a collection expression of RegisterDef entries");

    public static readonly DiagnosticDescriptor InvalidRegister = Make(
        "CPUGEN002", "Invalid register definition",
        "Register entry '{0}' is not analyzable: {1}");

    public static readonly DiagnosticDescriptor MissingInstructions = Make(
        "CPUGEN003", "Missing Instructions table",
        "Spec class '{0}' must declare a field 'Instructions' initialized with a collection expression of Insn(...) entries");

    public static readonly DiagnosticDescriptor InvalidInstruction = Make(
        "CPUGEN004", "Invalid instruction definition",
        "Instruction entry '{0}' is not analyzable: {1}");

    public static readonly DiagnosticDescriptor DuplicateOpcode = Make(
        "CPUGEN005", "Duplicate opcode",
        "Opcode 0x{0} is defined more than once");

    public static readonly DiagnosticDescriptor UnknownMicroOp = Make(
        "CPUGEN006", "Unknown micro-op",
        "'{0}' is not a recognized micro-op factory (allowed: Load, Store, Transfer, Increment, SetNZ, Jump, BranchIf)");

    public static readonly DiagnosticDescriptor RoleViolation = Make(
        "CPUGEN007", "Register role violation",
        "{0}");

    public static readonly DiagnosticDescriptor UnknownRegisterInOp = Make(
        "CPUGEN008", "Unknown register in micro-op",
        "Micro-op references register '{0}' which is not declared in the Registers table");

    public static readonly DiagnosticDescriptor InvalidSpecMetadata = Make(
        "CPUGEN009", "Invalid spec metadata", "{0}");

    public static readonly DiagnosticDescriptor UnsupportedModeOpCombination = Make(
        "CPUGEN010", "Unsupported mode/op combination",
        "Instruction '{0}': {1}");

    public static readonly DiagnosticDescriptor InvalidMicroOpArgument = Make(
        "CPUGEN011", "Invalid micro-op argument",
        "Argument {0} of '{1}' must be a {2}");

    public static readonly DiagnosticDescriptor InvalidDecodeStructure = Make(
        "CPUGEN012", "Invalid decode structure",
        "Decode structure is not analyzable: {0}");

    public static readonly DiagnosticDescriptor InvalidFlagLayout = Make(
        "CPUGEN013", "Invalid flag layout",
        "Flag layout is not analyzable: {0}");

    public static readonly DiagnosticDescriptor InvalidPairView = Make(
        "CPUGEN014", "Invalid register pair view",
        "Register pair view '{0}': {1}");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "CPUGEN001" => MissingRegisters,
        "CPUGEN002" => InvalidRegister,
        "CPUGEN003" => MissingInstructions,
        "CPUGEN004" => InvalidInstruction,
        "CPUGEN005" => DuplicateOpcode,
        "CPUGEN006" => UnknownMicroOp,
        "CPUGEN007" => RoleViolation,
        "CPUGEN008" => UnknownRegisterInOp,
        "CPUGEN009" => InvalidSpecMetadata,
        "CPUGEN010" => UnsupportedModeOpCombination,
        "CPUGEN011" => InvalidMicroOpArgument,
        "CPUGEN012" => InvalidDecodeStructure,
        "CPUGEN013" => InvalidFlagLayout,
        "CPUGEN014" => InvalidPairView,
        _ => throw new System.ArgumentException($"Unknown diagnostic id '{id}'."),
    };
}
