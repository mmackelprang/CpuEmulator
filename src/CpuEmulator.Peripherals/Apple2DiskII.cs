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
    private const long MotorOffDelayCycles = 1_020_500; // ~1 s at ~1.0205 MHz (the 556 one-shot)

    // Two drive slots, 1-based ([1] = drive 1, [2] = drive 2; [0] is unused). PR-F/G shipped a single
    // build-time image (drive 1); Q makes drive 2 real and adds runtime insert/eject (design T-D).
    private readonly IFluxImage?[] _drives = new IFluxImage?[3];

    private int _halfTrack;         // 0..(2*TrackCount-2); track = _halfTrack / 2
    private int _bitPos;            // head position within the current track's bitstream
    private byte _latch;            // the data latch (bit 7 set => a complete nibble is ready)
    private bool _motorOn;

    private IScheduler? _scheduler;
    private ScheduledEvent? _pendingMotorOff;
    private readonly bool[] _phase = new bool[4];   // the 4 stepper phase magnets
    private int _lastPhaseOn;                        // the most recently energized phase (for the step model)
    private int _drive = 1;                          // selected drive (1 or 2; PR-F models drive 1)

    /// <summary>Build the controller with <paramref name="image"/> inserted into drive 1 (the shipped
    /// single-image construction — the SoftCard CP/M disk, the synthetic boot image). Drive 2 starts
    /// empty; both drives can be swapped at runtime via <see cref="Insert"/> / <see cref="Eject"/>.</summary>
    public Apple2DiskII(IFluxImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _drives[1] = image;
    }

    /// <summary>Insert <paramref name="image"/> into <paramref name="drive"/> (1 or 2) at runtime — the
    /// in-session swap (design T-D / D12: the library dropdown and the upload path both land bytes here).
    /// Format-agnostic above the IFluxImage seam: a .woz image and a .dsk/.po DskFluxImage are inserted
    /// identically. Re-seeks the head to the track start (the disk just changed under it). Replacing an
    /// already-inserted image is allowed (a re-insert). Does NOT spin the motor — the light follows the
    /// real $C0E9/$C0E8 motor, never the insert (design D10).</summary>
    public void Insert(int drive, IFluxImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (drive is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(drive), drive, "Disk II has drives 1 and 2.");
        _drives[drive] = image;
        if (drive == _drive) _bitPos = 0;     // re-seek the active drive's head to the new image's start
    }

    /// <summary>Eject <paramref name="drive"/>'s image at runtime (design D13: allowed mid-access, no
    /// confirm — nothing is destroyed; a re-insert re-reads). An empty drive reads nothing (the head
    /// never assembles a bit-7-set byte). Ejecting an already-empty drive is a no-op.</summary>
    public void Eject(int drive)
    {
        if (drive is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(drive), drive, "Disk II has drives 1 and 2.");
        _drives[drive] = null;
        if (drive == _drive) _bitPos = 0;
    }

    /// <summary>Whether <paramref name="drive"/> currently holds an image (for the surface's drive-state
    /// readout; the controller owns the truth).</summary>
    public bool HasImage(int drive) => drive is >= 1 and <= 2 && _drives[drive] is not null;

    public string Name => "apple2disk2";

    public void Realize(IMachineContext context) => _scheduler = context.Scheduler;

    // The controller maps no $C0xx page (the IOU owns it); these are unreachable.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test inspectors.</summary>
    public int HalfTrackForTest => _halfTrack;
    public int BitPosForTest => _bitPos;
    public byte LatchForTest => _latch;
    public bool MotorOnForTestProperty => _motorOn;
    public int SelectedDriveForTest => _drive;

    /// <summary>The REAL motor state (the $C0E9 on / $C0E8-with-556-delay off, ADR 0014 Decision 6) —
    /// the host reads this for the drive-activity light in the <c>ST</c> status frame. It is NOT set by
    /// inserting an image (design D10 / interactions §4.2 — the light is "not faked on insert"); it
    /// follows the guest's motor switches and lingers ~1 s after the last access, exactly as the lamp
    /// on a real Disk II does.</summary>
    public bool MotorOn => _motorOn;

    /// <summary>Test-only: force the motor on without a soft-switch (the real $C0E9 path is Access).</summary>
    internal void MotorOnForTest() => _motorOn = true;

    /// <summary>Called by the IOU for every $C0E0-$C0EF access (offset 0xE0-0xEF). Returns the bus value
    /// for a read ($C0EC returns the data latch); other switches return a floating-bus 0. The side effect
    /// (stepper/motor/select) happens on ANY access. Exactly one Access call per bus access (a read's
    /// $C0Ex side effect rides BusValue; a write's rides ApplyAnyAccessSideEffect — the PR-E discipline).
    /// </summary>
    public byte Access(byte offset, bool isRead)
    {
        switch (offset & 0x0F)
        {
            case 0x0: SetPhase(0, false); break;
            case 0x1: SetPhase(0, true);  break;
            case 0x2: SetPhase(1, false); break;
            case 0x3: SetPhase(1, true);  break;
            case 0x4: SetPhase(2, false); break;
            case 0x5: SetPhase(2, true);  break;
            case 0x6: SetPhase(3, false); break;
            case 0x7: SetPhase(3, true);  break;
            case 0x8: RequestMotorOff();  break;   // $C0E8: ~1 s 556 delay
            case 0x9: TurnMotorOn();      break;   // $C0E9: motor on now
            case 0xA: _drive = 1;         break;   // $C0EA: select drive 1
            case 0xB: _drive = 2;         break;   // $C0EB: select drive 2
            case 0xC: return ReadDataLatch();      // $C0EC: read the data latch (shift a nibble)
            case 0xD: _latch = 0;         break;   // $C0ED ($C08D,X): reset sequencer + clear latch
            case 0xE: case 0xF: break;             // Q7L/Q7H read/write-mode latch (read mode = PR-F)
        }
        return 0x00;   // floating bus for the non-data switches
    }

    private void SetPhase(int phase, bool on)
    {
        bool was = _phase[phase];
        _phase[phase] = on;
        if (on && !was)
        {
            // The 4-phase stepper: energizing a phase adjacent (mod 4) to the last-on phase moves the
            // head a half-track toward it. +1 (mod 4) steps inward (higher tracks), -1 outward.
            int delta = ((phase - _lastPhaseOn) & 3) switch
            {
                1 => +1,   // adjacent ascending -> inward (toward higher tracks)
                3 => -1,   // adjacent descending -> outward
                _ => 0,    // same or opposite phase -> no net half-track step
            };
            // Only an actual half-track step re-seeks the head + advances the reference phase. A same- or
            // opposite-phase rising edge (delta == 0) leaves the head — and _lastPhaseOn — put, so an
            // opposite-phase blip cannot corrupt the direction of the NEXT real step (the model PR-G /
            // copy-protection stepping needs).
            if (delta != 0)
            {
                int tracks = _drives[_drive]?.TrackCount ?? 1;
                int max = 2 * (tracks - 1);
                _halfTrack = Math.Clamp(_halfTrack + delta, 0, max);
                _bitPos = 0;            // a track change re-seeks the head to the track start
                _lastPhaseOn = phase;
            }
        }
    }

    private void TurnMotorOn()
    {
        _pendingMotorOff?.Cancel();
        _pendingMotorOff = null;
        _motorOn = true;
    }

    private void RequestMotorOff()
    {
        if (_scheduler is null) { _motorOn = false; return; } // no scheduler (bare unit) -> stop now
        _pendingMotorOff?.Cancel();
        _pendingMotorOff = _scheduler.ScheduleAt(
            _scheduler.CurrentCycle + MotorOffDelayCycles, () => { _motorOn = false; _pendingMotorOff = null; });
    }

    /// <summary>A $C0EC read: with the motor on, shift bits from the track bitstream MSB-first until a
    /// byte with bit 7 set has assembled, latch it, and return it. With the motor off, the latch does not
    /// advance (bit 7 stays clear). This is the authentic "poll until bit 7" read.</summary>
    public byte ReadDataLatch()
    {
        IFluxImage? image = _drives[_drive];
        if (!_motorOn || image is null)
            return (byte)(_latch & 0x7F);   // motor off OR empty drive: not ready, no advance

        int track = _halfTrack / 2;
        if (track >= image.TrackCount) return (byte)(_latch & 0x7F);   // head past the image's tracks
        ReadOnlySpan<byte> bits = image.TrackBits(track);
        int bitLen = image.TrackBitLength(track);
        if (bitLen <= 0) return 0x00;
        if (_bitPos >= bitLen) _bitPos %= bitLen;   // a drive-select can leave the head past a shorter track

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
