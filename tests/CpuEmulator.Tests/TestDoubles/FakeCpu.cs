using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>An ICpuCore double that consumes its entire cycle budget on each Run call.</summary>
internal sealed class FakeCpu : ICpuCore
{
    public string Architecture => "fake";
    public long CycleCount { get; private set; }
    public int ResetCount { get; private set; }
    public bool IrqAsserted { get; private set; }
    public bool NmiAsserted { get; private set; }
    public List<long> RunBudgets { get; } = [];

    public void Reset() => ResetCount++;

    public void Step() => CycleCount += 1;

    public void Run(ref long cycleBudget)
    {
        RunBudgets.Add(cycleBudget);
        CycleCount += cycleBudget;
        cycleBudget = 0;
    }

    public void SetIrqLine(bool asserted) => IrqAsserted = asserted;
    public void SetNmiLine(bool asserted) => NmiAsserted = asserted;

    public IReadOnlyList<string> RegisterNames => ["PC"];
    public ulong GetRegister(string name) => 0;
    public void SetRegister(string name, ulong value) { }
}
