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
}
