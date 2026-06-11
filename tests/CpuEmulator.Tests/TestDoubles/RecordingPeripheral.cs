using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>Records every bus access and Realize call; returns a programmable read value.</summary>
internal sealed class RecordingPeripheral : IPeripheral
{
    public string Name { get; init; } = "recorder";
    public IMachineContext? RealizedWith { get; private set; }
    public int RealizeCount { get; private set; }
    public List<string>? RealizeLog { get; init; }
    public List<(uint Offset, AccessWidth Width)> Reads { get; } = [];
    public List<(uint Offset, AccessWidth Width, uint Value)> Writes { get; } = [];
    public uint NextReadValue { get; set; }

    public void Realize(IMachineContext context)
    {
        RealizedWith = context;
        RealizeCount++;
        RealizeLog?.Add(Name);
    }

    public uint Read(uint offset, AccessWidth width)
    {
        Reads.Add((offset, width));
        return NextReadValue;
    }

    public void Write(uint offset, AccessWidth width, uint value) =>
        Writes.Add((offset, width, value));
}
