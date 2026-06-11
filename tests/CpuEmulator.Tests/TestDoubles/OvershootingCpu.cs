using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>An ICpuCore double executing fixed 7-cycle "instructions" — overshoots any
/// budget that is not a multiple of 7, like a real core (ICpuCore.Run may overshoot).</summary>
internal sealed class OvershootingCpu : ICpuCore
{
    public string Architecture => "overshoot";
    public long CycleCount { get; private set; }
    public void Reset() { }
    public void Step() => CycleCount += 7;
    public void Run(ref long cycleBudget)
    {
        while (cycleBudget > 0)
        {
            CycleCount += 7;
            cycleBudget -= 7;
        }
    }
    public void SetIrqLine(bool asserted) { }
    public void SetNmiLine(bool asserted) { }
    public IReadOnlyList<string> RegisterNames => [];
    public ulong GetRegister(string name) => 0;
    public void SetRegister(string name, ulong value) { }
}
