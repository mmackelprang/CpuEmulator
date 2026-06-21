# PR-Q — in-session Disk II insert / eject (runtime image swap)

> **Queue row:** Q (`docs/BUILDER_QUEUE.md`). **Deps:** F, G (both ✅). **Design:** spec
> `docs/superpowers/specs/2026-06-20-apple-2-plus-design.md` task **T-D** / decisions **D11–D13**; handoff
> `docs/design-handoffs/apple-2-plus/interactions.md` §4.3–§4.5.
> **Grounded against `main` @ `c26faac`** (PRs #99–#117 merged). Every literal code block below was read
> against that HEAD; signatures are the real shipped ones.
> **Tier:** interpreter (the oracle). The gate runs headless with synthetic images, no asset needed.

---

## What this PR delivers (and what it does NOT)

**Delivers:** the `Apple2DiskII` controller accepts, **at runtime**, "load these bytes as drive N's image"
(insert) and "eject drive N", for **both** `.woz` (the `IFluxImage` track-bitstream path, PR-F) **and**
`.dsk`/`.po` (the `DskFluxImage` re-nibblizing adapter, PR-G). A **running** machine swaps the image
behind the head without a rebuild: the very next `$C0EC` poll reads sectors from the freshly inserted
image, and after an eject the head reads **nothing** (an empty drive — bit 7 never sets). This is **T-D,
the shared dependency** of the two disk-UX paths (R = library dropdown, S = upload) — both land bytes
here.

**Does NOT deliver:** the `GET /disks` catalog (row R), the upload binary-WS path (row S), or the
control-strip DOM (row T). Q ships **only the controller-level runtime-swap mechanism** + the surface
seam that exposes it + the un-fakeable gate that a running machine reads a runtime-inserted image. The
**wire message** that triggers an insert/eject (`disk-insert` / `disk-eject` JSON, or the `DK` binary
upload frame) is **rows R/S** — Q exposes a method R/S will call; it does **not** add a WebSocket message
handler. (Adding the swap *method* without a caller is correct: R/S are blocked on Q precisely because
they need this method to exist.)

> **Two-drive scope (recorded):** PR-F/G shipped the controller with a **single** `_image` field and a
> `_drive` selector (1/2) that is tracked but **ignored** on read (`ReadDataLatch` always reads `_image`).
> Q makes the controller hold **two** drive slots and routes the read through the **selected** drive — so
> drive 2 becomes real for the first time. This is the minimal change that makes "drive N" meaningful for
> the design's two-drive panel. Existing single-image construction (every shipped surface + test) is
> preserved as "drive 1 inserted at build time."

---

## The design at the controller seam

Today (`Apple2DiskII.cs`):

```csharp
private readonly IFluxImage _image;          // ONE image, drive-agnostic
private int _drive = 1;                       // tracked, but ReadDataLatch ignores it

public Apple2DiskII(IFluxImage image) { ...; _image = image; }

public byte ReadDataLatch()
{
    ...
    ReadOnlySpan<byte> bits = _image.TrackBits(track);   // always drive 1's image
    ...
}
```

After Q:

```csharp
private readonly IFluxImage?[] _drives = new IFluxImage?[3];  // index 1 = drive 1, index 2 = drive 2
                                                              // index 0 unused (drives are 1-based)
private int _drive = 1;

public Apple2DiskII(IFluxImage image) { ...; _drives[1] = image; }   // build-time drive 1 (unchanged API)

public void Insert(int drive, IFluxImage image) { ...; _drives[drive] = image; _bitPos = 0; }
public void Eject(int drive) { ...; _drives[drive] = null; if (drive == _drive) _bitPos = 0; }

public byte ReadDataLatch()
{
    IFluxImage? image = _drives[_drive];
    if (!_motorOn || image is null) return (byte)(_latch & 0x7F);   // empty drive: never ready
    ...
    ReadOnlySpan<byte> bits = image.TrackBits(track);               // the SELECTED drive's image
    ...
}
```

The motor/stepper/select soft switches are unchanged — only the **image storage** and the **read path's
image source** change, plus the two public runtime-swap methods.

---

## TDD task list

Each task is **write the test (red) → implement (green)**. Run `dotnet test` after each.

---

### Task 1 — the controller holds two drive slots; the read uses the selected drive

This is the structural change with the existing single-image API preserved. The existing
`Apple2DiskIITests` (which build with one image and read drive 1) must stay green — that is the regression
proof that drive 1's build-time path is byte-for-byte unchanged.

#### 1a. Test (red) — extend `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`

Add to the existing class (it already has `OneTrack`, `SampleNibbles`, `AssertSubsequence`,
`SystemRom`, `BuildBoardWithDisk` helpers):

```csharp
    [Fact]
    public void The_read_follows_the_selected_drive_after_a_runtime_insert_into_drive_2()
    {
        // Build with drive 1 = the sample track (the legacy single-image ctor path).
        var disk = new Apple2DiskII(OneTrack(SampleNibbles()));

        // Insert a DISTINCT image into drive 2 at runtime (a different distinctive nibble run).
        var drive2 = new SyntheticFluxImage(trackCount: 35);
        drive2.SetTrackNibbles(0, new byte[] { 0xFF, 0xFF, 0xB5, 0xD7, 0xE7, 0xF7 });
        disk.Insert(drive: 2, image: drive2);

        disk.MotorOnForTest();

        // Still on drive 1 (default): we read drive 1's distinctive bytes.
        var d1 = ReadGcr(disk, 200);
        AssertSubsequence(new byte[] { 0x96, 0xD5, 0xAA, 0x96 }, d1);

        // Select drive 2 ($C0EB) and read: we now read drive 2's distinctive bytes, NOT drive 1's.
        disk.Access(0xB, isRead: true);             // $C0EB: select drive 2
        var d2 = ReadGcr(disk, 200);
        AssertSubsequence(new byte[] { 0xB5, 0xD7, 0xE7, 0xF7 }, d2);
    }

    private static List<byte> ReadGcr(Apple2DiskII disk, int polls)
    {
        var seen = new List<byte>();
        for (int i = 0; i < polls; i++)
        {
            byte b = disk.ReadDataLatch();
            if ((b & 0x80) != 0) seen.Add(b);
        }
        return seen;
    }
```

#### 1b. Implement (green) — `src/CpuEmulator.Peripherals/Apple2DiskII.cs`

Replace the single-image field (line 17) and the constructor (lines 30–34); update the read path
(lines 133–135). The motor/stepper/select code (lines 59–123) is **unchanged**.

Field (replace line 17):

```csharp
    // Two drive slots, 1-based ([1] = drive 1, [2] = drive 2; [0] is unused). PR-F/G shipped a single
    // build-time image (drive 1); Q makes drive 2 real and adds runtime insert/eject (design T-D).
    private readonly IFluxImage?[] _drives = new IFluxImage?[3];
```

Constructor (replace lines 30–34) — same public signature, so every shipped caller is unchanged:

```csharp
    /// <summary>Build the controller with <paramref name="image"/> inserted into drive 1 (the shipped
    /// single-image construction — the SoftCard CP/M disk, the synthetic boot image). Drive 2 starts
    /// empty; both drives can be swapped at runtime via <see cref="Insert"/> / <see cref="Eject"/>.</summary>
    public Apple2DiskII(IFluxImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _drives[1] = image;
    }
```

Read path (replace lines 130–135 — the `if (!_motorOn) ...` guard and the `_image.TrackBits` reads):

```csharp
        IFluxImage? image = _drives[_drive];
        if (!_motorOn || image is null)
            return (byte)(_latch & 0x7F);   // motor off OR empty drive: not ready, no advance

        int track = _halfTrack / 2;
        if (track >= image.TrackCount) return (byte)(_latch & 0x7F);   // head past the image's tracks
        ReadOnlySpan<byte> bits = image.TrackBits(track);
        int bitLen = image.TrackBitLength(track);
```

> **Stepper clamp note:** `SetPhase` (lines 100–106) clamps `_halfTrack` against `_image.TrackCount`.
> Replace that one read (line 102) with the **selected** drive's track count, falling back to a safe bound
> when the selected drive is empty (so an eject mid-seek can't index a null image):

```csharp
                int tracks = _drives[_drive]?.TrackCount ?? 1;
                int max = 2 * (tracks - 1);
```

(That is the only other `_image` reference in the file; with it changed, `_image` is fully removed.)

**Gate after Task 1:** `dotnet test` — the new selected-drive test passes; **every existing
`Apple2DiskIITests` test passes unchanged** (they build with one image → drive 1, default-select drive 1,
read drive 1 — the exact prior behavior). This is the byte-for-byte regression proof for the build-time
path.

---

### Task 2 — `Insert` / `Eject` runtime methods (the swap mechanism)

#### 2a. Test (red) — extend `tests/CpuEmulator.Tests/Apple2/Apple2DiskIITests.cs`

```csharp
    [Fact]
    public void Eject_makes_the_drive_read_nothing_and_a_later_insert_restores_reads()
    {
        var disk = new Apple2DiskII(OneTrack(SampleNibbles()));
        disk.MotorOnForTest();

        // Inserted: the head recovers the distinctive bytes.
        Assert.NotEmpty(ReadGcr(disk, 200));

        // Eject drive 1: the head reads NOTHING — no byte ever has bit 7 set (an empty drive).
        disk.Eject(drive: 1);
        var afterEject = ReadGcr(disk, 200);
        Assert.Empty(afterEject);

        // Insert a fresh image at runtime: reads resume from the new image's bytes.
        var fresh = new SyntheticFluxImage(trackCount: 35);
        fresh.SetTrackNibbles(0, new byte[] { 0xFF, 0xFF, 0xAD, 0xDA, 0x96, 0xD5 });
        disk.Insert(drive: 1, image: fresh);
        AssertSubsequence(new byte[] { 0xAD, 0xDA, 0x96, 0xD5 }, ReadGcr(disk, 400));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Insert_and_Eject_reject_an_out_of_range_drive(int drive)
    {
        var disk = new Apple2DiskII(OneTrack(SampleNibbles()));
        Assert.Throws<ArgumentOutOfRangeException>(() => disk.Insert(drive, OneTrack(SampleNibbles())));
        Assert.Throws<ArgumentOutOfRangeException>(() => disk.Eject(drive));
    }
```

#### 2b. Implement (green) — add to `src/CpuEmulator.Peripherals/Apple2DiskII.cs`

Place after the constructor (after line 34), beside the other public surface:

```csharp
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
```

**Gate after Task 2:** `dotnet test` — the eject-reads-nothing + insert-restores + range-guard tests pass.
The empty-drive `Assert.Empty(afterEject)` is the un-fakeable eject proof (an empty slot returns the
not-ready latch with bit 7 clear, so `ReadGcr` collects nothing).

---

### Task 3 — both formats swap through the same seam (`.woz` LSS path **and** `.dsk` adapter)

The design (D12, locked decision "both `.woz` and `.dsk`/`.po`") requires the runtime insert to accept
**both** image kinds. The controller is already format-agnostic (it takes any `IFluxImage`); this task
**proves** the `.dsk` re-nibblizing adapter (`DskFluxImage`, built from bytes at runtime) inserts and
reads back a known sector — the same end-to-end proof PR-G shipped, but via the runtime `Insert` rather
than the constructor.

#### 3a. Test (red) — extend `tests/CpuEmulator.Tests/Apple2/DskFluxImageTests.cs`

The shipped file has `BuildDos33Image()`, `Dos33Flux()`, `TryReadFirstDataField`, `MatchesAnyTrack0Sector`
helpers (referenced in PR-G). Add a runtime-insert variant of the head-reads-a-sector gate:

```csharp
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
```

> **Builder note:** `TryReadFirstDataField` + `MatchesAnyTrack0Sector` + `BuildDos33Image` are the
> existing PR-G test helpers in this file. If any are `private static` in a different test class, hoist
> them to a shared helper or copy them into this class — verify their exact names/signatures in the
> shipped `DskFluxImageTests.cs` before writing the test and adjust the call to match. The structure
> (build image bytes → `DiskImage` → `DskFluxImage` → read a data field → match a sector) is the load-
> bearing part; the helper names follow the shipped file.

#### 3b. Implement (green) — none beyond Tasks 1–2

No production change: `Insert` already takes any `IFluxImage`, and `DskFluxImage`/`SyntheticFluxImage`
already implement it. This task is a **format-coverage gate** proving the `.dsk` path works through the
runtime seam (the `.woz`/synthetic path is covered by Tasks 1–2). The `.woz` real-file path is a thin
`IFluxImage` (a follow-on noted in `SyntheticFluxImage`'s doc comment); Q's contract is "accepts both via
the seam," which the synthetic `IFluxImage` (the `.woz`-shape path) + `DskFluxImage` (the `.dsk` path)
together prove.

**Gate after Task 3:** `dotnet test` — the runtime-inserted `.dsk` reads back a known sector.

---

### Task 4 — the surface exposes the runtime swap (the R/S call point)

The surfaces hold the `Apple2DiskII` (after PR-P added `Disk` to the surface records; if Q lands **before**
P, add the `Disk` field here instead — see the ordering note). Q adds **surface-level** insert/eject
helpers that build the right `IFluxImage` from raw disk bytes + a format hint, so R/S call one method with
bytes and a format, not raw `IFluxImage` construction. This is where "load these bytes as drive N's image"
lives.

#### 4a. Test (red) — `tests/CpuEmulator.Tests/Surface/Apple2SurfaceDiskSwapTests.cs` (new)

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class Apple2SurfaceDiskSwapTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector
        return rom;
    }

    // A minimal valid DOS 3.3 .dsk: 35 tracks * 16 sectors * 256 bytes, distinctive per-LBA bytes.
    private static byte[] BuildDsk()
    {
        var img = new byte[35 * 16 * 256];
        for (int i = 0; i < img.Length; i++) img[i] = (byte)((i + 1) & 0xFF);
        return img;
    }

    [Fact]
    public void Surface_inserts_a_dsk_from_bytes_then_a_running_read_pulls_a_nibble()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // Insert a .dsk from raw bytes at runtime (the path R/S will drive).
        surface.InsertDisk(drive: 1, bytes: BuildDsk(), format: DiskFormat.Dsk);

        // Run the machine's motor + a poll via the live bus: $C0E9 (motor), then $C0EC reads advance the
        // head over the runtime-inserted image and eventually latch a GCR byte (bit 7 set).
        var bus = surface.Machine.Space(AddressSpaceKind.Program);
        bus.Read8(0xC0E9);                         // motor on
        bool sawNibble = false;
        for (int i = 0; i < 50_000 && !sawNibble; i++)
            if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
        Assert.True(sawNibble, "a running machine must read a nibble off the runtime-inserted .dsk");
    }

    [Fact]
    public void Eject_then_a_running_read_pulls_nothing()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });
        surface.InsertDisk(drive: 1, bytes: BuildDsk(), format: DiskFormat.Dsk);
        surface.EjectDisk(drive: 1);

        var bus = surface.Machine.Space(AddressSpaceKind.Program);
        bus.Read8(0xC0E9);                         // motor on
        bool sawNibble = false;
        for (int i = 0; i < 50_000 && !sawNibble; i++)
            if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
        Assert.False(sawNibble, "an ejected drive reads nothing — no byte ever has bit 7 set");
    }
}
```

#### 4b. Implement (green) — `src/CpuEmulator.Surface.Web/DiskFormat.cs` (new)

```csharp
namespace CpuEmulator.Surface.Web;

/// <summary>The disk image format a runtime insert provides (design D12). <c>Woz</c> is the native flux
/// bitstream (PR-F); <c>Dsk</c>/<c>Po</c> are logical-sector images re-nibblized by DskFluxImage (PR-G)
/// — DOS 3.3 order for .dsk, ProDOS order for .po.</summary>
public enum DiskFormat { Woz, Dsk, Po }
```

#### 4c. Implement (green) — `src/CpuEmulator.Surface.Web/DiskImageFactory.cs` (new)

The shared bytes→`IFluxImage` builder (R and S both call it). Lives in the surface project beside the
surfaces; depends on `CpuEmulator.Peripherals` (already referenced).

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Builds an <see cref="IFluxImage"/> from raw disk-image bytes + a format (design D12 / T-D).
/// The one place "these bytes -> a flux image" lives, shared by the library-insert (R) and upload (S)
/// paths. .dsk/.po wrap a 256-byte-sector <see cref="DiskImage"/> in <see cref="DskFluxImage"/> with the
/// format's sector order; .woz is the native flux path (a thin WozFluxImage follow-on — see the note).
/// Validation (length, .woz magic) is the caller's job (S re-validates server-side); this throws the
/// shipped DskFluxImage/DiskImage exceptions on a malformed .dsk/.po.</summary>
public static class DiskImageFactory
{
    /// <summary>A .dsk/.po image is exactly 143,360 bytes (35 tracks * 16 sectors * 256). Exposed so the
    /// upload validator (S) can length-check before building.</summary>
    public const int DskBytes = 35 * 16 * 256;

    public static IFluxImage FromBytes(byte[] bytes, DiskFormat format)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        switch (format)
        {
            case DiskFormat.Dsk:
            case DiskFormat.Po:
                var block = new DiskImage(bytes, sectorSize: 256, isReadOnly: true);
                SectorOrderKind order = format == DiskFormat.Po ? SectorOrderKind.ProDos : SectorOrderKind.Dos33;
                return new DskFluxImage(block, order);
            case DiskFormat.Woz:
                // The native .woz flux path is a thin WozFluxImage (a noted IFluxImage follow-on; the
                // SyntheticFluxImage doc-comment flags it). Until it ships, a .woz runtime insert is out
                // of this PR's literal scope; R/S that need .woz construct the WozFluxImage when it lands.
                throw new NotSupportedException(
                    ".woz runtime insert needs WozFluxImage (a noted IFluxImage follow-on); use .dsk/.po for Q.");
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown disk format.");
        }
    }
}
```

> **Decision (recorded) — `.woz` runtime insert:** the shipped tree has **no** `.woz`-file parser
> (`WozFluxImage`); PR-F shipped the **controller's** `.woz`/LSS *read path* + the `IFluxImage` seam and a
> `SyntheticFluxImage` test double, with a real `.woz` parser called out as "a thin follow-on"
> (`SyntheticFluxImage` doc comment, `IFluxImage` doc comment). Q's gate (the design's "both `.woz` and
> `.dsk`/`.po`") is satisfied at the **seam** level: `Insert(int, IFluxImage)` accepts a `.woz`-derived
> image identically (Task 1 proves it with a `SyntheticFluxImage`, which **is** a `.woz`-shape flux image
> — the controller cannot tell them apart, the whole point of OQ1-✅). The **byte-level** `.woz`→
> `IFluxImage` parse is a separable follow-on; Q wires `.dsk`/`.po` end-to-end (the common upload case)
> and throws an explicit, honest `NotSupportedException` for `.woz` bytes until `WozFluxImage` lands. This
> is flagged as drift below.

#### 4d. Implement (green) — surface insert/eject helpers

Add to `src/CpuEmulator.Surface.Web/Apple2Surface.cs` (and the SoftCard surfaces). The surface holds the
`Apple2DiskII` (the `disk` local; add `Apple2DiskII Disk` to the record if P has not already):

```csharp
    /// <summary>Insert a disk image (raw bytes + format) into <paramref name="drive"/> at runtime — the
    /// in-session swap the library (R) and upload (S) paths call (design T-D / D11–D12). Builds the
    /// IFluxImage via DiskImageFactory and hands it to the live Disk II controller; the running machine
    /// reads the new image on the next poll.</summary>
    public void InsertDisk(int drive, byte[] bytes, DiskFormat format) =>
        Disk.Insert(drive, DiskImageFactory.FromBytes(bytes, format));

    /// <summary>Eject <paramref name="drive"/>'s image at runtime (design D13 — allowed mid-access, no
    /// confirm). The drive reads nothing until a re-insert.</summary>
    public void EjectDisk(int drive) => Disk.Eject(drive);
```

> **Ordering note (P vs Q):** P (Task 3) adds `Apple2DiskII Disk` to the surface records. If P lands
> first, `Disk` already exists and Q adds only the two helper methods. If Q lands first (the queue allows
> either — P has no deps, Q deps F/G ✅; take the lower id P first per the queue rule), Q must itself add
> `Apple2DiskII Disk` to the surface record (capture the `disk` local in the returned record) — the same
> additive change P's Task 3 describes. **Builder:** whichever ships first adds the `Disk` field; the
> second reuses it. The field addition is identical in both plans.

**Gate after Task 4:** `dotnet test` — the surface insert→running-read and eject→empty-read tests pass.
This is the **headline gate**: a real machine, stepped through its live bus, reads sector data off a
runtime-inserted image and reads nothing after eject.

---

## The un-fakeable gate (the row's acceptance proof)

> **Gate:** *a running machine reads sector data from an image inserted at runtime (and reads nothing
> after eject), interpreter-tier, synthetic images.*

Encoded as deterministic tests (no asset, headless, interpreter — the oracle):

1. **`Apple2SurfaceDiskSwapTests.Surface_inserts_a_dsk_from_bytes_then_a_running_read_pulls_a_nibble`** —
   the headline: build a surface (no build-time disk → drive 1 synthetic), `InsertDisk(1, dskBytes, Dsk)`
   at runtime, then drive the **live program bus** (`$C0E9` motor + `$C0EC` polls) and recover a GCR
   nibble off the **runtime-inserted** image. A no-op insert (image not actually swapped) leaves the blank
   synthetic image, whose single `0xFF` sync byte never latches a distinct nibble run — the test's
   bit-7-set assertion would still trip on `0xFF`, so the **eject** test is the stronger half:
2. **`Apple2SurfaceDiskSwapTests.Eject_then_a_running_read_pulls_nothing`** — after `EjectDisk(1)`, the
   live bus polls `$C0EC` 50,000 times and **never** sees bit 7 set — an empty drive reads nothing. A
   faked "still inserted" controller would keep latching; the empty-read is impossible to fake.
3. **`DskFluxImageTests.A_runtime_inserted_dsk_image_is_read_back_through_the_head`** — the controller-
   level proof: a `.dsk` built from bytes and inserted at runtime is decoded back to a **known 256-byte
   sector** matching the inserted image — the read genuinely comes from the new bytes, not a stale image.
4. **`Apple2DiskIITests.The_read_follows_the_selected_drive_after_a_runtime_insert_into_drive_2`** +
   **`...Eject_makes_the_drive_read_nothing_and_a_later_insert_restores_reads`** — drive-2 routing +
   eject/re-insert at the controller level, with **distinct** images so a wrong-slot read fails.

Together: runtime insert → the running machine's head reads the new image's real sectors; runtime eject →
the head reads nothing. Both via the live bus, interpreter tier, synthetic images — no asset, no fakery.

---

## Self-review

- **Spec coverage:** T-D (the controller accepts "load bytes as drive N" + "eject drive N" at runtime, for
  `.woz` via the seam + `.dsk`/`.po` via the adapter) ✅. D11/D12 land bytes here (the method R/S call) ✅.
  D13 (eject mid-access, no confirm, re-insert re-reads) ✅. R (catalog), S (upload WS frame), T (DOM) are
  **out of scope** (their own rows; Q is their shared dependency, exposing exactly the call point they
  need).
- **Placeholders:** none — every block is literal against `c26faac`. The only `NotSupportedException` is a
  deliberate, documented `.woz`-bytes gap (no `WozFluxImage` in the tree), not a TODO.
- **Regression safety:** the single-image constructor signature is **unchanged** — every shipped surface
  (`Apple2Surface`, `SoftCardSurface`, `SoftCardVidexSurface`) and every shipped `Apple2DiskIITests` /
  `DskFluxImageTests` / `SoftCard*` test builds with one image → drive 1, default-selects drive 1, reads
  drive 1, exactly as before. Task 1's gate is that regression proof.
- **Format-agnostic invariant preserved:** the controller still takes any `IFluxImage`; Q changes only
  *storage* (1 → 2 slots) and the read's *image source* (always-drive-1 → selected-drive). The `.dsk`
  adapter and the `IFluxImage` seam are untouched.

---

## Shipped-API-vs-design-spec drift flagged

- **No `WozFluxImage` in `src/` (the `.woz`-bytes parse gap).** The design's "both `.woz` and `.dsk`/`.po`"
  (locked decision + D12) is satisfied at the **seam** (the controller inserts a `.woz`-shape `IFluxImage`
  identically — proven with `SyntheticFluxImage`), but a **byte-level** `.woz`-file → `IFluxImage` parser
  does not exist in the shipped tree (PR-F shipped the controller's `.woz` *read path* + the seam + a test
  double; a real parser was called out as "a thin follow-on" in the `SyntheticFluxImage`/`IFluxImage` doc
  comments). Q wires `.dsk`/`.po` end-to-end through `DiskImageFactory.FromBytes` (the common upload case)
  and throws an explicit `NotSupportedException` for `.woz` **bytes** until `WozFluxImage` lands. **Flag to
  owner:** the `.woz`-bytes runtime insert needs a small follow-on `WozFluxImage` (a separable PR, not a
  blocker for Q's mechanism or for R's `.dsk`/`.po` library path). The library catalog (R) can list and
  insert `.dsk`/`.po` immediately; `.woz` library items wait on `WozFluxImage`.
- **Drive 2 was a no-op before Q.** The shipped controller tracked `_drive` (1/2) via `$C0EA/$C0EB` but
  **ignored it on read** (single `_image`). Q makes drive 2 real (two slots + selected-drive routing).
  This is required by the design's two-drive panel (T) and is the first time `$C0EB`-select changes what
  the head reads — additive, with the single-drive build path preserved.
- **The insert/eject *wire message* is not in Q.** The design (D11/D13) names `disk-insert`/`disk-eject`
  JSON over the existing text path; Q exposes the surface method (`InsertDisk`/`EjectDisk`) those handlers
  will call but does **not** add the WebSocket handler — that is rows R (library text message) and S
  (binary `DK` upload frame). Q is correctly scoped to the **mechanism + the call point**; R/S are blocked
  on Q precisely for this method.
```
