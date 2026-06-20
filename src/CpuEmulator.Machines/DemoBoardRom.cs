using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Machines;

/// <summary>
/// The SP0 demo's 8 KiB 6502 ROM ($E000–$FFFF), assembled AT STARTUP by the generated
/// single-instruction assembler (the same pattern as the host's DemoRom). The program proves all
/// three SP0 device contracts:
/// <list type="number">
///   <item>paints a 256-byte gradient test pattern to VRAM $8000.. (display out);</item>
///   <item>polls the keyboard and echoes the typed byte to VRAM $8100 (input round-trip);</item>
///   <item>reads disk sector 0 and paints its first byte to VRAM $8101 (block device).</item>
/// </list>
/// Assembly happens in a SCRATCH space mapping the SAME byte[] writable at $E000, exactly as
/// DemoRom does. The device addresses MUST match <see cref="DemoBoard"/>: framebuffer $8000,
/// keyboard $D000 (DATA/STATUS), disk $D100 (LBA/CMD/DATA).
/// </summary>
public static class DemoBoardRom
{
    public const ushort Entry = 0xE000;

    // Device addresses — kept in lockstep with DemoBoard's peripheral slots.
    public const ushort FramebufferBase = 0x8000; // VRAM
    public const ushort PatternLength = 0x0100;    // 256-byte gradient strip
    public const ushort EchoCell = 0x8100;         // where a typed key lands
    public const ushort DiskCell = 0x8101;         // where the disk byte lands
    public const ushort KbdData = 0xD000;
    public const ushort KbdStatus = 0xD001;
    public const ushort DiskLba = 0xD100;
    public const ushort DiskCmd = 0xD101;
    public const ushort DiskData = 0xD102;

    public static byte[] Build()
    {
        var image = new byte[0x2000];
        var scratch = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        scratch.MapMemory(0xE000, image, writable: true);
        var cpu = new Mos6502Cpu(scratch);
        var assembler = new MonitorEngine(cpu, scratch, cpu);

        // Program listing (hand-laid; addresses are documented for the reader and to anchor branches).
        //
        //   ; ---- 1. paint the 256-byte gradient at $8000 (X = index = colour) ----
        //   E000  LDX #$00
        //   E002  TXA            ; A = X (the gradient byte == the index)
        //   E003  STA $8000,X    ; VRAM[X] = X
        //   E006  INX
        //   E007  BNE $E002      ; loop 256 times (until X wraps to 0)
        //
        //   ; ---- 3. read disk sector 0, paint first byte at $8101 (done once, up front) ----
        //   E009  LDA #$00
        //   E00B  STA $D100      ; disk LBA = 0
        //   E00E  LDA #$01
        //   E010  STA $D101      ; disk CMD = read
        //   E013  LDA $D102      ; disk DATA[0]
        //   E016  STA $8101      ; VRAM[$8101] = disk byte
        //
        //   ; ---- 2. keyboard poll/echo loop ----
        //   E019  LDA $D001      ; keyboard STATUS
        //   E01C  AND #$01       ; key-ready?
        //   E01E  BEQ $E019      ; no -> keep polling
        //   E020  LDA $D000      ; keyboard DATA (dequeue)
        //   E023  STA $8100      ; VRAM[$8100] = typed byte
        //   E026  JMP $E019      ; poll forever
        string[] program =
        [
            "LDX #$00",      // E000
            "TXA",           // E002
            "STA $8000,X",   // E003
            "INX",           // E006
            "BNE $E002",     // E007
            "LDA #$00",      // E009
            "STA $D100",     // E00B
            "LDA #$01",      // E00E
            "STA $D101",     // E010
            "LDA $D102",     // E013
            "STA $8101",     // E016
            "LDA $D001",     // E019
            "AND #$01",      // E01C
            "BEQ $E019",     // E01E
            "LDA $D000",     // E020
            "STA $8100",     // E023
            "JMP $E019",     // E026
        ];

        uint pc = Entry;
        foreach (string line in program)
        {
            if (!assembler.TryAssembleAt(pc, line, out byte[] bytes, out string? error))
                throw new EmulationException($"demo board ROM assembly failed at ${pc:X4} '{line}': {error}");
            pc += (uint)bytes.Length;
        }

        scratch.Write8(0xFFFA, 0x00); scratch.Write8(0xFFFB, 0xE0); // NMI    -> entry
        scratch.Write8(0xFFFC, 0x00); scratch.Write8(0xFFFD, 0xE0); // RESET  -> entry
        scratch.Write8(0xFFFE, 0x00); scratch.Write8(0xFFFF, 0xE0); // IRQ/BRK -> entry
        return image;
    }
}
