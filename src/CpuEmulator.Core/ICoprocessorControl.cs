namespace CpuEmulator.Core;

/// <summary>The active-CPU toggle seam (ADR 0015 Decisions 1 + 3). The dual-CPU Machine implements this;
/// the SoftCard control-port peripheral (PR-J), which sees the Machine through its Realize context
/// (Machine : IMachineContext), flips the active CPU by calling SetCoprocessorActive on the $CnXX write.
/// On a single-CPU Machine the seam is absent (the cast `context is ICoprocessorControl` simply fails),
/// so a control port wired onto a single-CPU board is inert — never an exception.</summary>
public interface ICoprocessorControl
{
    /// <summary>Set which CPU drives the shared bus on the NEXT run slice: true = the coprocessor runs
    /// (the primary is DMA-suspended), false = the primary runs. The dual-CPU run loop reads this flag
    /// at the slice boundary and ends the current slice so the switch takes effect cleanly (the writing
    /// instruction completes first — ADR 0015 OQ5).</summary>
    void SetCoprocessorActive(bool active);
}
