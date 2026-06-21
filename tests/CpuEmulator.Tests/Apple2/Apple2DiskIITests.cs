using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2DiskIITests
{
    // A synthetic track holding a known run of valid GCR nibbles, framed with self-sync ($FF) so the
    // head can find a byte boundary the way a real read does.
    private static byte[] SampleNibbles() =>
    [
        0xFF, 0xFF, 0xFF,            // self-sync
        0x96, 0xD5, 0xAA, 0x96,     // some valid GCR bytes
        0xFF, 0xFF,
        0xAD, 0xDE, 0xAF,
    ];

    private static SyntheticFluxImage OneTrack(byte[] nibbles)
    {
        var img = new SyntheticFluxImage(trackCount: 35);
        img.SetTrackNibbles(0, nibbles);     // pack the bytes MSB-first into track 0's bitstream
        return img;
    }

    [Fact]
    public void Polling_C0EC_reads_the_track_nibbles_in_order()
    {
        byte[] nibbles = SampleNibbles();
        var disk = new Apple2DiskII(OneTrack(nibbles));
        disk.MotorOnForTest();                // motor must be on to read

        // Read enough latch-fetches to recover each nibble. A real read polls $C0EC until bit 7 sets;
        // here we pull a sequence and confirm every emitted byte is a valid GCR byte with bit 7 set, and
        // that the KNOWN non-sync bytes appear in order.
        var seen = new List<byte>();
        for (int i = 0; i < 200; i++)
        {
            byte b = disk.ReadDataLatch();    // == a $C0EC read
            if ((b & 0x80) != 0) seen.Add(b);
        }

        // The distinctive (non-$FF) bytes appear in their track order somewhere in the stream.
        AssertSubsequence(new byte[] { 0x96, 0xD5, 0xAA, 0x96, 0xAD, 0xDE, 0xAF }, seen);
    }

    [Fact]
    public void With_the_motor_off_the_latch_does_not_advance()
    {
        var disk = new Apple2DiskII(OneTrack(SampleNibbles()));
        // motor off (default): a read returns a non-ready latch (bit 7 clear) and does not advance.
        byte a = disk.ReadDataLatch();
        byte b = disk.ReadDataLatch();
        Assert.Equal(0, a & 0x80);
        Assert.Equal(0, b & 0x80);
    }

    private static void AssertSubsequence(byte[] needle, List<byte> haystack)
    {
        int n = 0;
        foreach (byte b in haystack)
            if (n < needle.Length && b == needle[n]) n++;
        Assert.True(n == needle.Length,
            $"expected nibbles [{string.Join(",", needle.Select(x => $"${x:X2}"))}] as a subsequence");
    }

    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;     // reset -> $D000 (unused here)
        return rom;
    }

    private static (CpuEmulator.Core.Machine machine, Apple2DiskII disk, CpuEmulator.Core.IAddressSpace bus)
        BuildBoardWithDisk()
    {
        var image = new SyntheticFluxImage(trackCount: 35);
        for (int t = 0; t < 35; t++) image.SetTrackNibbles(t, new byte[] { 0xFF, (byte)(0x96 + t % 0x10) });
        var disk = new Apple2DiskII(image);
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state, disk);                          // IOU holds the Disk II
        var spec = CpuEmulator.Machines.Apple2Board.SpecWithDiskII(SystemRom(), iou, disk);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);
        return (machine, disk, machine.Space(AddressSpaceKind.Program));
    }

    [Fact]
    public void Energizing_adjacent_stepper_phases_moves_the_head_a_half_track()
    {
        var (_, disk, bus) = BuildBoardWithDisk();
        Assert.Equal(0, disk.HalfTrackForTest);
        // From phase 0 (implicit), energize phase 1 then phase 2 to step inward (the 4-phase model).
        _ = bus.Read8(0xC0E3);   // phase 1 on
        _ = bus.Read8(0xC0E5);   // phase 2 on  -> head advances toward higher tracks
        Assert.True(disk.HalfTrackForTest > 0, "adjacent-phase energizing should advance the head");
    }

    [Fact]
    public void Motor_on_then_off_keeps_the_motor_running_for_the_one_second_delay()
    {
        var (machine, disk, bus) = BuildBoardWithDisk();
        _ = bus.Read8(0xC0E9);                    // motor ON
        Assert.True(disk.MotorOnForTestProperty);
        _ = bus.Read8(0xC0E8);                    // motor OFF requested -> ~1 s 556 delay
        Assert.True(disk.MotorOnForTestProperty, "the motor lingers during the 556 delay");

        machine.Run(500_000);                     // < ~1 s: still spinning
        Assert.True(disk.MotorOnForTestProperty);
        machine.Run(700_000);                     // now total > ~1.02 M cycles (~1 s): the motor stops
        Assert.False(disk.MotorOnForTestProperty, "after the ~1 s delay the motor is off");
    }

    [Fact]
    public void Re_requesting_motor_on_cancels_a_pending_off()
    {
        var (machine, disk, bus) = BuildBoardWithDisk();
        _ = bus.Read8(0xC0E9);                    // on
        _ = bus.Read8(0xC0E8);                    // off (pending ~1 s)
        _ = bus.Read8(0xC0E9);                    // on again -> cancels the pending off
        machine.Run(1_500_000);                   // well past 1 s
        Assert.True(disk.MotorOnForTestProperty, "motor-on cancelled the pending off-timer");
    }

    [Fact]
    public void TryPeek_of_C0EC_has_no_side_effect_and_does_not_advance_the_head()
    {
        // The ][+ peek-free invariant (ADR 0014 Decision 2): a debugger LOOKING at the Disk II data latch
        // ($C0EC) must NOT shift a nibble. A peek must never reach the controller's Access (no head
        // advance, no latch change). Mirrors the PR-E TryPeek_of_C08x_has_no_side_effect gate.
        var image = new SyntheticFluxImage(trackCount: 35);
        image.SetTrackNibbles(0, new byte[] { 0xFF, 0xD5, 0xAA, 0x96, 0xAD, 0xDA, 0x96, 0xFF });
        var disk = new Apple2DiskII(image);
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state, disk);
        var spec = CpuEmulator.Machines.Apple2Board.SpecWithDiskII(SystemRom(), iou, disk);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);
        var bus = machine.Space(AddressSpaceKind.Program);

        _ = bus.Read8(0xC0E9);                     // motor on so a REAL read would advance the head
        byte first = bus.Read8(0xC0EC);            // a real read latches + advances
        int posBefore = disk.BitPosForTest;

        // A debugger peek of $C0EC must be open-bus + leave the head/latch untouched.
        bool ok = bus.TryPeek8(0xC0EC, out byte peeked);
        Assert.True(ok);
        Assert.Equal(0x00, peeked);                // open-bus, side-effect-free
        Assert.Equal(posBefore, disk.BitPosForTest);   // the head did NOT advance
        Assert.Equal(first, disk.LatchForTest);    // the latch is unchanged

        // And a subsequent REAL read still advances from where the real reads left off (peek was inert).
        _ = bus.Read8(0xC0EC);
        Assert.NotEqual(posBefore, disk.BitPosForTest);
    }

    [Fact]
    public void A_real_6502_poll_loop_reads_the_track_nibbles_into_RAM()
    {
        // A track of distinctive valid GCR bytes after some sync.
        var image = new SyntheticFluxImage(trackCount: 35);
        image.SetTrackNibbles(0, new byte[] { 0xFF, 0xFF, 0xD5, 0xAA, 0x96, 0xAD, 0xDA, 0x96 });
        var disk = new Apple2DiskII(image);
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state, disk);
        var spec = CpuEmulator.Machines.Apple2Board.SpecWithDiskII(SystemRom(), iou, disk);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);   // interpreter (the oracle)
        var bus = machine.Space(AddressSpaceKind.Program);

        // Motor on, then the canonical read loop, storing 8 recovered nibbles to $0300..$0307:
        //   LDA $C0E9            ; motor on            AD E9 C0
        //   LDX #$00             ; index               A2 00
        // poll:
        //   LDA $C0EC            ; read data latch      AD EC C0
        //   BPL poll             ; bit7 clear? keep polling   10 FB
        //   STA $0300,X          ; store the nibble     9D 00 03
        //   INX                  ;                      E8
        //   CPX #$08             ;                      E0 08
        //   BNE poll             ;                      D0 F3   (-> $0205, the poll label, NOT $0203)
        //   JMP *                ; spin                 4C <here>
        var prog = new byte[]
        {
            0xAD, 0xE9, 0xC0,          // $0200 LDA $C0E9  (motor on)
            0xA2, 0x00,                // $0203 LDX #$00
            0xAD, 0xEC, 0xC0,          // $0205 LDA $C0EC  (poll)
            0x10, 0xFB,                // $0208 BPL $0205
            0x9D, 0x00, 0x03,          // $020A STA $0300,X
            0xE8,                      // $020D INX
            0xE0, 0x08,                // $020E CPX #$08
            0xD0, 0xF3,                // $0210 BNE $0205  (poll); $0212 + (-13) = $0205
            0x4C, 0x12, 0x02,          // $0212 JMP $0212  (spin)
        };
        for (int i = 0; i < prog.Length; i++) bus.Write8((uint)(0x0200 + i), prog[i]);
        machine.Cpu.SetRegister("PC", 0x0200);

        machine.Run(200_000);   // plenty to recover 8 nibbles via the poll loop

        // Every stored byte is a valid GCR nibble (bit 7 set), and the distinctive track bytes appear.
        var got = new List<byte>();
        for (uint a = 0x0300; a < 0x0308; a++) got.Add(bus.Read8(a));
        Assert.All(got, b => Assert.True((b & 0x80) != 0, $"stored ${b:X2} must be a GCR nibble"));
        // The distinctive sequence D5 AA 96 AD DA 96 is recovered in order (sync $FF may lead).
        AssertSubsequence(new byte[] { 0xD5, 0xAA, 0x96, 0xAD, 0xDA, 0x96 }, got);
    }
}
