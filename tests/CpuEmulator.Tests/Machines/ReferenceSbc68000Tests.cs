using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Machines;

/// <summary>Piece #2 — the 68000 ReferenceSbc boot+run smoke. The board boots from its low ROM (the reset
/// SSP/PC vectors at $0/$4 + the program), runs a tiny program that writes "OK\r" out the memory-mapped
/// UART at $010000, and parks in a tight self-loop — on BOTH tiers (interpreter + JIT). Proves the recipe +
/// the 68000 reset produce a runnable computer. No TomHarte reset vector exists; the landed UART stream is
/// the gate. The 68000 board bus is BIG-ENDIAN (ReferenceSbc declares it), so the ROM image carries the
/// vectors + opcode words MSB-first.</summary>
public class ReferenceSbc68000Tests
{
    private const uint ProgramEntry = 0x0000_0008;   // the program starts just past the 8-byte vector area
    private const uint UartData = 0x0001_0000;        // the recipe's 68000 UART DATA address

    private static byte[] BuildRom()
    {
        var rom = new byte[0x1_0000];   // 64 KiB low ROM (recipe-required size)

        // Reset vectors (big-endian longs): SSP at $0, PC (program entry) at $4.
        WriteLongBE(rom, 0x0, 0x0002_0000);     // initial SSP -> top-of-RAM-ish (any mapped supervisor stack)
        WriteLongBE(rom, 0x4, ProgramEntry);    // initial PC -> the program

        // The program at $0008: write 'O','K','\r' to the UART, then self-loop.
        int p = (int)ProgramEntry;
        // For each byte: MOVEQ #imm,D0 ; MOVE.B D0,($00010000).L
        // MOVE.B D0,(abs).L opword = 0x13C0: size .b = bits13-12 (01); dest reg = bits11-9 = 001, dest mode =
        // bits8-6 = 111 (mode 7 reg 1 = absolute LONG); src = D0 (mode 0 reg 0). The abs-long address follows
        // as two big-endian extension words.
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0x70; rom[p++] = ch;                       // MOVEQ #ch,D0
            rom[p++] = 0x13; rom[p++] = 0xC0;                     // MOVE.B D0,(abs).L  opword
            rom[p++] = (byte)(UartData >> 24); rom[p++] = (byte)(UartData >> 16); // abs-long hi word
            rom[p++] = unchecked((byte)(UartData >> 8)); rom[p++] = unchecked((byte)UartData); // abs-long lo word
        }
        // STOP does not halt this core (its data-axis body only loads SR — the halt is an IPL/timing concern),
        // so park the CPU in a 1-instruction self-loop instead: BRA.s * = 0x60 0xFE (disp -2 from PC+2 → self).
        // The Run() cycle budget then drains harmlessly with the UART stream already complete.
        rom[p++] = 0x60; rom[p++] = 0xFE;                        // BRA.s *

        return rom;
    }

    private static void WriteLongBE(byte[] buf, int at, uint value)
    {
        buf[at + 0] = (byte)(value >> 24);
        buf[at + 1] = (byte)(value >> 16);
        buf[at + 2] = (byte)(value >> 8);
        buf[at + 3] = (byte)value;
    }

    private static string RunBoot(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);

        BoardSpec spec = ReferenceSbc.Build(CpuKind.M68000, uart, timer, BuildRom());
        Machine machine = BoardMachineFactory.Build(spec, tier);

        machine.Reset();            // 68000: SSP/PC from $0/$4, SR=0x2700
        machine.Run(2000);          // ample budget; the self-loop drains it once the stream is written
        return tx.ToString();
    }

    [Fact]
    public void M68000_board_boots_and_prints_OK_on_the_interpreter()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Interpreter));
    }

    [Fact]
    public void M68000_board_boots_and_prints_OK_on_the_jit()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Jit));
    }
}
