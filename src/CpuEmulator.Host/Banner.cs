using System.Linq;
using CpuEmulator.Machines;

namespace CpuEmulator.Host;

/// <summary>One-line board banners for the REPL, derived from the BoardSpec so each board
/// describes itself (name · CPU · address width · the UART/timer MMIO bases · region map).</summary>
public static class Banner
{
    public static string For(BoardSpec spec)
    {
        string uart = SlotBase(spec, "uart");
        string timer = SlotBase(spec, "timer");
        string regions = string.Join(" · ", spec.Memory.Select(r =>
            $"{r.Kind} ${r.Start:X}-${r.Start + r.Length - 1:X}"));
        return $"CpuEmulator — {spec.Name}\n" +
               $"{spec.Cpu} · {spec.AddressBits}-bit · UART {uart} · timer {timer}\n" +
               $"{regions}";
    }

    private static string SlotBase(BoardSpec spec, string name)
    {
        foreach (var slot in spec.Peripherals)
            if (slot.Name == name)
                return $"${slot.Base:X}";
        return "(none)";
    }
}
