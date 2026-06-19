namespace CpuEmulator.Machines;

/// <summary>Thrown by BoardMachineFactory.Build when BoardSpecValidator returns any diagnostic.
/// Carries every finding so the board author sees all problems at once.</summary>
public sealed class BoardValidationException : Exception
{
    public IReadOnlyList<BoardDiagnostic> Diagnostics { get; }

    public BoardValidationException(string boardName, IReadOnlyList<BoardDiagnostic> diagnostics)
        : base($"Board '{boardName}' is invalid: "
             + string.Join("; ", diagnostics.Select(d => $"[{d.Code}] {d.Message}")))
        => Diagnostics = diagnostics;
}
