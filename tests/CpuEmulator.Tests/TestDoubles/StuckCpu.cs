using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>An ICpuCore double that never makes progress — exercises the run-loop guard.</summary>
internal sealed class StuckCpu : ICpuCore
{
    public string Architecture => "stuck";
    public long CycleCount => 0;
    public void Reset() { }
    public void Step() { }
    public void Run(ref long cycleBudget) { /* consumes nothing, advances nothing */ }
    public void SetIrqLine(bool asserted) { }
    public void SetNmiLine(bool asserted) { }
    public IReadOnlyList<string> RegisterNames => [];
    public ulong GetRegister(string name) => 0;
    public void SetRegister(string name, ulong value) { }
}
