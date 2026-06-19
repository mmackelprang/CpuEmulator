using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

/// <summary>Board #2 (spec section 8 "Z80 reference-board smoke"): the Z80 ReferenceSbc boots from
/// its PC=0 reset, runs a tiny program that writes "OK\r" out the memory-mapped UART, and halts —
/// on BOTH tiers (interpreter + JIT). Proves the BoardSpec model generalizes across a genuinely
/// different CPU + reset mechanic from the same recipe.</summary>
public class ReferenceSbcZ80Tests
{
    private static string RunBoot(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);

        var rom = new byte[0x2000]; // unused by the Z80 boot (it runs from RAM at $0000)
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, uart, timer, rom);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        // Poke the boot program into RAM at $0000 (the Z80 reset entry).
        var space = machine.Space(AddressSpaceKind.Program);
        byte[] program =
        [
            0x3E, 0x4F,             // LD A,'O'
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x3E, 0x4B,             // LD A,'K'
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x3E, 0x0D,             // LD A,CR
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x76,                   // HALT
        ];
        for (int i = 0; i < program.Length; i++)
            space.Write8((uint)i, program[i]);

        machine.Reset();          // Z80: PC = 0
        machine.Run(1000);        // ample budget; the program halts well within it
        return tx.ToString();
    }

    [Fact]
    public void Z80_board_boots_and_prints_OK_on_the_interpreter()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Interpreter));
    }

    [Fact]
    public void Z80_board_boots_and_prints_OK_on_the_jit()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Jit));
    }
}
