namespace CpuEmulator.Machines;

/// <summary>One board-validation finding. A diagnostic, not an exception: validation collects all
/// findings so a board author sees every problem at once (BoardMachineFactory turns a non-empty
/// list into a BoardValidationException at instantiation time).</summary>
public sealed record BoardDiagnostic(string Code, string Message);
