# Apple ][+ PR-F — Disk II controller: the `.woz`/LSS nibble path + the `IFluxImage` track-bitstream seam

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Apple2DiskII : IPeripheral` (ADR 0014 Decision 6) — the project's first real disk **controller**, modeling the **LSS sequencer + the nibble bitstream as the primary path** (the OWNER DECISION: full `.woz`/LSS fidelity UPFRONT, ADR 0014 OQ1 ✅ — no sector-first staging). It builds a small **`IFluxImage`-style track-bitstream seam beside `IBlockDevice`** (per ADR 0014 Decision 6 + the ✅ OWNER callout), owns the slot-6 `$C0E0–$C0EF` stepper/motor/sequencer soft switches (delegated by the IOU), imposes the **~1 s 556 motor-off delay** via the scheduler, and produces a **6-and-2 GCR** nibble stream a guest poll reads at `$C0EC`. A `.dsk`/`.po` re-nibblizing adapter (PR-G) folds into the **same** `IFluxImage` path later — the controller is format-agnostic above the seam. The un-fakeable interpreter-tier gate: a synthetic `.woz` track's nibble stream is read out byte-by-byte by a guest poll of `$C0EC` (the LSS data-latch read); the stepper soft switches move the head; the motor-off switch schedules the ~1 s delay before the motor stops; **synthetic `.woz`, no ROM**.

**Architecture:** Disk II on the ][+ is a **polled** nibble device (no IRQ — `IrqWiring.None`; the 6502 reads the data latch in a tight loop). The controller has three layers:

1. **`IFluxImage` (the new storage seam, beside `IBlockDevice`):** a per-track **bit array + bit length** abstraction — `int TrackCount`, `ReadOnlySpan<byte> TrackBits(int track)`, `int TrackBitLength(int track)`. A `.woz` file *is* this (a normalized exact-length per-track bitstream that loops); the `.dsk`/`.po` adapter (PR-G) *synthesizes* one by re-nibblizing. **It does not change `IBlockDevice`; it sits alongside it** (the way `IDisplayDevice` sits alongside `IBlockDevice`). PR-F ships the interface + a `SyntheticFluxImage` test double + a `WozFluxImage` parser stub (the full `.woz` chunk parser can be a thin follow-on; the gate uses a synthetic track so it runs with no asset).
2. **The LSS sequencer + read head:** the head sits at a bit position in the current track's bitstream. Each qualifying read of the data register (`$C0EC`, Q6-low/Q7-low = read mode) advances the head along the bitstream, assembling bits MSB-first into a **nibble** (a byte with bit 7 set — the on-disk invariant: every valid 6-and-2 byte has MSB set + ≤2 consecutive zero bits). When a complete nibble has shifted in, the data latch holds it and the read returns it. This is the authentic "the CPU polls until bit 7 is set" model — **no new timing primitive is needed; the polled-read cadence IS the timing** (the controller does not depend on the un-shipped `TimingTier`; see the recon note).
3. **The `$C0E0–$C0EF` soft switches (slot 6):** stepper phases `$C0E0–$C0E7` (4 phase magnets; the head moves a half-track when an adjacent phase energizes — the standard 4-phase stepper model), motor on `$C0E9` / motor off `$C0E8` (the **~1 s 556 one-shot delay** before the motor actually stops, scheduled via `IScheduler.ScheduleAt`), drive 1/2 select `$C0EA`/`$C0EB`, and the Q6/Q7 sequencer-mode latches `$C0EC–$C0EF` (`$C0EC` read = read data; `$C0ED` = `$C08D,X` for slot 6 resets the sequencer/clears the latch).

**IOU delegation + the `$C600` boot ROM:** the IOU owns `$C000–$C0FF`, which includes `$C0E0–$C0EF` — so Disk II is delegated `$C0Ex` by the IOU (the LC-delegation pattern from PR-E). The slot-6 **boot ROM** at `$C600` is a **separate page** (outside the IOU's `$C000` page), so it is a real `PeripheralSlot("disk2rom", …, 0xC600, 0x0100)` mapping the 256-byte P5/P6 boot ROM (fetched in PR-H; the bare synthetic gate needs no ROM). PR-F wires the `$C0Ex` delegation + (optionally) the `$C600` slot; the **gate** pokes a synthetic track and polls `$C0EC` directly, no ROM.

**Tech Stack:** C# / .NET 10, the new `IFluxImage` (Core), `IScheduler.ScheduleAt` + `ScheduledEvent.Cancel()` for the motor-off one-shot (the `IntervalTimer` precedent), the IOU `$C0Ex` delegate seam (the PR-E pattern), 6-and-2 GCR encode/decode, xUnit. **Depends on PR-B** (the IOU + board). Namespace: `CpuEmulator.Peripherals` (the controller + the GCR + the synthetic image) + `CpuEmulator.Core` (the `IFluxImage` contract).

---

## Recon facts this plan is built on (verified against `main` @ `97a44d5`)

1. **`IFluxImage` does NOT exist yet** (a `grep` confirms no `IFluxImage` in `src/`). PR-F creates it in `CpuEmulator.Core`, **beside** the shipped `IBlockDevice` (`src/CpuEmulator.Core/IBlockDevice.cs`) — it does not modify `IBlockDevice`. This is the ADR 0014 Decision 6 + OQ1-✅ seam: a track-bitstream interface, not LBA sectors.
2. **`TimingTier` / `ITimingSensitive` are NOT shipped** — they appear only in ADRs (0009/0010/0014), not in `src/`. **PR-F does not depend on them.** The Disk II byte cadence is the **polled-read model** (the guest polls `$C0EC` until bit 7 sets; each qualifying read advances the bitstream). This is authentic Disk II behaviour and needs no new framework primitive. The `Fine` timing escalation in ADR 0014 Decision 6 is a **future dial** (when a copy-protection title needs sub-poll bit timing) — recorded as a follow-on, not built here. (Flag to the owner: the ADR assumed a shipped `TimingTier.Fine` to declare; it is not yet shipped, so PR-F uses the polled-read cadence + the scheduler for the motor delay — the right model regardless, and no blocker.)
3. **The IOU owns `$C000–$C0FF`** (`Apple2Board.cs`), including `$C0E0–$C0EF`. Disk II is **delegated** `$C0Ex` by the IOU — the same seam PR-E adds for `$C08x`. The IOU's `ApplyAnyAccessSideEffect` has `// $C0E0-$C0EF (Disk II) ... delegated in PR-F` with `default: break;`. **If PR-E shipped first**, the IOU already threads `isRead` + holds an optional delegate; PR-F adds a parallel `Apple2DiskII?` reference + the `case >= 0xE0 and <= 0xEF:` forward. (If PR-F is implemented before PR-E, add the `isRead` threading here — see Task 3.)
4. **The motor-off ~1 s delay** uses `IScheduler.ScheduleAt(CurrentCycle + delay, callback)` + `ScheduledEvent.Cancel()` — the `IntervalTimer` one-shot precedent (`src/CpuEmulator.Peripherals/IntervalTimer.cs:94`). ~1 s at ~1.0205 MHz ≈ **1_020_500 cycles**. Motor-on (`$C0E9`) cancels any pending off-timer + sets the motor on immediately; motor-off (`$C0E8`) schedules the stop ~1 s out (re-arming cancels the prior one-shot). The controller captures the scheduler in `Realize` (the `Apple2Video`/`SpectrumUla` precedent).
5. **6-and-2 GCR (research §8):** 64 valid on-disk bytes (`$96`..`$FF`), every valid byte has **MSB set** + **≤2 consecutive zero bits**. A 256-byte sector → **342 6-and-2 bytes + 1 checksum = 343**. DOS 3.3 is 16-sector. PR-F ships the GCR **write-translate table** (6-bit value → on-disk byte) + its inverse, gated by the invariant (every output byte has bit 7 set + ≤2 consecutive zeros). The full sector-framing (self-sync, address/data fields) is exercised by the `.dsk` adapter (PR-G); PR-F's gate uses a **synthetic nibble track** directly (a sequence of valid GCR bytes the head reads out).
6. **The slot-6 soft-switch map (research §8, slot-relative `X = slot×16` → `$C0Ex`):**
   | Address | Function |
   |---|---|
   | `$C0E0`/`$C0E1` | stepper phase 0 off/on |
   | `$C0E2`/`$C0E3` | stepper phase 1 off/on |
   | `$C0E4`/`$C0E5` | stepper phase 2 off/on |
   | `$C0E6`/`$C0E7` | stepper phase 3 off/on |
   | `$C0E8` | motor OFF (with the ~1 s 556 delay) |
   | `$C0E9` | motor ON |
   | `$C0EA`/`$C0EB` | select drive 1 / drive 2 |
   | `$C0EC` | Q6L — read the data latch (shift the next nibble) |
   | `$C0ED` | Q6H — `$C08D,X`: load/sense write-protect; resets sequencer + clears latch |
   | `$C0EE`/`$C0EF` | Q7L / Q7H — read/write-mode latch |
   The 4-phase stepper: the head moves a half-track toward the phase that energizes adjacent to the currently-energized one (the standard "phase N on while phase N±1 was last" half-track step). PR-F models the **half-track position** (0..69 full tracks → 0..139 half-tracks); even half-tracks are the readable tracks (`track = halfTrack / 2`).
7. **Disk II is polled — `IrqWiring.None`** (the board's existing wiring, PR-B). The controller raises no interrupt; its only scheduled event is the motor-off one-shot.
8. **The controller maps no `$C0xx` page of its own** (the IOU owns it); like the LC it is delegated `$C0Ex` and Realized via the IOU-forwards-Realize pattern (PR-E Task 1) OR a spare-slot mapping. The `$C600` boot ROM **is** a real slot (a different page) — but it is **not needed for the synthetic gate** and is added in PR-H (when the boot ROM is fetched). PR-F's gate constructs the controller + drives `$C0Ex` directly over a synthetic track.
9. **The 6502 core issues the RMW dummy read** (verified in PR-B's `A_real_STA_C030_double_toggles` gate). For Disk II this matters for the soft switches that toggle on any access; PR-F models each `Read`/`Write` bus access as one switch access (the IOU already does this).

---

## Conventions to follow

- **`.woz`/LSS is the PRIMARY path** (owner decision — no sector-first staging). The `IFluxImage` seam is built from the start; the gate reads a track bitstream, not logical sectors.
- **The new seam is BESIDE `IBlockDevice`**, in Core — it does not modify `IBlockDevice`.
- **No dependency on the un-shipped `TimingTier`** — the polled-read cadence is the timing; the scheduler handles the motor delay.
- **The IOU stays the single `$C000`-page owner**; Disk II is delegated `$C0Ex`.
- **TDD per task**, literal code, commit per task. Warning-clean. **Gate on the interpreter tier** (the oracle) with a synthetic track, **no ROM**.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Core`
- **Create** `src/CpuEmulator.Core/IFluxImage.cs` — the track-bitstream storage seam (beside `IBlockDevice`).

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/Apple2Gcr.cs` — the 6-and-2 GCR translate table + its inverse (the on-disk-byte invariant), pure + separately gated.
- **Create** `src/CpuEmulator.Peripherals/SyntheticFluxImage.cs` — an in-memory `IFluxImage` for the gate (poke a track's nibble bytes; it packs them into a bitstream). Also the foundation PR-G's `.dsk` adapter reuses.
- **Create** `src/CpuEmulator.Peripherals/Apple2DiskII.cs` — `IPeripheral`: the slot-6 soft-switch decode, the 4-phase stepper, the motor + ~1 s delay, the LSS read head over the `IFluxImage` bitstream.
- **Modify** `src/CpuEmulator.Peripherals/Apple2Iou.cs` — delegate `$C0E0–$C0EF` (offsets `0xE0–0xEF`) to an optional `Apple2DiskII` (the PR-E delegate-seam pattern).

### `CpuEmulator.Machines`
- **Modify** `src/CpuEmulator.Machines/Apple2Board.cs` — a `SpecWithDiskII` overload that attaches the Disk II to the IOU + (optionally) maps the `$C600` boot-ROM slot.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2GcrTests.cs` — the 6-and-2 invariant (64 bytes, MSB set, ≤2 consecutive zeros) + round-trip.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs` — the LSS read-head gate (a synthetic track's nibbles read out at `$C0EC`), the stepper, the motor + ~1 s delay, and the interpreter-tier poll-loop gate.

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **F** to ✅; update the banner.

---

## Task 1: `IFluxImage` (the track-bitstream seam) + the 6-and-2 GCR table

**Files:**
- Create: `src/CpuEmulator.Core/IFluxImage.cs`
- Create: `src/CpuEmulator.Peripherals/Apple2Gcr.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2GcrTests.cs`

- [ ] **Step 1: Write the failing GCR test (the on-disk-byte invariant + round-trip)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2GcrTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2GcrTests
{
    [Fact]
    public void There_are_exactly_64_valid_on_disk_bytes()
    {
        Assert.Equal(64, Apple2Gcr.WriteTable.Length);
    }

    [Fact]
    public void Every_valid_byte_has_MSB_set_and_at_most_two_consecutive_zero_bits()
    {
        foreach (byte b in Apple2Gcr.WriteTable)
        {
            Assert.True((b & 0x80) != 0, $"byte ${b:X2} must have MSB set");
            Assert.True(NoMoreThanTwoConsecutiveZeros(b), $"byte ${b:X2} has >2 consecutive zero bits");
        }
    }

    [Fact]
    public void First_is_0x96_and_last_is_0xFF()
    {
        Assert.Equal(0x96, Apple2Gcr.WriteTable[0]);
        Assert.Equal(0xFF, Apple2Gcr.WriteTable[^1]);
    }

    [Fact]
    public void The_inverse_round_trips_every_6_bit_value()
    {
        for (int v = 0; v < 64; v++)
        {
            byte disk = Apple2Gcr.WriteTable[v];
            Assert.True(Apple2Gcr.TryDecode(disk, out int back));
            Assert.Equal(v, back);
        }
    }

    private static bool NoMoreThanTwoConsecutiveZeros(byte b)
    {
        int run = 0;
        for (int i = 7; i >= 0; i--)
        {
            if ((b & (1 << i)) == 0) { run++; if (run > 2) return false; }
            else run = 0;
        }
        return true;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2GcrTests"`
Expected: FAIL — `Apple2Gcr` does not exist.

- [ ] **Step 3: Create the `IFluxImage` seam**

Create `src/CpuEmulator.Core/IFluxImage.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Track-bitstream backing for nibble-level disk controllers (the Apple Disk II, ADR 0014 Decision 6),
/// SITTING BESIDE <see cref="IBlockDevice"/> — NOT a replacement. A flux image is a per-track bit array
/// + an exact bit length that LOOPS (the on-disk track is a continuous spiral the head reads forever); a
/// `.woz` file IS this, and a `.dsk`/`.po` logical-sector image is RE-NIBBLIZED into one (PR-G). This is
/// the honest seam for copy-protection-grade fidelity: a track bitstream cannot be expressed as LBA
/// sectors, so it gets its own interface (the way <see cref="IDisplayDevice"/> sits beside
/// <see cref="IBlockDevice"/>). The controller owns the LSS sequencer + the head; the image only stores
/// bits.
/// </summary>
public interface IFluxImage
{
    /// <summary>Number of quarter/half/whole tracks the image addresses (the controller maps its
    /// half-track head position onto this; a 35-track DOS 3.3 disk has 35 whole tracks).</summary>
    int TrackCount { get; }

    /// <summary>The packed bits of <paramref name="track"/> (MSB-first within each byte). The valid bit
    /// count is <see cref="TrackBitLength"/> — the last byte may be partially used; the head wraps at the
    /// bit length, not the byte length.</summary>
    ReadOnlySpan<byte> TrackBits(int track);

    /// <summary>The exact number of VALID bits in <paramref name="track"/>'s stream (the loop point).
    /// The head advances bit by bit and wraps to 0 at this count.</summary>
    int TrackBitLength(int track);

    /// <summary>Whether the image is write-protected (a write-mode store is ignored when true).</summary>
    bool IsWriteProtected { get; }
}
```

- [ ] **Step 4: Create the GCR table**

Create `src/CpuEmulator.Peripherals/Apple2Gcr.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>The Apple Disk II 6-and-2 GCR translate table (research §8): 64 valid on-disk bytes
/// ($96..$FF), each with the MSB set and at most two consecutive zero bits (the AGC noise-floor
/// constraint). WriteTable[v] maps a 6-bit value (0..63) to its on-disk byte; TryDecode inverts it.
/// A 256-byte sector encodes to 342 6-and-2 bytes + 1 checksum = 343 (the sector framing lives in the
/// .dsk adapter, PR-G; PR-F uses raw nibble streams). Pure + separately gated by the invariant.</summary>
public static class Apple2Gcr
{
    /// <summary>The 64 canonical 6-and-2 on-disk bytes, in 6-bit-value order (index = the source 6-bit
    /// value, value = the byte written to disk). This is the standard DOS 3.3 / Beneath-Apple-DOS table.</summary>
    public static readonly byte[] WriteTable =
    [
        0x96, 0x97, 0x9A, 0x9B, 0x9D, 0x9E, 0x9F, 0xA6,
        0xA7, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB2, 0xB3,
        0xB4, 0xB5, 0xB6, 0xB7, 0xB9, 0xBA, 0xBB, 0xBC,
        0xBD, 0xBE, 0xBF, 0xCB, 0xCD, 0xCE, 0xCF, 0xD3,
        0xD6, 0xD7, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE,
        0xDF, 0xE5, 0xE6, 0xE7, 0xE9, 0xEA, 0xEB, 0xEC,
        0xED, 0xEE, 0xEF, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6,
        0xF7, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
    ];

    private static readonly int[] ReadTable = BuildReadTable();

    /// <summary>Map an on-disk byte back to its 6-bit value; false if the byte is not a valid GCR byte.</summary>
    public static bool TryDecode(byte diskByte, out int value)
    {
        value = ReadTable[diskByte];
        return value >= 0;
    }

    private static int[] BuildReadTable()
    {
        var t = new int[256];
        Array.Fill(t, -1);
        for (int v = 0; v < WriteTable.Length; v++)
            t[WriteTable[v]] = v;
        return t;
    }
}
```

- [ ] **Step 5: Run the GCR gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2GcrTests"`
Expected: PASS — 64 valid bytes, MSB set + ≤2 consecutive zeros, `$96`..`$FF`, round-trip. **This is the GCR invariant gate.**

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core/IFluxImage.cs src/CpuEmulator.Peripherals/Apple2Gcr.cs tests/CpuEmulator.Tests/Apple2/Apple2GcrTests.cs
git commit -m "feat(core/peripherals): IFluxImage track-bitstream seam + 6-and-2 GCR table"
```

---

## Task 2: `SyntheticFluxImage` + the LSS read head (the nibble stream a poll reads)

**Files:**
- Create: `src/CpuEmulator.Peripherals/SyntheticFluxImage.cs`
- Create: `src/CpuEmulator.Peripherals/Apple2DiskII.cs` (the read head first)
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`

- [ ] **Step 1: Write the failing read-head test (a synthetic track's nibbles read out at `$C0EC`)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2DiskIITests.Polling|FullyQualifiedName~Apple2DiskIITests.With_the_motor"`
Expected: FAIL — `SyntheticFluxImage` / `Apple2DiskII` do not exist.

- [ ] **Step 3: Create `SyntheticFluxImage`**

Create `src/CpuEmulator.Peripherals/SyntheticFluxImage.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>An in-memory IFluxImage for tests + the .dsk/.po re-nibblizing adapter (PR-G): poke a
/// track's nibble bytes; they pack MSB-first into the track's bitstream (the bit length = 8 * byteCount).
/// A real .woz parser (WozFluxImage, a thin follow-on) produces the same IFluxImage from a file; the
/// controller cannot tell them apart (format-agnostic above the seam — the whole point of OQ1-✅).</summary>
public sealed class SyntheticFluxImage : IFluxImage
{
    private readonly byte[][] _trackBytes;
    private readonly int[] _trackBitLen;

    public SyntheticFluxImage(int trackCount)
    {
        _trackBytes = new byte[trackCount][];
        _trackBitLen = new int[trackCount];
        for (int t = 0; t < trackCount; t++)
        {
            _trackBytes[t] = [0xFF];     // a 1-byte all-sync default (a blank-ish track)
            _trackBitLen[t] = 8;
        }
    }

    public int TrackCount => _trackBytes.Length;
    public bool IsWriteProtected => false;

    /// <summary>Pack a sequence of nibble bytes (each already a valid on-disk byte) MSB-first into
    /// <paramref name="track"/>'s bitstream; the bit length becomes 8 * nibbles.Length (the loop point).</summary>
    public void SetTrackNibbles(int track, byte[] nibbles)
    {
        ArgumentNullException.ThrowIfNull(nibbles);
        _trackBytes[track] = (byte[])nibbles.Clone();
        _trackBitLen[track] = nibbles.Length * 8;
    }

    public ReadOnlySpan<byte> TrackBits(int track) => _trackBytes[track];
    public int TrackBitLength(int track) => _trackBitLen[track];
}
```

- [ ] **Step 4: Create `Apple2DiskII` (the read head first)**

Create `src/CpuEmulator.Peripherals/Apple2DiskII.cs` (the soft-switch decode + stepper + motor delay land in Task 3; this is the head + the data-latch read):

```csharp
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

    private int _halfTrack;         // 0..(2*TrackCount-2); track = _halfTrack / 2
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
```

- [ ] **Step 5: Run the read-head gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2DiskIITests.Polling|FullyQualifiedName~Apple2DiskIITests.With_the_motor"`
Expected: PASS — polling `$C0EC` recovers the track's GCR nibbles in order (every emitted byte has bit 7 set; the distinctive bytes appear as a subsequence); with the motor off the latch does not advance. **This is the LSS read-head gate** — the synthetic `.woz` track's nibble stream is read out byte-by-byte, no ROM.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Peripherals/SyntheticFluxImage.cs src/CpuEmulator.Peripherals/Apple2DiskII.cs tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs
git commit -m "feat(peripherals): Disk II LSS read head over the IFluxImage bitstream + SyntheticFluxImage"
```

---

## Task 3: The slot-6 soft switches — stepper, motor + ~1 s delay, drive select (IOU-delegated)

**Files:**
- Modify: `src/CpuEmulator.Peripherals/Apple2DiskII.cs`
- Modify: `src/CpuEmulator.Peripherals/Apple2Iou.cs`
- Modify: `src/CpuEmulator.Machines/Apple2Board.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`

- [ ] **Step 1: Write the failing soft-switch tests (stepper moves the head; motor delay)**

Append to `Apple2DiskIITests`. The stepper + motor are driven through the **real IOU + a built Machine** so the ~1 s delay can be exercised on the scheduler:

```csharp
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
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2DiskIITests.Energizing|FullyQualifiedName~Apple2DiskIITests.Motor|FullyQualifiedName~Apple2DiskIITests.Re_requesting"`
Expected: FAIL — the soft-switch decode, the stepper, the motor delay, `HalfTrackForTest`, `MotorOnForTestProperty`, the `Apple2Iou(state, disk)` ctor, and `SpecWithDiskII` do not exist.

- [ ] **Step 3: Add the soft-switch decode + stepper + motor delay to `Apple2DiskII`**

Replace the `Realize`/`MotorOnForTest` region of `Apple2DiskII` and add the `$C0Ex` access entry + the stepper + the motor one-shot:

```csharp
    private const long MotorOffDelayCycles = 1_020_500; // ~1 s at ~1.0205 MHz (the 556 one-shot)

    private IScheduler? _scheduler;
    private ScheduledEvent? _pendingMotorOff;
    private readonly bool[] _phase = new bool[4];   // the 4 stepper phase magnets
    private int _lastPhaseOn = 0;                   // the most recently energized phase (for the step model)
    private int _drive = 1;                         // selected drive (1 or 2; PR-F models drive 1)

    public void Realize(IMachineContext context) => _scheduler = context.Scheduler;

    /// <summary>Test inspectors.</summary>
    public int HalfTrackForTest => _halfTrack;
    public bool MotorOnForTestProperty => _motorOn;
    internal void MotorOnForTest() => _motorOn = true;

    /// <summary>Called by the IOU for every $C0E0-$C0EF access (offset 0xE0-0xEF). Returns the bus value
    /// for a read ($C0EC returns the data latch); other switches return a floating-bus 0. The side effect
    /// (stepper/motor/select) happens on ANY access.</summary>
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
            case 0x9: MotorOn();          break;   // $C0E9: motor on now
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
            int max = 2 * (_image.TrackCount - 1);
            _halfTrack = Math.Clamp(_halfTrack + delta, 0, max);
            _bitPos = 0;            // a track change re-seeks the head to the track start
            _lastPhaseOn = phase;
        }
    }

    private void MotorOn()
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
```

- [ ] **Step 4: Add the IOU `$C0Ex` delegate + the board overload**

In `Apple2Iou.cs`, add the optional `Apple2DiskII` (parallel to the PR-E LC delegate) + the `$C0Ex` forward. If PR-E already added the `isRead`-threaded `ApplyAnyAccessSideEffect` + an LC field, add a `disk2` field and a `case >= 0xE0 and <= 0xEF:` arm; otherwise add the `isRead` threading here too:

```csharp
    private readonly Apple2VideoState _state;
    private readonly Apple2LanguageCard? _lc;     // PR-E (may be null)
    private readonly Apple2DiskII? _disk2;        // PR-F (may be null)

    public Apple2Iou(Apple2VideoState state) : this(state, null, null) { }
    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc) : this(state, lc, null) { }
    public Apple2Iou(Apple2VideoState state, Apple2DiskII? disk2) : this(state, null, disk2) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc, Apple2DiskII? disk2)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state; _lc = lc; _disk2 = disk2;
    }
```

In `BusValue` (the read path), forward `$C0Ex` reads to the disk (so `$C0EC` returns the latched nibble):

```csharp
        if (o is >= 0xE0 and <= 0xEF)
            return _disk2?.Access(o, isRead: true) ?? 0x00;
```

And in `ApplyAnyAccessSideEffect` for the **write** path (the LC double-call caveat from PR-E applies — route reads through `BusValue`, writes through here):

```csharp
            case >= 0xE0 and <= 0xEF:
                _disk2?.Access(o, isRead: false);
                break;
```

> **Realize wiring:** add `_disk2?.Realize(context);` to `Apple2Iou.Realize` (the IOU-forwards-Realize pattern PR-E established) so the Disk II captures the scheduler for the motor delay without needing its own bus slot. (Same for `_lc?.Realize(context);` if PR-E used this path.)

In `Apple2Board.cs`, add `SpecWithDiskII`:

```csharp
    /// <summary>The ][+ board with the Disk II controller wired (ADR 0014 Decision 6). The controller is
    /// delegated $C0E0-$C0EF by the IOU (already attached) and is Realized by the IOU (IOU-forwards-Realize)
    /// so it captures the scheduler for the ~1 s motor-off delay. The $C600 boot ROM slot is added in PR-H
    /// (when the boot ROM is fetched); the synthetic gate needs no ROM.</summary>
    public static BoardSpec SpecWithDiskII(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2)
    {
        ArgumentNullException.ThrowIfNull(disk2);
        return Spec(systemRom, iou);   // the IOU (holding disk2) Realizes it; no extra slot needed
    }
```

> **Implementer note — the `$C600` boot ROM (deferred to PR-H).** The slot-6 boot ROM is a 256-byte ROM at `$C600` (a different page from the IOU's `$C000`), mapped as `new PeripheralSlot("disk2rom", romPeripheral, 0xC600, 0x0100)` — but it is **only needed for a real boot** (PR-H fetches it). PR-F's gate pokes a synthetic track and polls `$C0EC` directly, so `SpecWithDiskII` does **not** map `$C600`. When PR-H adds it, the boot ROM's first bytes carry the slot-6 signature (`$Cn01=$20,$Cn03=$00,$Cn05=$03,$Cn07=$3C`, research §9) so the Autostart slot-scan finds it.

- [ ] **Step 5: Run the soft-switch gates to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2DiskIITests.Energizing|FullyQualifiedName~Apple2DiskIITests.Motor|FullyQualifiedName~Apple2DiskIITests.Re_requesting"`
Expected: PASS — adjacent-phase energizing advances the head; motor-off lingers ~1 s then stops; motor-on cancels a pending off. **This is the stepper + motor-delay gate.**

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2DiskII.cs src/CpuEmulator.Peripherals/Apple2Iou.cs src/CpuEmulator.Machines/Apple2Board.cs tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs
git commit -m "feat(peripherals): Disk II slot-6 soft switches — stepper, motor + ~1s 556 delay (IOU-delegated)"
```

---

## Task 4: The un-fakeable interpreter-tier gate — a real 6502 poll loop reads the nibble stream

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`

The row-F deliverable: a **real 6502 program** on a built `Machine` (interpreter tier — the oracle) turns the motor on and polls `$C0EC` in the authentic "load until bit 7 set, store the nibble" loop, recovering the synthetic track's bytes into RAM — no faked nibbles.

- [ ] **Step 1: Write the failing/passing interpreter poll-loop gate**

Append to `Apple2DiskIITests`:

```csharp
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
        //   BNE poll             ;                      D0 F1
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
            0xD0, 0xF1,                // $0210 BNE $0205
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
```

- [ ] **Step 2: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2DiskIITests.A_real_6502_poll"`
Expected: PASS. **This is the row-F interpreter-tier gate (interpreter-as-oracle):** a real 6502 "poll `$C0EC` until bit 7, store the nibble" loop, with no faked data, recovers the synthetic `.woz` track's GCR bytes into RAM — the whole LSS/nibble/soft-switch arc validated on the oracle tier, synthetic `.woz`, **no ROM**.

- [ ] **Step 3: Run the full Apple2 suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Expected: PASS — PR-B/C/D/E gates + PR-F's GCR/read-head/soft-switch/poll-loop gates all green (the base no-disk `Apple2Board.Spec` overload is unchanged).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs
git commit -m "test(apple2): interpreter-tier gate — a real 6502 poll loop reads the nibble stream"
```

---

## Task 5: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **F** status to ✅ and update the **Last updated** banner with the date + "PR-F merged".

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-F (Disk II — .woz/LSS) done"
```

---

## Done-when

- `IFluxImage` (the track-bitstream seam) ships in Core **beside** `IBlockDevice` (it does not modify it); `SyntheticFluxImage` packs nibble bytes into a looping bitstream (the foundation PR-G's `.dsk` adapter reuses).
- `Apple2Gcr` ships the 6-and-2 GCR table (64 valid bytes, MSB set + ≤2 consecutive zeros, `$96`..`$FF`) + its round-tripping inverse.
- `Apple2DiskII` is a polled LSS controller: the read head reads the track bitstream out as GCR nibbles a `$C0EC` poll recovers; the slot-6 soft switches drive the 4-phase stepper (head half-tracks), the motor on/off with the **~1 s 556 delay** (via the scheduler), and drive select — all **delegated by the IOU** (the `$C0Ex` seam), Realized via the IOU.
- A **real 6502 poll loop** on the **interpreter tier** (the oracle) recovers the synthetic `.woz` track's nibbles into RAM — no faked data, **no ROM**.
- The controller is **format-agnostic above the `IFluxImage` seam** — PR-G's `.dsk`/`.po` re-nibblizing adapter folds into the same path with no controller change.
- The base no-disk `Apple2Board.Spec` overload is unchanged. Queue row **F** is ✅.

---

## API-drift note for the owner

**One drift the ADR assumed:** ADR 0014 Decision 6 said the controller "declares `TimingTier.Fine` for its sequencer (ADR 0009 Decision 3)". **`TimingTier`/`ITimingSensitive` are NOT shipped** — they exist only in the ADRs, not in `src/`. PR-F does **not** depend on them: the Disk II byte cadence is modeled the authentic way (the **polled-read model** — the guest polls `$C0EC` until bit 7 sets; each qualifying read advances the bitstream), and the motor-off delay uses the shipped `IScheduler.ScheduleAt`. This is the correct model regardless of whether `TimingTier` ever ships, and it is no blocker. The `Fine`/sub-poll-bit-timing escalation (a copy-protection title that needs cycle-exact bit arrival between polls) remains a **future dial** — and would be the first real consumer that motivates building `TimingTier`, if and when a target needs it. Everything else (the `IFluxImage` seam shape, the 6-and-2 GCR, the `$C0Ex` map, the ~1 s motor delay, polled/`IrqWiring.None`) matches the ADR.

---

## Notes for the PR-G / PR-H planner (deferred)

- **PR-G** (`.dsk`/`.po` re-nibblizing adapter) builds a `DskFluxImage : IFluxImage` that reads a logical-sector image (an `IBlockDevice` or raw bytes) and **synthesizes** a track bitstream (address field + data field + the 343-byte 6-and-2 sector encoding + self-sync gaps) into the **same** `SyntheticFluxImage`-style packing PR-F reads. The controller is unchanged — that is the whole point of the seam (OQ1-✅). PR-G's gate: a `.dsk` re-nibblizes into a track whose nibbles PR-F's read head + a guest poll recover.
- **PR-H** adds the `$C600` boot ROM slot (the fetched P5/P6 ROM, signature-carrying) so the Autostart slot-scan boots DOS 3.3 from a `.dsk` in drive 1 — the asset-gated end-to-end gate. The `WozFluxImage` file parser (the real `.woz` chunk reader producing an `IFluxImage` from a fetched `.woz`) is a thin follow-on PR-H/PR-Q wires in; PR-F's synthetic image proves the controller above it.
- **PR-Q** (in-session disk insert/eject) gives the controller a "load this `IFluxImage` as drive N / eject drive N" runtime method — the `IFluxImage` seam is exactly where the bytes land.
