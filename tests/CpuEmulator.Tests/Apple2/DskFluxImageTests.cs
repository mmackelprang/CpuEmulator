using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class DskFluxImageTests
{
    [Fact]
    public void Dos33_order_is_a_16_entry_permutation()
    {
        int[] map = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, map.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), map.ToHashSet()); // a permutation of 0..15
        Assert.Equal(0, map[0]);    // physical 0 == logical 0 (the DOS 3.3 anchor)
        Assert.Equal(15, map[15]);  // physical 15 == logical 15
    }

    [Fact]
    public void ProDos_order_is_a_16_entry_permutation_distinct_from_Dos33()
    {
        int[] po = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos);
        int[] dos = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, po.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), po.ToHashSet());
        Assert.NotEqual(dos, po);   // the two interleaves differ (that is the .dsk vs .po distinction)
    }

    // A 143,360-byte DOS 3.3 image (35 tracks x 16 sectors x 256). Each sector is filled with a byte
    // that encodes its (track, sector) so a recovered sector identifies itself.
    private static byte[] BuildDos33Image()
    {
        var img = new byte[35 * 16 * 256];
        for (int t = 0; t < 35; t++)
        for (int logical = 0; logical < 16; logical++)
        {
            int lba = t * 16 + logical;
            for (int i = 0; i < 256; i++)
                img[lba * 256 + i] = (byte)((t * 16 + logical + i) & 0xFF);
        }
        return img;
    }

    private static DskFluxImage Dos33Flux()
    {
        var block = new DiskImage(BuildDos33Image(), sectorSize: 256, isReadOnly: true);
        return new DskFluxImage(block, SectorOrderKind.Dos33);
    }

    [Fact]
    public void Track_count_is_sectors_over_16()
    {
        DskFluxImage flux = Dos33Flux();
        Assert.Equal(35, flux.TrackCount);   // 560 sectors / 16
    }

    [Fact]
    public void Every_byte_of_a_synthesized_track_is_a_valid_on_disk_byte()
    {
        DskFluxImage flux = Dos33Flux();
        ReadOnlySpan<byte> bits = flux.TrackBits(17);   // an arbitrary middle track
        Assert.True(flux.TrackBitLength(17) == bits.Length * 8);
        foreach (byte b in bits)
            Assert.True((b & 0x80) != 0, $"every nibble byte must have bit 7 set; got ${b:X2}");
    }

    [Fact]
    public void The_PR_F_head_reads_a_known_sector_back_out_of_a_renibblized_track()
    {
        // Drive the UNCHANGED PR-F controller over the re-nibblized track and software-decode a sector
        // the way RWTS does: scan for the data prologue D5 AA AD, pull 343 nibbles, 6-and-2 decode them.
        DskFluxImage flux = Dos33Flux();
        var disk = new Apple2DiskII(flux);
        disk.MotorOnForTest();

        // Pull a long run of nibbles off track 0 and find a D5 AA AD data field, then decode it.
        var stream = new List<byte>();
        for (int i = 0; i < 20_000; i++)
        {
            byte b = disk.ReadDataLatch();
            if ((b & 0x80) != 0) stream.Add(b);
        }
        Assert.True(TryReadFirstDataField(stream, out byte[] decoded),
            "expected a D5 AA AD data field of 343 GCR bytes that 6-and-2 decodes");

        // The decoded 256 bytes match SOME sector of track 0 (any of the 16 — we found the first one).
        Assert.Equal(256, decoded.Length);
        Assert.True(MatchesAnyTrack0Sector(decoded), "the decoded sector must match a real track-0 sector");
    }

    [Fact]
    public void A_runtime_inserted_dsk_image_is_read_back_through_the_head()
    {
        // Start with an empty-ish controller (drive 1 = a blank synthetic image), motor on.
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        disk.MotorOnForTest();

        // Build a .dsk image FROM BYTES at runtime (the upload/library path: bytes -> DiskImage ->
        // DskFluxImage), then INSERT it into drive 1 while the "machine" is running.
        var block = new DiskImage(BuildDos33Image(), sectorSize: 256, isReadOnly: true);
        var dskFlux = new DskFluxImage(block, SectorOrderKind.Dos33);
        disk.Insert(drive: 1, image: dskFlux);

        // The very next poll loop reads a real sector off the runtime-inserted image (PR-G's proof, but
        // post-insert): recover the first data field and confirm it matches a known track-0 sector.
        var stream = new List<byte>();
        for (int i = 0; i < 20_000; i++)
        {
            byte b = disk.ReadDataLatch();
            if ((b & 0x80) != 0) stream.Add(b);
        }
        Assert.True(TryReadFirstDataField(stream, out byte[] decoded),
            "a runtime-inserted .dsk must be readable through the head");
        Assert.Equal(256, decoded.Length);
        Assert.True(MatchesAnyTrack0Sector(decoded),
            "the decoded sector must match a known track-0 sector of the inserted image");
    }

    private static bool TryReadFirstDataField(List<byte> stream, out byte[] decoded)
    {
        decoded = [];
        for (int i = 0; i + 3 + 343 <= stream.Count; i++)
        {
            if (stream[i] == 0xD5 && stream[i + 1] == 0xAA && stream[i + 2] == 0xAD)
            {
                var gcr = stream.GetRange(i + 3, 343).ToArray();
                if (Apple2SectorCodec.TryDecodeData(gcr, out decoded)) return true;
            }
        }
        return false;
    }

    private static bool MatchesAnyTrack0Sector(byte[] decoded)
    {
        byte[] img = BuildDos33Image();
        for (int logical = 0; logical < 16; logical++)
        {
            var sector = new byte[256];
            Array.Copy(img, logical * 256, sector, 0, 256);
            if (sector.AsSpan().SequenceEqual(decoded)) return true;
        }
        return false;
    }

    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;     // reset -> $D000 (unused here)
        return rom;
    }

    [Fact]
    public void A_real_6502_finds_the_data_field_and_reads_a_sector_from_a_renibblized_dsk()
    {
        // The UNCHANGED PR-F controller, backed by a DskFluxImage (a re-nibblized .dsk), wired into a real
        // Apple ][+ board exactly like Apple2DiskIITests.BuildBoardWithDisk — but the image is a .dsk.
        var block = new DiskImage(BuildDos33Image(), sectorSize: 256, isReadOnly: true);
        var flux = new DskFluxImage(block, SectorOrderKind.Dos33);
        var disk = new Apple2DiskII(flux);
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state, disk);
        var spec = CpuEmulator.Machines.Apple2Board.SpecWithDiskII(SystemRom(), iou, disk);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);   // interpreter (the oracle)
        var bus = machine.Space(AddressSpaceKind.Program);

        // A 6502 routine: motor on, then poll $C0EC and store every nibble (bit-7-set byte) to a $0400
        // ring buffer of 1024 bytes. We then scan that RAM in C# for the D5 AA AD data field and decode it
        // (the same split PR-F's gate uses: the CPU does the timing-faithful poll; C# asserts the bytes).
        //   LDA $C0E9            ; motor on             AD E9 C0      $0200
        //   LDY #$00             ; low index            A0 00         $0203
        // poll:
        //   LDA $C0EC            ; read data latch      AD EC C0      $0205
        //   BPL poll             ; bit7 clear? poll     10 FB         $0208
        //   STA $0400,Y          ; store nibble         99 00 04      $020A
        //   INY                  ;                      C8            $020D
        //   BNE poll             ; 256 nibbles then...  D0 F6         $020E  (-> $0205, -10)
        //   INC $020A+2? no -> simpler: spin           4C ...        we only need 256 nibbles? Need >400.
        // To capture a whole sector framing we store 4 pages ($0400..$07FF) by stepping the high byte:
        //   we keep it simple: 256 nibbles is < one sector framing (~400). So store to $0400,X with a
        //   16-bit count via two pages. Implement as: Y wraps at 256 four times, bumping the STA page.
        // For determinism we instead store 256 nibbles per page across 4 pages using self-modifying the
        // STA high byte; but to stay simple + robust we store 256 nibbles and rely on the track LOOPING:
        // a single 256-nibble grab will not always contain a full 346-byte sector framing. So capture 512:
        //   page A ($0400) then page B ($0500). Use X for the page toggle.
        var prog = new byte[]
        {
            0xAD, 0xE9, 0xC0,          // $0200 LDA $C0E9   (motor on)
            0xA0, 0x00,                // $0203 LDY #$00
            // poll page $0400 (256 nibbles)
            0xAD, 0xEC, 0xC0,          // $0205 LDA $C0EC
            0x10, 0xFB,                // $0208 BPL $0205
            0x99, 0x00, 0x04,          // $020A STA $0400,Y
            0xC8,                      // $020D INY
            0xD0, 0xF5,                // $020E BNE $0205  ($0210 + (-11) = $0205)
            // poll page $0500 (another 256 nibbles)
            0xAD, 0xEC, 0xC0,          // $0210 LDA $C0EC
            0x10, 0xFB,                // $0213 BPL $0210
            0x99, 0x00, 0x05,          // $0215 STA $0500,Y
            0xC8,                      // $0218 INY
            0xD0, 0xF5,                // $0219 BNE $0210  ($021B + (-11) = $0210)
            0x4C, 0x1B, 0x02,          // $021B JMP $021B   (spin)
        };
        for (int i = 0; i < prog.Length; i++) bus.Write8((uint)(0x0200 + i), prog[i]);
        machine.Cpu.SetRegister("PC", 0x0200);
        machine.Run(400_000);   // ample to capture 512 nibbles via the poll loop

        // Pull the 512 captured nibbles back out of RAM ($0400..$05FF) and scan for the data field.
        var captured = new List<byte>(512);
        for (uint a = 0x0400; a < 0x0600; a++) captured.Add(bus.Read8(a));

        Assert.True(TryReadFirstDataField(captured, out byte[] decoded),
            "the captured nibble stream must contain a D5 AA AD data field that 6-and-2 decodes");
        Assert.True(MatchesAnyTrack0Sector(decoded),
            "the decoded sector must equal a real track-0 sector of the source .dsk");
    }
}
