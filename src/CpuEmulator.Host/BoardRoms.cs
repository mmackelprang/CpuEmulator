namespace CpuEmulator.Host;

/// <summary>
/// Boot-ROM images for the host's reference boards. The 6502 image is the assembled
/// breadboard demo (hello-print + polled echo); the Z80/68000/8086 images are the tiny
/// "print OK\r then self-loop" boot programs proven to round-trip in the piece-#2 smokes
/// (tests/CpuEmulator.Tests/Machines/ReferenceSbc*Tests.cs) — copied here byte-for-byte so
/// the host boots the same provably-runnable programs.
/// </summary>
public static class BoardRoms
{
    /// <summary>The 6502 breadboard demo ROM ($E000-$FFFF, 8 KiB): hello-print then a polled
    /// echo loop, with all vectors -> $E000. Identical to the retired Breadboard6502's ROM.</summary>
    public static byte[] Mos6502Demo() => DemoRom.Build();

    /// <summary>The Z80 boot ROM image (8 KiB). Unused by the Z80 boot itself (the Z80 runs
    /// from RAM at $0000); the registry pokes <see cref="Z80BootProgram"/> into RAM. Present
    /// because the ReferenceSbc(Z80) recipe requires an 8 KiB ROM image.</summary>
    public static byte[] Z80Boot() => new byte[0x2000];

    /// <summary>The Z80 "print OK\r then HALT" program, poked into RAM at $0000 at boot.
    /// Copied verbatim from ReferenceSbcZ80Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] Z80BootProgram() =>
    [
        0x3E, 0x4F,             // LD A,'O'
        0x32, 0x00, 0xC0,       // LD ($C000),A   ; UART DATA at $C000
        0x3E, 0x4B,             // LD A,'K'
        0x32, 0x00, 0xC0,       // LD ($C000),A
        0x3E, 0x0D,             // LD A,CR
        0x32, 0x00, 0xC0,       // LD ($C000),A
        0x76,                   // HALT
    ];

    /// <summary>The 68000 boot ROM (64 KiB low ROM): reset vectors (SSP at $0, PC at $4) +
    /// a program at $0008 that writes "OK\r" out the UART at $010000, then self-loops. The
    /// bus is BIG-ENDIAN, so vectors + opcode words are MSB-first. Copied verbatim from
    /// ReferenceSbc68000Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] M68000Boot()
    {
        const uint programEntry = 0x0000_0008;
        const uint uartData = 0x0001_0000;
        var rom = new byte[0x1_0000];

        WriteLongBE(rom, 0x0, 0x0002_0000);    // initial SSP -> a mapped supervisor stack
        WriteLongBE(rom, 0x4, programEntry);   // initial PC -> the program

        int p = (int)programEntry;
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0x70; rom[p++] = ch;                                      // MOVEQ #ch,D0
            rom[p++] = 0x13; rom[p++] = 0xC0;                                    // MOVE.B D0,(abs).L
            rom[p++] = (byte)(uartData >> 24); rom[p++] = (byte)(uartData >> 16); // abs-long hi word
            rom[p++] = unchecked((byte)(uartData >> 8)); rom[p++] = unchecked((byte)uartData); // lo word
        }
        rom[p++] = 0x60; rom[p++] = 0xFE;      // BRA.s *  (1-instruction self-loop)
        return rom;
    }

    /// <summary>The 8086 boot ROM (64 KiB high ROM, $F0000-$FFFFF). The body at offset 0
    /// (physical 0xF0000) sets DS=0xA000 and writes "OK\r" out the UART at physical 0xA0000,
    /// then self-loops; the reset entry at offset 0xFFF0 (physical 0xFFFF0) FAR-JMPs to the
    /// body (the body is too big for the 16 bytes below the top of memory). Copied verbatim
    /// from ReferenceSbc8086Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] I8086Boot()
    {
        const uint romBase = 0xF_0000;
        const uint resetEntryPhysical = 0xF_FFF0;
        var rom = new byte[0x1_0000];

        int p = 0;
        rom[p++] = 0xB8; rom[p++] = 0x00; rom[p++] = 0xA0;   // MOV AX,0xA000
        rom[p++] = 0x8E; rom[p++] = 0xD8;                    // MOV DS,AX  (DS:0000 = physical 0xA0000)
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0xB0; rom[p++] = ch;                  // MOV AL,ch
            rom[p++] = 0xA2; rom[p++] = 0x00; rom[p++] = 0x00; // MOV [0x0000],AL
        }
        rom[p++] = 0xEB; rom[p++] = 0xFE;                    // JMP short *  (self-loop)

        int e = (int)(resetEntryPhysical - romBase);         // 0xFFF0
        rom[e++] = 0xEA; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0xF0; // JMP F000:0000
        return rom;
    }

    private static void WriteLongBE(byte[] buf, int at, uint value)
    {
        buf[at + 0] = (byte)(value >> 24);
        buf[at + 1] = (byte)(value >> 16);
        buf[at + 2] = (byte)(value >> 8);
        buf[at + 3] = (byte)value;
    }
}
