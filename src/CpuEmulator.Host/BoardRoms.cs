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
}
