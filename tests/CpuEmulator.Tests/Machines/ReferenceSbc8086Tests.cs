using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Machines;

/// <summary>Piece #2 — the 8086 ReferenceSbc boot+run smoke. After reset CS:IP = FFFF:0000 → physical
/// 0xFFFF0, inside the high ROM ($F0000-$FFFFF). The reset entry sits only 16 bytes below the top of ROM,
/// too small for the 21-byte body, so this uses the real-PC idiom: a FAR jump at the reset entry that
/// reloads CS:IP to the body lower in ROM. The body sets DS=0xA000, writes "OK\r" out the memory-mapped
/// UART at physical 0xA0000, and parks in a self-loop — on BOTH tiers. Proves the recipe + the 8086 reset
/// boot a runnable computer. The landed UART stream is the gate (no TomHarte reset vector exists).</summary>
public class ReferenceSbc8086Tests
{
    private const uint RomBase = 0xF_0000;            // the recipe's 8086 ROM base
    private const uint ResetEntryPhysical = 0xF_FFF0; // (CS<<4)+IP = (0xFFFF<<4)+0

    private static byte[] BuildRom()
    {
        var rom = new byte[0x1_0000];   // 64 KiB high ROM (recipe-required size)

        // ── The body at image offset 0 = physical 0xF0000, reachable as CS=0xF000:IP=0x0000. ──────────────
        // A NEAR JMP from the reset entry CANNOT reach the body: with CS=0xFFFF (base 0xFFFF0) the 20-bit
        // address wraps, so only the top 16 ROM bytes (physical 0xFFFF0-0xFFFFF) are reachable by changing
        // IP alone — every offset >= 0x10 wraps to physical 0x00000+ (RAM). The 21-byte body cannot fit in
        // 16 bytes, so the reset entry uses a CS-reloading FAR jump (the real-PC reset-vector idiom).
        int p = 0;
        rom[p++] = 0xB8; rom[p++] = 0x00; rom[p++] = 0xA0;   // MOV AX,0xA000  (the UART segment)
        rom[p++] = 0x8E; rom[p++] = 0xD8;                    // MOV DS,AX      (DS:0000 = physical 0xA0000)
        // For each byte: MOV AL,imm8 ; MOV [0x0000],AL  (DS:0000 = physical 0xA0000 = UART DATA).
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0xB0; rom[p++] = ch;                  // MOV AL,ch
            rom[p++] = 0xA2; rom[p++] = 0x00; rom[p++] = 0x00; // MOV [0x0000],AL
        }
        // HLT does not halt this core (its data-axis body is a no-op; the real halt is M5.5d), so park the
        // CPU in a 1-instruction self-loop instead: JMP short * = 0xEB 0xFE (rel8 -2 from IP-past-jmp → self).
        rom[p++] = 0xEB; rom[p++] = 0xFE;                    // JMP short *

        // ── The reset entry at image offset 0xFFF0 = physical 0xFFFF0 (CS:IP = FFFF:0000 at reset). ────────
        // FAR JMP F000:0000 = EA off_lo off_hi seg_lo seg_hi = EA 00 00 00 F0 → CS=0xF000, IP=0x0000, which
        // is physical 0xF0000 = the body start. Verified by single-stepping: i0 lands CS:IP = F000:0000.
        int e = (int)(ResetEntryPhysical - RomBase);         // 0xFFF0
        rom[e++] = 0xEA; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0xF0; // JMP F000:0000

        return rom;
    }

    private static string RunBoot(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);

        BoardSpec spec = ReferenceSbc.Build(CpuKind.I8086, uart, timer, BuildRom());
        Machine machine = BoardMachineFactory.Build(spec, tier);

        machine.Reset();            // 8086: CS=FFFF, IP=0, DS=ES=SS=0, FLAGS=0
        machine.Run(2000);          // ample budget; the self-loop drains it once the stream is written
        return tx.ToString();
    }

    [Fact]
    public void I8086_board_boots_and_prints_OK_on_the_interpreter()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Interpreter));
    }

    [Fact]
    public void I8086_board_boots_and_prints_OK_on_the_jit()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Jit));
    }
}
