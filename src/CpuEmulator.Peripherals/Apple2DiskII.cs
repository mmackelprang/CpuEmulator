using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ Disk II controller (slot 6, ADR 0014 Decision 6 + OQ1-✅ — full .woz/LSS
/// fidelity UPFRONT). A POLLED nibble device (no IRQ): the LSS read head sits at a bit position in the
/// current track's IFluxImage bitstream; each qualifying $C0EC read shifts bits in MSB-first until a
/// byte with bit 7 set has assembled (the on-disk invariant), then returns it from the data latch. The
/// stepper ($C0E0-$C0E7), the motor on/off with the ~1 s 556 delay ($C0E9/$C0E8), and the drive select
/// ($C0EA/$C0EB) are the slot-6 soft switches (Task 3), delegated by the IOU which owns the $C000 page.
/// Format-agnostic above the IFluxImage seam: .woz reads its own bitstream; .dsk/.po (PR-G) re-nibblizes
/// into a synthetic one. No new timing primitive — the polled-read cadence IS the byte timing.</summary>
public sealed class Apple2DiskII : IPeripheral
{
    private readonly IFluxImage _image;

    // CS0649: _halfTrack is read-only until Task 3 wires the stepper that assigns it. Scoped pragma
    // removed in Task 3 (the soft-switch commit), where SetPhase mutates _halfTrack.
#pragma warning disable CS0649
    private int _halfTrack;         // 0..(2*TrackCount-2); track = _halfTrack / 2
#pragma warning restore CS0649
    private int _bitPos;            // head position within the current track's bitstream
    private byte _latch;            // the data latch (bit 7 set => a complete nibble is ready)
    private bool _motorOn;

    public Apple2DiskII(IFluxImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
    }

    public string Name => "apple2disk2";

    public void Realize(IMachineContext context) { /* scheduler captured in Task 3 for the motor delay */ }

    // The controller maps no $C0xx page (the IOU owns it); these are unreachable.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test-only: force the motor on without a soft-switch (Task 3 adds the real $C0E9 path).</summary>
    internal void MotorOnForTest() => _motorOn = true;

    /// <summary>A $C0EC read: with the motor on, shift bits from the track bitstream MSB-first until a
    /// byte with bit 7 set has assembled, latch it, and return it. With the motor off, the latch does not
    /// advance (bit 7 stays clear). This is the authentic "poll until bit 7" read.</summary>
    public byte ReadDataLatch()
    {
        if (!_motorOn)
            return (byte)(_latch & 0x7F);   // not ready: bit 7 clear, no advance

        int track = _halfTrack / 2;
        ReadOnlySpan<byte> bits = _image.TrackBits(track);
        int bitLen = _image.TrackBitLength(track);
        if (bitLen <= 0) return 0x00;

        // Shift bits one at a time into a register MSB-first; a real disk byte begins with a 1 bit (the
        // MSB-set invariant), so we shift until the register's top bit is set with 8 bits accumulated.
        byte reg = 0;
        for (int guard = 0; guard < bitLen * 2 + 16; guard++)
        {
            int bit = (bits[_bitPos >> 3] >> (7 - (_bitPos & 7))) & 1;
            _bitPos = (_bitPos + 1) % bitLen;          // wrap at the exact bit length (the loop point)
            reg = (byte)((reg << 1) | bit);
            if ((reg & 0x80) != 0)                      // a complete nibble (top bit set) has assembled
            {
                _latch = reg;
                return _latch;
            }
        }
        return _latch;   // (a degenerate all-zero track: return the stale latch)
    }
}
