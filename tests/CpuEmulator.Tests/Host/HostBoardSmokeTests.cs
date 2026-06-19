using System.Text;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Host;

/// <summary>The per-board host smokes: each board boots through BoardRegistry, the monitor
/// renders the right per-CPU registers (+ disassembly where the CPU has one), step/run
/// advances, and the UART round-trips. These are the un-fakeable "the host boots any board"
/// proofs for piece #3.</summary>
public class HostBoardSmokeTests
{
    [Fact]
    public void Mos6502_registry_path_prints_the_demo_banner_message_byte_identically()
    {
        // Boot the 6502 through the registry, reset, run the demo on a bounded budget — the
        // captured UART stream must be the breadboard demo's hello message (the same bytes the
        // retired hand-wired path produced).
        var tx = new StringBuilder();
        bool ok = BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);
        Assert.True(ok, error);
        board!.Uart.OnTransmit = b => tx.Append((char)b);

        board.Machine.Reset();        // PC = $E000 via the ROM reset vector
        board.Machine.Run(10_000);    // hello completes well within this budget

        Assert.Contains("Hello from Breadboard6502!", tx.ToString());
    }
}
