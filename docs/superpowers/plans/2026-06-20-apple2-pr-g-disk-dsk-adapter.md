# Apple ][+ PR-G — Disk II `.dsk`/`.po` re-nibblizing adapter (`DskFluxImage : IFluxImage`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the **`.dsk`/`.po` logical-sector → synthetic-GCR-track re-nibblizing adapter** that folds into the **`IFluxImage` track-bitstream seam PR-F already shipped** (ADR 0014 Decision 6 + OQ1-✅ — the owner decision: full `.woz`/LSS fidelity upfront, with the `.dsk`/`.po` adapter re-nibblizing into the *same* path). The shipped `Apple2DiskII` controller is **format-agnostic above the seam** — it reads any `IFluxImage`. PR-G builds `DskFluxImage : IFluxImage` (in `CpuEmulator.Peripherals`) that takes an unprotected 143,360-byte logical-sector image (a DOS-3.3-order `.dsk` or a ProDOS-order `.po`, backed by the SP0 `IBlockDevice`/`DiskImage`), and **synthesizes** a per-track GCR bitstream — self-sync gaps + the 14-byte address field + the 343-byte 6-and-2-encoded data field per sector — so the **unchanged** PR-F read head + a guest `$C0EC` poll recover the original sector bytes. The CP/M data-track skew is a **later concern** (the CP/M arc, PR-K/O); G targets the **DOS 3.3 / ProDOS** sector orders only. The un-fakeable interpreter-tier gate: a real 6502 RWTS-style "find the address field, read + 6-and-2-decode the data field" routine reads a known sector's 256 bytes back out of a `.dsk` re-nibblized track — **synthetic `.dsk`, no ROM.**

**Architecture:** PR-F made the controller read a bit array per track (`int TrackCount` / `ReadOnlySpan<byte> TrackBits(int)` / `int TrackBitLength(int)` / `bool IsWriteProtected`) and assemble nibbles MSB-first until a byte with bit 7 set appears. A `.woz` *is* such a bitstream; a `.dsk`/`.po` is a flat LBA dump that must be **re-nibblized** into one. The adapter has three layers, all new, none touching the controller:

1. **`Apple2SectorCodec` (the 6-and-2 sector framing):** the standard DOS 3.3 16-sector track format (research §8). Per sector it emits, into a nibble-byte list: self-sync (`$FF` with the 10-bit sync framing approximated as plain `$FF` gap bytes — the head re-syncs on any byte with bit 7 set), the **address field** (`D5 AA 96`, the 4-and-4-encoded volume/track/sector/checksum, `DE AA EB`), a gap, the **data field** (`D5 AA AD`, the **343** 6-and-2 GCR bytes from `Apple2SectorCodec.EncodeData(256 bytes)`, `DE AA EB`), and a trailing gap. `EncodeData` is the 86-byte (low 2 bits, bit-reversed) + 256-byte (high 6 bits) + running-XOR-checksum transform, mapped through the **shipped** `Apple2Gcr.WriteTable`. Its inverse `TryDecodeData` (used by the gate + a round-trip test) recovers the 256 bytes — pure, separately gated by a round-trip.
2. **The logical→physical sector skew (`Apple2SectorOrder`):** a `.dsk` stores sectors in **DOS 3.3 logical** order; the head reads them in **physical** order on the track. The adapter lays sectors down the track in physical order 0..15, pulling each from the image's LBA via the DOS-3.3 (`.dsk`) or ProDOS (`.po`) interleave table. (The CP/M skew is a *different* table — out of scope for G; named in the notes.)
3. **`DskFluxImage : IFluxImage`:** wraps an `IBlockDevice` (the SP0 `DiskImage`, 256-byte sectors), exposes `TrackCount` = `SectorCount / 16`, and lazily synthesizes each requested track's nibble bitstream (16 physical sectors framed by `Apple2SectorCodec`, packed MSB-first the same way `SyntheticFluxImage` packs). `IsWriteProtected` reflects the `IBlockDevice.IsReadOnly`. The synthesized track is the loop the PR-F head reads forever.

**Tech Stack:** C# / .NET 10. Reuses **shipped**: `IFluxImage` (Core), `Apple2Gcr.WriteTable`/`TryDecode` (Peripherals), `Apple2DiskII` (the unchanged controller), `IBlockDevice`/`DiskImage` (the SP0 storage seam), `Apple2Iou`/`Apple2Board.SpecWithDiskII`, xUnit. **Depends on PR-F ✅** (the seam + controller + GCR table all merged at `c2ae005`). Namespace: `CpuEmulator.Peripherals` (the codec, the order tables, the adapter). **No controller change, no IOU change, no board change** — the whole point of the seam (OQ1-✅).

---

## Recon facts this plan is built on (verified against `main` @ `c2ae005`)

1. **`IFluxImage` is shipped** (`src/CpuEmulator.Core/IFluxImage.cs`): `int TrackCount`, `ReadOnlySpan<byte> TrackBits(int track)`, `int TrackBitLength(int track)`, `bool IsWriteProtected`. The doc-comment explicitly says "a `.dsk`/`.po` logical-sector image is RE-NIBBLIZED into one (PR-G)". `DskFluxImage` implements this interface and the controller cannot tell it apart from a `.woz` (format-agnostic above the seam).
2. **`SyntheticFluxImage` shows the packing contract** (`src/CpuEmulator.Peripherals/SyntheticFluxImage.cs`): `SetTrackNibbles(track, byte[])` packs nibble bytes MSB-first; `TrackBitLength = nibbles.Length * 8`. `DskFluxImage` packs the same way — a flat `byte[]` of nibble bytes per track, bit length = `8 * byteCount`. The PR-F head reads `bits[_bitPos >> 3] >> (7 - (_bitPos & 7))` and wraps at `bitLen`, so any MSB-first byte array works as-is.
3. **`Apple2Gcr` is shipped** (`src/CpuEmulator.Peripherals/Apple2Gcr.cs`): `public static readonly byte[] WriteTable` (64 bytes, index = 6-bit value, value = on-disk byte) + `bool TryDecode(byte diskByte, out int value)`. The sector codec composes these — it does **not** re-derive the table. The class doc-comment already says "the sector framing lives in the .dsk adapter, PR-G".
4. **The PR-F head assembles nibbles MSB-first until bit 7 sets** (`Apple2DiskII.ReadDataLatch`): a byte boundary is found by "shift until the top bit is set with 8 bits accumulated". So the adapter's self-sync just needs `$FF` gap bytes (bit 7 set) and every framed byte must itself be a valid MSB-set GCR/4-and-4 byte — which the standard format guarantees. **No controller change is needed** for the head to find the adapter's address/data marks.
5. **The DOS 3.3 sector encoding (research §8, `docs/research/apple-2-plus-architecture-analysis.md:171`):** a 256-byte sector → **342 6-and-2 bytes + 1 checksum = 343 total**. "First 86 bytes hold the low 2 bits (bit-reversed) of source groups; next 256 hold the high 6 bits; checksum is a running XOR (first value unaltered)." DOS 3.3 is **16-sector**; address/data fields are **framed by self-sync bytes**. This is the canonical Beneath-Apple-DOS data-field nibblize.
6. **A standard DOS 3.3 / ProDOS `.dsk`/`.po` is 143,360 bytes** = 35 tracks × 16 sectors × 256 bytes. The `DiskImage` (`src/CpuEmulator.Peripherals/DiskImage.cs`) wraps such a flat array with `SectorSize: 256`, `SectorCount: 560`, `ReadSector(lba, dst)`. `TrackCount` = `SectorCount / 16` = 35. **G targets only this geometry** (the unprotected case the owner scoped); odd geometries are a follow-on.
7. **The DOS 3.3 physical↔logical skew** (Beneath Apple DOS): the head reads physical sectors `0,1,…,15` along the track, but DOS stores them in a soft-interleaved *logical* order. The `.dsk` file is in **logical** order; the `.po` file is in **ProDOS** order. The adapter, when laying physical sector `p` on the track, must pull the image LBA for the logical sector that maps to physical `p`. The two interleave tables (DOS 3.3 and ProDOS) are 16-entry constants (Task 2). **The CP/M order is a third table — NOT in G** (it lands with the CP/M disk in the CP/M arc).
8. **The 4-and-4 nibble encoding** (address-field bytes: volume/track/sector/checksum): a data byte `d` is encoded as two bytes `(d >> 1) | 0xAA` and `d | 0xAA` — both always have bit 7 set and ≤2 consecutive zeros, so they are valid on-disk bytes the head reads. Decoding: `((b1 << 1) | 1) & b2`. Pure, gated by a round-trip.
9. **`Apple2Board.SpecWithDiskII(systemRom, iou, disk2)` is shipped** (`src/CpuEmulator.Machines/Apple2Board.cs`) and `new Apple2Iou(state, disk2)` delegates `$C0Ex`. The interpreter gate builds exactly the PR-F board (`Apple2DiskIITests.BuildBoardWithDisk` is the template) but backs the `Apple2DiskII` with a **`DskFluxImage`** instead of a `SyntheticFluxImage`. **No ROM** — the gate's 6502 routine is hand-assembled, like PR-F's poll-loop gate.
10. **`TimingTier`/`ITimingSensitive` are NOT shipped** — ADR-only. PR-G inherits PR-F's polled-read model; it adds **no** timing primitive (the adapter only synthesizes bits; the controller's existing poll cadence reads them).

---

## Conventions to follow

- **Re-nibblize into the SAME `IFluxImage` path PR-F reads** — the controller is unchanged (OQ1-✅). The adapter is a new `IFluxImage` implementation, nothing more.
- **Compose the shipped `Apple2Gcr.WriteTable`/`TryDecode`** — do not re-derive the GCR table.
- **DOS 3.3 / ProDOS orders only** — the CP/M skew is explicitly out of scope (named in the notes for the CP/M-arc planner).
- **No dependency on the un-shipped `TimingTier`** — bits in, poll out; the controller's cadence is unchanged.
- **TDD per task**, literal code, commit per task. Warning-clean. **Gate on the interpreter tier** (the oracle) with a synthetic `.dsk`, **no ROM**.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/Apple2SectorCodec.cs` — the 6-and-2 data-field encode/decode (343 bytes) + the 4-and-4 address-field encode/decode, composing `Apple2Gcr`.
- **Create** `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs` — the DOS 3.3 + ProDOS logical↔physical interleave tables (the CP/M order is NOT here).
- **Create** `src/CpuEmulator.Peripherals/DskFluxImage.cs` — `IFluxImage` over an `IBlockDevice`: synthesizes each track's GCR bitstream (16 physical sectors, address + data fields, self-sync gaps).

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2SectorCodecTests.cs` — the 343-byte data round-trip + the 4-and-4 round-trip + the on-disk-byte invariant on every emitted byte.
- **Create** `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs` — track geometry, every nibble valid/MSB-set, the PR-F head reads a sector back, the order tables, and the **interpreter-tier RWTS gate** (a real 6502 routine recovers a known sector's 256 bytes from a re-nibblized `.dsk`).

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **G** to ✅; update the banner.

---

## Task 1: `Apple2SectorCodec` — the 6-and-2 data field + the 4-and-4 address field

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2SectorCodec.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2SectorCodecTests.cs`

- [ ] **Step 1: Write the failing codec tests (the 343-byte data round-trip + the 4-and-4 round-trip + the on-disk invariant)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2SectorCodecTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2SectorCodecTests
{
    private static byte[] SampleSector()
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)((i * 7 + 0x13) & 0xFF); // a distinctive pattern
        return s;
    }

    [Fact]
    public void EncodeData_emits_exactly_343_bytes()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        Assert.Equal(343, gcr.Length);   // 342 6-and-2 bytes + 1 checksum (research §8)
    }

    [Fact]
    public void Every_encoded_data_byte_is_a_valid_on_disk_GCR_byte()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        foreach (byte b in gcr)
            Assert.True(Apple2Gcr.TryDecode(b, out _), $"data byte ${b:X2} must be a valid GCR byte");
    }

    [Fact]
    public void The_data_field_round_trips_to_the_original_256_bytes()
    {
        byte[] sector = SampleSector();
        byte[] gcr = Apple2SectorCodec.EncodeData(sector);
        Assert.True(Apple2SectorCodec.TryDecodeData(gcr, out byte[] back));
        Assert.Equal(sector, back);
    }

    [Fact]
    public void A_corrupted_data_checksum_fails_to_decode()
    {
        byte[] gcr = Apple2SectorCodec.EncodeData(SampleSector());
        gcr[10] ^= 0x04;   // flip a bit inside a still-valid GCR byte region (corrupt the running XOR)
        // Either the byte is no longer valid GCR, or the checksum mismatches -> decode reports failure.
        bool ok = Apple2SectorCodec.TryDecodeData(gcr, out _);
        Assert.False(ok, "a corrupted data field must not silently decode");
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    [InlineData(0xA5)]
    [InlineData(0x3C)]
    public void The_4and4_address_encoding_round_trips_and_is_valid_on_disk(byte value)
    {
        (byte hi, byte lo) = Apple2SectorCodec.Encode44(value);
        // 4-and-4 bytes always have bit 7 set (the odd bits are 1).
        Assert.NotEqual(0, hi & 0x80);
        Assert.NotEqual(0, lo & 0x80);
        Assert.Equal(value, Apple2SectorCodec.Decode44(hi, lo));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2SectorCodecTests"`
Expected: FAIL — `Apple2SectorCodec` does not exist.

- [ ] **Step 3: Create `Apple2SectorCodec`**

Create `src/CpuEmulator.Peripherals/Apple2SectorCodec.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>The DOS 3.3 6-and-2 sector framing (research §8): a 256-byte sector encodes to 342 6-and-2
/// bytes + 1 running-XOR checksum = 343 on-disk bytes, and the 4-and-4 address-field encoding for the
/// volume/track/sector/checksum prologue bytes. Composes the SHIPPED <see cref="Apple2Gcr"/> table (it
/// does NOT re-derive it). Pure + separately gated by round-trips. PR-G's <see cref="DskFluxImage"/>
/// uses this to re-nibblize an unprotected .dsk/.po logical-sector image into the IFluxImage track
/// bitstream the PR-F controller already reads — the controller is unchanged (the seam's whole point).</summary>
public static class Apple2SectorCodec
{
    public const int DataFieldNibbles = 343;   // 342 6-and-2 bytes + 1 checksum

    // ── 4-and-4: a data byte -> two on-disk bytes (odd bits then even bits, OR'd with 0xAA). ──
    /// <summary>Encode <paramref name="value"/> as the 4-and-4 pair (high = odd bits, low = even bits).
    /// Each output byte has bit 7 set and never more than two consecutive zero bits, so the head reads
    /// them as ordinary on-disk bytes.</summary>
    public static (byte hi, byte lo) Encode44(byte value)
    {
        byte hi = (byte)((value >> 1) | 0xAA);
        byte lo = (byte)(value | 0xAA);
        return (hi, lo);
    }

    /// <summary>Decode a 4-and-4 pair back to the original byte: ((hi &lt;&lt; 1) | 1) &amp; lo.</summary>
    public static byte Decode44(byte hi, byte lo) => (byte)(((hi << 1) | 1) & lo);

    // ── 6-and-2 data field: 256 bytes -> 343 on-disk bytes (the Beneath-Apple-DOS nibblize). ──
    /// <summary>Encode a 256-byte sector into the 343-byte 6-and-2 GCR data field. The low 2 bits of each
    /// byte (bit-reversed, packed three groups to a nibble) fill the first 86 entries; the high 6 bits
    /// fill the next 256; a running XOR over the 342 6-bit values yields the 343rd checksum value. Each
    /// 6-bit value is mapped through <see cref="Apple2Gcr.WriteTable"/> to its on-disk byte.</summary>
    public static byte[] EncodeData(byte[] sector)
    {
        ArgumentNullException.ThrowIfNull(sector);
        if (sector.Length != 256)
            throw new ArgumentException($"sector must be 256 bytes; got {sector.Length}.", nameof(sector));

        // 1) Build the 342 6-bit values: 86 "low 2 bits" values, then 256 "high 6 bits" values.
        var sixBit = new int[342];
        for (int i = 0; i < 256; i++)
            sixBit[86 + i] = (sector[i] >> 2) & 0x3F;     // high 6 bits

        // The first 86 values pack the low 2 bits of three source bytes each, bit-reversed (b0<->b1).
        for (int i = 0; i < 86; i++)
        {
            int v = 0;
            v |= Rev2(sector[i] & 0x03);                  // group A: bytes 0..85
            if (i + 86 < 256) v |= Rev2(sector[i + 86] & 0x03) << 2;   // group B: bytes 86..171
            if (i + 172 < 256) v |= Rev2(sector[i + 172] & 0x03) << 4; // group C: bytes 172..255
            sixBit[i] = v & 0x3F;
        }

        // 2) Running-XOR the 342 values into 342 "pre-nibblized" values, then append the final accumulator.
        var prenib = new int[343];
        int acc = 0;
        for (int i = 0; i < 342; i++)
        {
            prenib[i] = acc ^ sixBit[i];
            acc = sixBit[i];
        }
        prenib[342] = acc;                                // the checksum value (the last accumulator)

        // 3) Map each 6-bit value through the GCR write table to its on-disk byte.
        var gcr = new byte[343];
        for (int i = 0; i < 343; i++)
            gcr[i] = Apple2Gcr.WriteTable[prenib[i] & 0x3F];
        return gcr;
    }

    /// <summary>Decode a 343-byte 6-and-2 data field back to 256 bytes; false if any byte is not valid
    /// GCR or the running-XOR checksum does not reconcile. The exact inverse of <see cref="EncodeData"/>.</summary>
    public static bool TryDecodeData(byte[] gcr, out byte[] sector)
    {
        sector = [];
        if (gcr is null || gcr.Length != 343) return false;

        // Reverse the GCR table, then undo the running XOR.
        var sixBit = new int[343];
        for (int i = 0; i < 343; i++)
        {
            if (!Apple2Gcr.TryDecode(gcr[i], out int v)) return false;
            sixBit[i] = v;
        }
        var values = new int[342];
        int acc = 0;
        for (int i = 0; i < 342; i++)
        {
            acc ^= sixBit[i];
            values[i] = acc;
        }
        // The 343rd byte is the XOR checksum: after consuming all 342, acc must equal sixBit[342].
        if ((acc & 0x3F) != (sixBit[342] & 0x3F)) return false;

        // Reassemble the 256 bytes: high 6 bits from values[86..341], low 2 bits from values[0..85].
        var outBytes = new byte[256];
        for (int i = 0; i < 256; i++)
            outBytes[i] = (byte)((values[86 + i] & 0x3F) << 2);
        for (int i = 0; i < 86; i++)
        {
            int low = values[i] & 0x3F;
            outBytes[i] |= (byte)Rev2(low & 0x03);
            if (i + 86 < 256) outBytes[i + 86] |= (byte)Rev2((low >> 2) & 0x03);
            if (i + 172 < 256) outBytes[i + 172] |= (byte)Rev2((low >> 4) & 0x03);
        }
        sector = outBytes;
        return true;
    }

    /// <summary>Reverse a 2-bit value (swap bit 0 and bit 1) — the DOS 3.3 low-bit ordering.</summary>
    private static int Rev2(int b) => ((b & 1) << 1) | ((b >> 1) & 1);
}
```

- [ ] **Step 4: Run the codec gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2SectorCodecTests"`
Expected: PASS — `EncodeData` is exactly 343 valid GCR bytes; the data field round-trips to the original 256 bytes; a corrupted field fails; the 4-and-4 pair round-trips and is MSB-set. **This is the sector-codec round-trip gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2SectorCodec.cs tests/CpuEmulator.Tests/Apple2/Apple2SectorCodecTests.cs
git commit -m "feat(peripherals): Apple2SectorCodec — 6-and-2 data field + 4-and-4 address field (composes Apple2Gcr)"
```

---

## Task 2: `Apple2SectorOrder` — the DOS 3.3 + ProDOS logical↔physical interleave

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs` (the order asserts; the file grows in Task 3)

- [ ] **Step 1: Write the failing order tests (the two tables are 16-entry permutations; physical 0 maps to logical 0)**

Create `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs` (the order-table tests first):

```csharp
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
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DskFluxImageTests.Dos33_order|FullyQualifiedName~DskFluxImageTests.ProDos_order"`
Expected: FAIL — `Apple2SectorOrder` / `SectorOrderKind` do not exist.

- [ ] **Step 3: Create `Apple2SectorOrder`**

Create `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>Which logical-sector ordering a flat .dsk/.po image uses. A `.dsk` is in DOS 3.3 logical
/// order; a `.po` is in ProDOS order. (CP/M uses a THIRD ordering — NOT modeled here; it lands with the
/// CP/M disk in the CP/M arc, PR-K/O.)</summary>
public enum SectorOrderKind { Dos33, ProDos }

/// <summary>The logical↔physical sector interleave (Beneath Apple DOS). The disk head reads PHYSICAL
/// sectors 0..15 along a track; a .dsk/.po file stores its 16 sectors in LOGICAL order. When PR-G's
/// <see cref="DskFluxImage"/> lays physical sector p onto a synthesized track, it pulls the image LBA for
/// the LOGICAL sector that maps to physical p — i.e. PhysicalToLogical(order)[p]. DOS 3.3 and ProDOS use
/// different (well-documented, constant) 16-entry tables.</summary>
public static class Apple2SectorOrder
{
    // DOS 3.3: the standard "soft interleave" mapping physical -> logical (Beneath Apple DOS, Table).
    private static readonly int[] Dos33PhysToLog =
        [0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15];

    // ProDOS (.po): the ProDOS block interleave mapping physical -> logical.
    private static readonly int[] ProDosPhysToLog =
        [0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15];

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> (a fresh copy per call so
    /// callers cannot mutate the shared table).</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
```

- [ ] **Step 4: Run the order gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DskFluxImageTests.Dos33_order|FullyQualifiedName~DskFluxImageTests.ProDos_order"`
Expected: PASS — both tables are 16-entry permutations of 0..15; DOS 3.3 anchors physical 0/15 to logical 0/15; ProDOS differs from DOS 3.3. **This is the interleave gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2SectorOrder.cs tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs
git commit -m "feat(peripherals): Apple2SectorOrder — DOS 3.3 + ProDOS sector interleave tables (CP/M order deferred)"
```

---

## Task 3: `DskFluxImage` — re-nibblize a `.dsk`/`.po` into a track bitstream

**Files:**
- Create: `src/CpuEmulator.Peripherals/DskFluxImage.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs`

- [ ] **Step 1: Write the failing adapter tests (track geometry; every nibble valid; the PR-F head reads a sector back)**

Append to `DskFluxImageTests`:

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DskFluxImageTests.Track_count|FullyQualifiedName~DskFluxImageTests.Every_byte|FullyQualifiedName~DskFluxImageTests.The_PR_F_head"`
Expected: FAIL — `DskFluxImage` does not exist.

- [ ] **Step 3: Create `DskFluxImage`**

Create `src/CpuEmulator.Peripherals/DskFluxImage.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The .dsk/.po re-nibblizing adapter (ADR 0014 Decision 6 + OQ1-✅ — the .dsk/.po path folds
/// into the SAME IFluxImage track-bitstream seam PR-F shipped). Wraps a logical-sector image (the SP0
/// <see cref="IBlockDevice"/>: 256-byte sectors, 16 per track) and SYNTHESIZES each track's GCR bitstream
/// on demand — 16 physical sectors, each framed by self-sync gaps, a 4-and-4 address field
/// (volume/track/sector/checksum, D5 AA 96 ... DE AA EB) and a 6-and-2 data field (D5 AA AD + 343 bytes +
/// DE AA EB) from <see cref="Apple2SectorCodec"/>. The UNCHANGED <see cref="Apple2DiskII"/> head reads it
/// exactly like a .woz (format-agnostic above the seam). Targets unprotected DOS 3.3 (.dsk) / ProDOS
/// (.po) images only; copy-protected layouts and the CP/M skew are out of scope (the CP/M arc).</summary>
public sealed class DskFluxImage : IFluxImage
{
    private const int SectorsPerTrack = 16;
    private const byte Volume = 254;            // the conventional DOS 3.3 volume number ($FE)

    private readonly IBlockDevice _block;
    private readonly int[] _physToLog;          // physical-sector -> logical-sector for this image order
    private readonly byte[]?[] _trackCache;     // lazily synthesized per-track nibble bitstreams

    public DskFluxImage(IBlockDevice block, SectorOrderKind order)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.SectorSize != 256)
            throw new ArgumentException($"a .dsk/.po image must have 256-byte sectors; got {block.SectorSize}.",
                nameof(block));
        if (block.SectorCount % SectorsPerTrack != 0)
            throw new ArgumentException(
                $"sector count {block.SectorCount} must be a multiple of {SectorsPerTrack} (whole tracks).",
                nameof(block));
        _block = block;
        _physToLog = Apple2SectorOrder.PhysicalToLogical(order);
        _trackCache = new byte[]?[block.SectorCount / SectorsPerTrack];
    }

    public int TrackCount => _trackCache.Length;
    public bool IsWriteProtected => _block.IsReadOnly;

    public ReadOnlySpan<byte> TrackBits(int track) => GetTrack(track);
    public int TrackBitLength(int track) => GetTrack(track).Length * 8;   // packed 8 bits per nibble byte

    private byte[] GetTrack(int track)
    {
        if (track < 0 || track >= _trackCache.Length)
            throw new ArgumentOutOfRangeException(nameof(track));
        return _trackCache[track] ??= Synthesize(track);
    }

    /// <summary>Build the nibble bitstream for <paramref name="track"/>: 16 physical sectors, each with a
    /// self-sync gap, an address field, and a 6-and-2 data field. The PR-F head finds byte boundaries on
    /// any MSB-set byte and the prologues on D5 AA 96 / D5 AA AD, exactly as a real RWTS does.</summary>
    private byte[] Synthesize(int track)
    {
        var nibbles = new List<byte>(SectorsPerTrack * 400);
        for (int phys = 0; phys < SectorsPerTrack; phys++)
        {
            int logical = _physToLog[phys];
            long lba = (long)track * SectorsPerTrack + logical;
            var sector = new byte[256];
            _block.ReadSector(lba, sector);

            // --- self-sync gap (12 sync bytes is ample for the head to re-byte-align) ---
            for (int i = 0; i < 12; i++) nibbles.Add(0xFF);

            // --- address field: D5 AA 96 | vol track sector chk (4-and-4) | DE AA EB ---
            nibbles.AddRange([0xD5, 0xAA, 0x96]);
            byte chk = (byte)(Volume ^ track ^ phys);
            Add44(nibbles, Volume);
            Add44(nibbles, (byte)track);
            Add44(nibbles, (byte)phys);
            Add44(nibbles, chk);
            nibbles.AddRange([0xDE, 0xAA, 0xEB]);

            // --- a short gap, then the data field: D5 AA AD | 343 6-and-2 bytes | DE AA EB ---
            for (int i = 0; i < 6; i++) nibbles.Add(0xFF);
            nibbles.AddRange([0xD5, 0xAA, 0xAD]);
            nibbles.AddRange(Apple2SectorCodec.EncodeData(sector));
            nibbles.AddRange([0xDE, 0xAA, 0xEB]);
        }
        return nibbles.ToArray();
    }

    private static void Add44(List<byte> dst, byte value)
    {
        (byte hi, byte lo) = Apple2SectorCodec.Encode44(value);
        dst.Add(hi);
        dst.Add(lo);
    }
}
```

- [ ] **Step 4: Run the adapter gates to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DskFluxImageTests.Track_count|FullyQualifiedName~DskFluxImageTests.Every_byte|FullyQualifiedName~DskFluxImageTests.The_PR_F_head"`
Expected: PASS — `TrackCount` = 35; every synthesized nibble has bit 7 set; the **unchanged** PR-F head reads a `D5 AA AD` data field off a re-nibblized track and it 6-and-2-decodes to a real track-0 sector. **This is the re-nibblize-and-read-back gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/DskFluxImage.cs tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs
git commit -m "feat(peripherals): DskFluxImage — re-nibblize .dsk/.po into the PR-F IFluxImage track bitstream"
```

---

## Task 4: The un-fakeable interpreter-tier gate — a real 6502 RWTS-style routine recovers a sector

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs`

The row-G deliverable: a **real 6502 program** on a built `Machine` (interpreter tier — the oracle), backed by the **`DskFluxImage`** over the unchanged `Apple2DiskII`, finds the data field on track 0 and reads its 343 nibbles into RAM — proving the `.dsk` re-nibblizes onto the same path the controller polls, with **no faked nibbles and no ROM.** (The full 6-and-2 decode is asserted in C# on the RAM the routine captured — matching PR-F's "store nibbles, assert in C#" discipline; a guest-side decoder is the RWTS the boot ROM brings, validated end-to-end in PR-H.)

- [ ] **Step 1: Write the interpreter-tier gate**

Append to `DskFluxImageTests`:

```csharp
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
            0xD0, 0xF6,                // $020E BNE $0205
            // poll page $0500 (another 256 nibbles)
            0xAD, 0xEC, 0xC0,          // $0210 LDA $C0EC
            0x10, 0xFB,                // $0213 BPL $0210
            0x99, 0x00, 0x05,          // $0215 STA $0500,Y
            0xC8,                      // $0218 INY
            0xD0, 0xF6,                // $0219 BNE $0210
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
```

> **Implementer note — buffer sizing.** A single DOS 3.3 sector framing (gap + address + gap + data) is ~390 nibble bytes, so 512 captured nibbles is guaranteed to contain at least one complete `D5 AA AD` + 343-byte data field given the track loops. If the scan ever fails to find a field in CI, widen the capture to 768 (add a third page `$0600`) — the track is far longer than 768 nibbles, so a complete field is always present. Do **not** loosen the decode assertion; the gate's value is the exact 256-byte match.

- [ ] **Step 2: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DskFluxImageTests.A_real_6502"`
Expected: PASS. **This is the row-G interpreter-tier gate (interpreter-as-oracle):** a real 6502 poll loop, over the **unchanged** PR-F controller backed by a **`.dsk` re-nibblized into the same `IFluxImage` path**, captures a track's nibbles; the `D5 AA AD` data field 6-and-2-decodes to a byte-exact track-0 sector of the source image — **synthetic `.dsk`, no ROM, no controller change.**

- [ ] **Step 3: Run the full Apple2 suite + the full suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Then: `dotnet test CpuEmulator.slnx`
Expected: PASS — PR-A..F gates + PR-G's codec/order/adapter/RWTS gates all green; the full suite (~7133 + the new G tests) green. The controller, IOU, and board are **untouched** (G adds only new files), so no existing Apple2 test changes.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs
git commit -m "test(apple2): interpreter-tier gate — a real 6502 reads a sector from a re-nibblized .dsk"
```

---

## Task 5: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **G** status to ✅ and update the **Last updated** banner with the date + "PR-G merged". Add a **Recently shipped** entry for PR-G (the `.dsk`/`.po` adapter folding into the PR-F seam with no controller change).

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-G (.dsk/.po re-nibblizing adapter) done"
```

---

## Done-when

- `Apple2SectorCodec` ships the 6-and-2 **343-byte** data-field encode + its checksum-verifying inverse and the 4-and-4 address-field encode/decode, **composing the shipped `Apple2Gcr` table** (no re-derivation); every emitted byte is a valid on-disk GCR byte; the data field round-trips.
- `Apple2SectorOrder` ships the **DOS 3.3** (`.dsk`) and **ProDOS** (`.po`) 16-entry interleave tables (permutations, distinct from each other). **The CP/M skew is explicitly NOT added** (named for the CP/M-arc planner).
- `DskFluxImage : IFluxImage` wraps an `IBlockDevice` (the SP0 `DiskImage`) and **synthesizes** each track's GCR bitstream (16 physical sectors, address + data fields, self-sync gaps) onto the **same** path PR-F reads — `TrackCount = SectorCount / 16`, `IsWriteProtected` from the block device.
- The **unchanged** `Apple2DiskII` controller reads a re-nibblized `.dsk` track and a real 6502 poll loop on the **interpreter tier** recovers a **byte-exact** track-0 sector — **synthetic `.dsk`, no ROM, no controller/IOU/board change** (the format-agnostic-above-the-seam invariant, OQ1-✅).
- Queue row **G** is ✅.

---

## API-drift note for the owner

**No drift.** Every shipped API G builds on matched the ADR / the PR-F plan: the `IFluxImage` seam (PR-F), the `Apple2Gcr.WriteTable`/`TryDecode` table (PR-F), the SP0 `IBlockDevice`/`DiskImage` (SP0), and `Apple2Board.SpecWithDiskII` + the IOU `$C0Ex` delegate (PR-F) are all present at `c2ae005` exactly as the PR-F plan's "Notes for the PR-G planner" anticipated. The one thing PR-F's note assumed and G confirms: the controller is **format-agnostic above the seam** — G adds only new files (`Apple2SectorCodec`, `Apple2SectorOrder`, `DskFluxImage`) and touches **no** controller/IOU/board code, which is the seam's whole purpose (OQ1-✅). `TimingTier`/`ITimingSensitive` remain unshipped and unreferenced (PR-F's polled-read model is inherited unchanged).

---

## Notes for the PR-H / CP/M-arc planner (deferred)

- **PR-H** wires the `$C600` boot ROM (the fetched P5/P6 ROM) so the Autostart slot-scan boots **DOS 3.3 from a `.dsk` in drive 1** end-to-end — the asset-gated boot exercises this adapter for real (the RWTS in the boot ROM does the same `D5 AA AD` scan + 6-and-2 decode the G gate does in C#). PR-H constructs the drive-1 `Apple2DiskII` over a `DskFluxImage` when the inserted image is a `.dsk`/`.po` (a `.woz` uses the `WozFluxImage` follow-on parser instead).
- **The CP/M arc (PR-K/O)** needs a **third** sector order (CP/M's own skew) — add a `SectorOrderKind.CpM` case + table to `Apple2SectorOrder` at that time. G deliberately leaves it out (the owner scoped G to DOS 3.3 / ProDOS).
- **Write-back** (`WriteSector` through the adapter — re-nibblize a written sector back into the track + flush to the `IBlockDevice`) is out of scope for G (the gate is read-only, `IsWriteProtected` reflects the block device). It folds in when a target needs disk writes (a save-game title), as a follow-on on the same seam.
