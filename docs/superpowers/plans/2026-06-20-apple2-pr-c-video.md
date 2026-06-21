# Apple ][+ PR-C — `Apple2Video`: the multi-mode display chip (`IDisplayDevice`) reading live main RAM

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Apple2Video` (ADR 0014 Decision 3) — one host-facing chip that reads **live main RAM** for scanout (no VRAM, the `SpectrumUla` pattern), implements `IDisplayDevice`, and renders the ][+'s three modes into RGBA: **text** (40×24, GBASCALC interleave), **lo-res** (40×48), and **hi-res** (280×192 with the **verified** `addr(y)` formula + the bit-7 artifact-color model). The chip reads the shared `Apple2VideoState` (PR-B) the IOU writes, so a `$C057` HIRES access flips the next render with no plumbing. It schedules a ~60 Hz frame tick that raises `FrameReady` (the bare ][+ raises **no** interrupt — no vblank IRQ on the bare machine). The un-fakeable gate proves the address math: the hi-res landmark rows (y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`) and the GBASCALC text row bases — asserted on real RGBA from synthetic RAM, **no ROM**.

**Architecture:** `Apple2Video : IPeripheral, IDisplayDevice` binds `context.Space(AddressSpaceKind.Program)` in `Realize` (reads `$0400–$07FF`/`$0800–$0BFF` text + `$2000–$3FFF`/`$4000–$5FFF` hi-res from the live RAM the guest wrote). `RenderInto(Span<uint>)` switches on the current mode (driven by the IOU's `Apple2VideoState` flags) and walks the **non-linear** Apple address math into RGBA, doing its own palette/glyph lookup so the surface stays a dumb blitter. `Width`/`Height` are mode-dependent (280×192 hi-res; 280×192-equivalent for text/lo-res rendered at the same pixel grid). The chip is `IPeripheral` only so it gets a `Realize` (to bind the bus + schedule the tick) — it does **not** map a page (the IOU owns `$C000`); it is wired into the board as a peripheral whose `Read`/`Write` are never hit (it maps nothing). It carries a built-in fallback glyph set so the text-render gate runs **without** a char-gen ROM (ADR 0014 Decision 8 default); a real char-gen ROM is injected later (PR-H). Artifact color ships as correct mono + basic 4-color (ADR 0014 Decision 8 default); the full NTSC-phase model is a later dial.

**Tech Stack:** C# / .NET 10, `IDisplayDevice` (`Width`/`Height`/`RenderInto(Span<uint>)`/`FrameReady`), the `IPeripheral.Realize` → `context.Space(...)` + `context.Scheduler.ScheduleEvery(...)` pattern (the `SpectrumUla` precedent), the shared `Apple2VideoState` from PR-B, xUnit. **Depends on PR-B** (the `Apple2VideoState` + the board into which the chip is wired). Namespace: `CpuEmulator.Peripherals`.

---

## Recon facts this plan is built on (verified against `main` @ HEAD + PR-B + the research)

1. **`IDisplayDevice`** (`src/CpuEmulator.Core/IDisplayDevice.cs`) — `int Width { get; }`, `int Height { get; }`, `void RenderInto(Span<uint> rgba)` (RGBA8888, row-major, `0xFFrrggbb` per the `DemoFramebuffer`/`SpectrumPalette` convention; a too-small span throws `ArgumentException`), `event Action? FrameReady`. The chip does its own palette/mode lookup.
2. **The `SpectrumUla` precedent** (`src/CpuEmulator.Peripherals/SpectrumUla.cs`) binds `IAddressSpace` for screen reads, schedules a frame tick in `Realize` (`context.Scheduler.ScheduleEvery(TStatesPerFrame, OnFrameTick)`), raises `FrameReady` in the tick, and fills border + walks the non-linear screen address in `RenderInto`. `Apple2Video` is the same shape — minus the IRQ (the bare ][+ has no vblank interrupt).
3. **`Apple2VideoState`** (PR-B) holds `GraphicsOn`, `Mixed`, `Page2`, `HiRes` — the IOU writes them on the `$C05x` any-access toggles; the video chip reads them in `RenderInto`. One shared object, no duplication (ADR 0014 Decision 3).
4. **Hi-res address formula (research §4, verified bijective over y=0..191):**
   `addr(y) = 0x2000 + (y/64)*0x28 + (y%8)*0x400 + ((y/8)&7)*0x80` (page 1; `+0x2000` → `$4000` base for page 2). Landmarks: y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`. The **refuted** swapped-stride variant must NOT be used (it passes only y=0/64/191). Each row is 40 bytes (`$28`); each byte's low 7 bits are 7 pixels, bit 7 = the artifact half-pixel/palette flag.
5. **Text/lo-res address (research §4):** page 1 `$0400–$07FF` (page 2 `$0800`). Each 128-byte block packs 3 rows of 40 chars (120 bytes) + 8 "screen-hole" bytes at offsets `$78–$7F` (left unread). The 24 row bases (GBASCALC): region 0 `$400,$480,$500,$580,$600,$680,$700,$780`; region 1 `$428,$4A8,$528,$5A8,$628,$6A8,$728,$7A8`; region 2 `$450,$4D0,$550,$5D0,$650,$6D0,$750,$7D0`. Row `r` (0..23) → base `0x400 + (r%8)*0x80 + (r/8)*0x28`. (Verify: r=0→`$400`, r=1→`$480`, r=8→`$428`, r=16→`$450`, r=23→`$7D0`.)
6. **Lo-res** is the same 40×24 byte grid as text, but each byte is **two** 4-bit color nibbles stacked vertically (low nibble = top block, high nibble = bottom block) → 40×48 of 16 colors. It uses the **same** GBASCALC row bases.
7. **Text glyphs:** the ][+ char-gen ROM is 2 KiB (256 chars × 8 rows) but its exact contents/legal status is a build-time follow-up (ADR 0014 Decision 7 / research §-residual 2). **Ship a built-in fallback glyph set** (a small embedded ASCII 7×8 font) so the text-render gate runs without the ROM (ADR 0014 Decision 8 default). The chip takes an **optional** char-ROM byte[] (null → fallback font).
8. **The bare ][+ raises no interrupt** — `Apple2Video.Realize` schedules the 60 Hz tick for `FrameReady`/present only; it claims **no** IRQ source (`IrqWiring.None` on the board, PR-B).
9. **`Apple2Video` maps no page.** The IOU owns `$C000`; the video chip is wired into the board purely to receive `Realize` (bus bind + tick). Its `Read`/`Write` are unreachable (no slot maps to it) — they throw/return a harmless default. **PR-C does not modify the board's PeripheralSlot list yet**; it constructs + `Realize`s the chip directly in tests over a built `Machine`'s space (the way the Spectrum tests build a bare ULA). Wiring the video chip into `Apple2Surface` is PR-H's job. This keeps PR-C a pure render-gate PR with no board change.
10. **Timing tier:** `Coarse` — a 60 Hz snapshot is correct for the bare ][+ (no per-scanline reprogramming; ADR 0014 Decision 3). Mid-frame mode-switch `Fine` escalation is a documented later dial.

---

## Conventions to follow

- **Warning-clean.** RGBA is `0xFFrrggbb` (the `SpectrumPalette` convention).
- **Device pattern** mirrors `SpectrumUla` (bind space + schedule tick in `Realize`; render in `RenderInto`).
- **Shared state**: read `Apple2VideoState`, never duplicate the mode flags.
- **TDD per task**, literal code, commit per task.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/Apple2HiResAddress.cs` — the verified `addr(y)` + the GBASCALC text/lo-res row-base helpers (pure functions, separately gated).
- **Create** `src/CpuEmulator.Peripherals/Apple2Palette.cs` — the lo-res 16-color palette + the basic hi-res artifact colors + mono.
- **Create** `src/CpuEmulator.Peripherals/Apple2Font.cs` — the built-in fallback 7×8 glyph set (uppercase + digits + symbols), plus the char-ROM accessor.
- **Create** `src/CpuEmulator.Peripherals/Apple2Video.cs` — `IPeripheral` + `IDisplayDevice`: bind RAM, schedule the 60 Hz tick, `RenderInto` the current mode.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2HiResAddressTests.cs` — the landmark-row + bijection + refuted-variant guard.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2TextAddressTests.cs` — the GBASCALC row-base landmarks.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs` — the un-fakeable render gates (hi-res pixel, text glyph, lo-res block, page-2, FrameReady), synthetic RAM, no ROM.

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **C** to ✅; update the banner.

---

## Task 1: The verified hi-res `addr(y)` + the refuted-variant guard

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2HiResAddress.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2HiResAddressTests.cs`

- [ ] **Step 1: Write the failing test (the landmarks + the full bijection)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2HiResAddressTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2HiResAddressTests
{
    [Theory]
    [InlineData(0, 0x2000)]
    [InlineData(1, 0x2400)]
    [InlineData(8, 0x2080)]
    [InlineData(64, 0x2028)]
    [InlineData(191, 0x3FD0)]
    public void HiRes_row_base_matches_the_verified_landmarks_page1(int y, int expected)
    {
        Assert.Equal((uint)expected, Apple2HiResAddress.RowBase(y, page2: false));
    }

    [Fact]
    public void Page2_is_the_page1_base_plus_0x2000()
    {
        for (int y = 0; y < 192; y++)
            Assert.Equal(Apple2HiResAddress.RowBase(y, page2: false) + 0x2000,
                         Apple2HiResAddress.RowBase(y, page2: true));
    }

    [Fact]
    public void The_192_row_bases_are_all_distinct_within_their_8KiB_page()
    {
        // Bijective over y=0..191: every row maps to a distinct $2000-page base (the address math is
        // a permutation, not a collision — the refuted swapped-stride variant collides).
        var seen = new HashSet<uint>();
        for (int y = 0; y < 192; y++)
            Assert.True(seen.Add(Apple2HiResAddress.RowBase(y, page2: false)),
                $"row {y} collided at ${Apple2HiResAddress.RowBase(y, false):X4}");
        Assert.Equal(192, seen.Count);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2HiResAddressTests"`
Expected: FAIL — `Apple2HiResAddress` does not exist.

- [ ] **Step 3: Create the address helper**

Create `src/CpuEmulator.Peripherals/Apple2HiResAddress.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ video screen-address math (research §4, verified bijective). Hi-res uses the
/// VERIFIED two-level interleave; the swapped-stride variant is REFUTED (it collides everywhere except
/// y=0/64/191) and must never be used. Text/lo-res use the GBASCALC row bases. Pure functions — the
/// un-fakeable address gate exercises them directly, and Apple2Video composes them in RenderInto.</summary>
public static class Apple2HiResAddress
{
    /// <summary>Hi-res scanline (y in 0..191) -> the base address of that row's 40 bytes.
    /// addr(y) = 0x2000 + (y/64)*0x28 + (y%8)*0x400 + ((y/8)&7)*0x80   (page 1; +0x2000 for page 2).
    /// Landmarks: y=0->$2000, y=1->$2400, y=8->$2080, y=64->$2028, y=191->$3FD0.</summary>
    public static uint RowBase(int y, bool page2)
    {
        uint baseAddr = (uint)(0x2000
            + (y / 64) * 0x28
            + (y % 8) * 0x400
            + ((y / 8) & 7) * 0x80);
        return page2 ? baseAddr + 0x2000 : baseAddr;
    }

    /// <summary>Text/lo-res row (r in 0..23) -> the base address of that row's 40 bytes (GBASCALC).
    /// base(r) = 0x400 + (r%8)*0x80 + (r/8)*0x28   (page 1; +0x400 for page 2).
    /// Landmarks: r=0->$400, r=1->$480, r=8->$428, r=16->$450, r=23->$7D0.</summary>
    public static uint TextRowBase(int r, bool page2)
    {
        uint baseAddr = (uint)(0x400 + (r % 8) * 0x80 + (r / 8) * 0x28);
        return page2 ? baseAddr + 0x400 : baseAddr;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2HiResAddressTests"`
Expected: PASS — the landmarks match and all 192 bases are distinct. **This is the hi-res address gate** (the refuted variant would collide → the bijection test fails).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2HiResAddress.cs tests/CpuEmulator.Tests/Apple2/Apple2HiResAddressTests.cs
git commit -m "feat(peripherals): verified Apple ][+ hi-res addr(y) + GBASCALC row bases"
```

---

## Task 2: The GBASCALC text/lo-res row-base gate

**Files:**
- Create: `tests/CpuEmulator.Tests/Apple2/Apple2TextAddressTests.cs`

- [ ] **Step 1: Write the failing/passing test**

Create `tests/CpuEmulator.Tests/Apple2/Apple2TextAddressTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2TextAddressTests
{
    [Theory]
    [InlineData(0, 0x400)]
    [InlineData(1, 0x480)]
    [InlineData(7, 0x780)]   // region 0 last
    [InlineData(8, 0x428)]   // region 1 first
    [InlineData(15, 0x7A8)]  // region 1 last
    [InlineData(16, 0x450)]  // region 2 first
    [InlineData(23, 0x7D0)]  // region 2 last
    public void Text_row_base_matches_the_GBASCALC_landmarks(int r, int expected)
    {
        Assert.Equal((uint)expected, Apple2HiResAddress.TextRowBase(r, page2: false));
    }

    [Fact]
    public void The_24_text_row_bases_are_distinct()
    {
        var seen = new HashSet<uint>();
        for (int r = 0; r < 24; r++)
            Assert.True(seen.Add(Apple2HiResAddress.TextRowBase(r, page2: false)));
        Assert.Equal(24, seen.Count);
    }
```

- [ ] **Step 2: Run it (passes — the helper from Task 1 implements it)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2TextAddressTests"`
Expected: PASS. **This is the text/lo-res address gate.**

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2TextAddressTests.cs
git commit -m "test(apple2): GBASCALC text/lo-res row-base landmarks"
```

---

## Task 3: The palette + the built-in fallback font

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2Palette.cs`
- Create: `src/CpuEmulator.Peripherals/Apple2Font.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs` (palette/font presence asserts)

- [ ] **Step 1: Write the failing test (presence + shape)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs` (the render cases are added in Tasks 4–5; start with the asset shape):

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoTests
{
    [Fact]
    public void LoRes_palette_has_16_entries_opaque()
    {
        Assert.Equal(16, Apple2Palette.LoRes.Length);
        Assert.All(Apple2Palette.LoRes.ToArray(), c => Assert.Equal(0xFF000000u, c & 0xFF000000u));
    }

    [Fact]
    public void Mono_white_and_black_are_defined()
    {
        Assert.Equal(0xFF000000u, Apple2Palette.MonoOff);
        Assert.Equal(0xFFFFFFFFu, Apple2Palette.MonoOn);
    }

    [Fact]
    public void Fallback_font_has_a_glyph_per_byte_of_8_rows()
    {
        // 256 glyphs x 8 rows; glyph 'A' (0x41 & 0x3F screen code mapping aside) has some set bits.
        Assert.Equal(256 * 8, Apple2Font.Fallback.Length);
        // The space-ish glyph (index 0x20) should be blank; an 'A'-ish glyph non-blank.
        int aRowsSet = 0;
        for (int row = 0; row < 8; row++) if (Apple2Font.Fallback[0x41 * 8 + row] != 0) aRowsSet++;
        Assert.True(aRowsSet > 0, "the 'A' glyph should have set pixels");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoTests.LoRes|FullyQualifiedName~Apple2VideoTests.Mono|FullyQualifiedName~Apple2VideoTests.Fallback"`
Expected: FAIL — `Apple2Palette`/`Apple2Font` do not exist.

- [ ] **Step 3: Create the palette**

Create `src/CpuEmulator.Peripherals/Apple2Palette.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>Apple ][+ colours as RGBA8888 (0xFFrrggbb). LoRes is the 16-colour low-res palette. Mono
/// is the hi-res monochrome pair. Artifact (basic 4-colour) ships per ADR 0014 Decision 8's default —
/// correct mono + basic green/purple/blue/orange; the full 12°-phase NTSC model is a later fidelity
/// dial.</summary>
public static class Apple2Palette
{
    public const uint MonoOff = 0xFF000000u; // black
    public const uint MonoOn  = 0xFFFFFFFFu; // white

    /// <summary>The 16 low-res colours (the standard ][+ lo-res palette, RGBA8888). Index = the 4-bit
    /// nibble value. Values are the widely-used canonical approximations.</summary>
    public static readonly uint[] LoRes =
    [
        0xFF000000, // 0 black
        0xFF8A2140, // 1 magenta/deep red
        0xFF3C22A5, // 2 dark blue
        0xFFC847E4, // 3 purple
        0xFF07653E, // 4 dark green
        0xFF7B7B7B, // 5 grey 1
        0xFF308EF3, // 6 medium blue
        0xFFB9A9FD, // 7 light blue
        0xFF4F5101, // 8 brown
        0xFFF25E00, // 9 orange
        0xFFC0C0C0, // 10 grey 2
        0xFFFF8FAF, // 11 pink
        0xFF38CB00, // 12 green
        0xFFD5CF30, // 13 yellow
        0xFF8AF9BC, // 14 aqua
        0xFFFFFFFF, // 15 white
    ];

    /// <summary>Basic hi-res artifact colours (ADR 0014 Decision 8 default): violet/green (bit7 clear)
    /// and blue/orange (bit7 set). Index by [bit7][evenColumn].</summary>
    public static readonly uint[] Artifact =
    [
        0xFFC847E4, // bit7=0, even -> violet
        0xFF38CB00, // bit7=0, odd  -> green
        0xFF308EF3, // bit7=1, even -> blue
        0xFFF25E00, // bit7=1, odd  -> orange
    ];
}
```

- [ ] **Step 4: Create the fallback font**

Create `src/CpuEmulator.Peripherals/Apple2Font.cs`. A full 256×8 bitmap is large; ship a compact generator that produces a legible 7×8 uppercase/digit/symbol set and blanks the rest (the real char-gen ROM replaces this in PR-H). The exact glyph shapes are NOT load-bearing for the gate (the gate asserts "a non-blank glyph renders ON pixels at the right cell"), so a simple deterministic font suffices:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>A built-in fallback glyph set so the text-render gate runs WITHOUT the (Apple-copyright,
/// build-time-sourced) char-gen ROM — ADR 0014 Decision 8's default. 256 glyphs x 8 rows; each byte is
/// one row, bit 6..0 = the 7 horizontal pixels (bit 6 leftmost). PR-H injects the real 2 KiB char ROM
/// (same 256x8 layout) when fetched; until then this legible 7x8 set drives the render gate. The exact
/// glyph shapes are not load-bearing (the gate asserts cell placement + on/off pixels, not letterforms).</summary>
public static class Apple2Font
{
    /// <summary>256 glyphs * 8 rows = 2048 bytes. Built once at type load.</summary>
    public static readonly byte[] Fallback = Build();

    private static byte[] Build()
    {
        var f = new byte[256 * 8];
        // A minimal vector: uppercase A-Z (0x41-0x5A), digits 0-9 (0x30-0x39), and a few symbols get a
        // simple filled-box-with-hole glyph so they are visibly non-blank and distinct from space; the
        // rest stay blank. This is intentionally crude — the real ROM lands in PR-H.
        for (int code = 0; code < 256; code++)
        {
            bool printable = (code >= 0x20 && code <= 0x7E);
            if (!printable || code == 0x20) continue; // space + non-printables stay blank
            // A 5x7 outline box inside the 7x8 cell: rows 0..6 use bits 5..1.
            for (int row = 0; row < 7; row++)
            {
                byte bits = row is 0 or 6
                    ? (byte)0b0111110          // top/bottom edge
                    : (byte)0b0100010;         // left/right edges
                // Add a code-dependent interior pixel so glyphs are not all identical (distinguishes
                // adjacent codes in a coarse but deterministic way).
                if (row == 3 && (code & 1) != 0) bits |= 0b0001000;
                f[code * 8 + row] = bits;
            }
        }
        return f;
    }
}
```

- [ ] **Step 5: Run the asset tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoTests.LoRes|FullyQualifiedName~Apple2VideoTests.Mono|FullyQualifiedName~Apple2VideoTests.Fallback"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2Palette.cs src/CpuEmulator.Peripherals/Apple2Font.cs tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs
git commit -m "feat(peripherals): Apple ][+ lo-res palette + mono/artifact colours + fallback font"
```

---

## Task 4: `Apple2Video` — bind RAM, schedule the tick, hi-res render

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2Video.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs` (add the hi-res render gate)

- [ ] **Step 1: Write the failing hi-res render gate**

Append to `Apple2VideoTests` (add `using CpuEmulator.Core;` is already present):

```csharp
    private const int HiResW = 280;
    private const int HiResH = 192;

    /// <summary>A bare video chip over a 16-bit RAM space the test writes screen bytes into. The chip
    /// is constructed + bound directly (the Spectrum-test pattern: no full board needed for the render
    /// gate). HiRes mode is selected on the shared state.</summary>
    private static (Apple2Video video, AddressSpace ram, Apple2VideoState state) BuildHiRes()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true); // whole 64K as RAM for the test
        var state = new Apple2VideoState { GraphicsOn = true, HiRes = true, Mixed = false, Page2 = false };
        var video = new Apple2Video(space, state);
        return (video, space, state);
    }

    [Fact]
    public void HiRes_render_size_is_280x192()
    {
        var (video, _, _) = BuildHiRes();
        Assert.Equal(HiResW, video.Width);
        Assert.Equal(HiResH, video.Height);
    }

    [Fact]
    public void A_set_hires_bit_lights_its_pixel_using_the_verified_addr()
    {
        var (video, ram, _) = BuildHiRes();
        // Row y=64 starts at $2028 (a landmark that exercises the (y/64) third-region stride). Set
        // bit 0 of the first byte -> the leftmost pixel of that row is ON.
        uint rowBase = Apple2HiResAddress.RowBase(64, page2: false);  // $2028
        ram.Write8(rowBase, 0x01);   // low bit = leftmost of the 7 pixels in this byte

        var rgba = new uint[HiResW * HiResH];
        video.RenderInto(rgba);

        // Pixel (x=0, y=64) must be ON (not black); a neighbour with no bit set is OFF.
        Assert.NotEqual(Apple2Palette.MonoOff, rgba[64 * HiResW + 0]);
        Assert.Equal(Apple2Palette.MonoOff, rgba[64 * HiResW + 1]);
    }

    [Fact]
    public void Page2_reads_the_4000_region()
    {
        var (video, ram, state) = BuildHiRes();
        state.Page2 = true;
        uint rowBase = Apple2HiResAddress.RowBase(0, page2: true);    // $4000
        ram.Write8(rowBase, 0x01);

        var rgba = new uint[HiResW * HiResH];
        video.RenderInto(rgba);
        Assert.NotEqual(Apple2Palette.MonoOff, rgba[0 * HiResW + 0]); // top-left lit from page 2
    }

    [Fact]
    public void Render_raises_FrameReady_on_the_scheduled_tick()
    {
        // Realize schedules the 60 Hz tick; firing it raises FrameReady. We assert the event wiring via
        // a built Machine so the scheduler actually runs (see the Apple2Board integration in PR-B tests).
        // Here we just confirm the event is invokable and the render does not throw on a too-small span.
        var (video, _, _) = BuildHiRes();
        var rgba = new uint[HiResW * HiResH];
        bool raised = false;
        video.FrameReady += () => raised = true;
        video.RaiseFrameForTest();   // test-only hook standing in for the scheduler tick
        Assert.True(raised);

        Assert.Throws<ArgumentException>(() => video.RenderInto(new uint[10]));
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoTests.HiRes|FullyQualifiedName~Apple2VideoTests.A_set|FullyQualifiedName~Apple2VideoTests.Page2|FullyQualifiedName~Apple2VideoTests.Render_raises"`
Expected: FAIL — `Apple2Video` does not exist.

- [ ] **Step 3: Create `Apple2Video` (hi-res first)**

Create `src/CpuEmulator.Peripherals/Apple2Video.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ video chip (ADR 0014 Decision 3): a host-facing IDisplayDevice that reads
/// LIVE main RAM for scanout (no VRAM — the SpectrumUla pattern) and renders the current mode (driven
/// by the shared Apple2VideoState the IOU writes) into RGBA. It is an IPeripheral only to receive
/// Realize (bind the program bus + schedule the ~60 Hz present tick); it maps no page (the IOU owns
/// $C000), so its Read/Write are never reached. The bare ][+ raises NO interrupt — the tick is the
/// host-present trigger only. Ships correct mono + basic-artifact hi-res, the lo-res 16-colour palette,
/// and a built-in fallback font (the real char-gen ROM is injected in PR-H). Timing tier: Coarse.</summary>
public sealed class Apple2Video : IPeripheral, IDisplayDevice
{
    public const int Width280 = 280;
    public const int Height192 = 192;

    private const long CyclesPerFrame = 17030; // ~1.0205 MHz / 60 Hz (the present cadence; Coarse)

    private readonly IAddressSpace _ram;
    private readonly Apple2VideoState _state;
    private readonly byte[] _charRom;   // 256x8; the fallback font unless a real ROM is injected

    public string Name => "apple2video";
    public int Width => Width280;
    public int Height => Height192;
    public event Action? FrameReady;

    /// <param name="ram">The program bus — the chip reads $0400/$2000 etc. live.</param>
    /// <param name="state">The shared mode/page state the IOU writes.</param>
    /// <param name="charRom">Optional 256x8 char-gen ROM; null uses the built-in fallback font.</param>
    public Apple2Video(IAddressSpace ram, Apple2VideoState state, byte[]? charRom = null)
    {
        ArgumentNullException.ThrowIfNull(ram);
        ArgumentNullException.ThrowIfNull(state);
        _ram = ram;
        _state = state;
        _charRom = charRom ?? Apple2Font.Fallback;
        if (_charRom.Length != 256 * 8)
            throw new ArgumentException("char ROM must be 256x8 = 2048 bytes.", nameof(charRom));
    }

    public void Realize(IMachineContext context)
    {
        // Schedule the present tick only; no IRQ on the bare ][+ (IrqWiring.None).
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => FrameReady?.Invoke());
    }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test-only: stand in for the scheduler tick so a unit test can assert FrameReady without
    /// building a full Machine.</summary>
    internal void RaiseFrameForTest() => FrameReady?.Invoke();

    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < Width280 * Height192)
            throw new ArgumentException(
                $"Destination needs {Width280 * Height192} pixels; got {rgba.Length}.", nameof(rgba));

        if (_state.GraphicsOn && _state.HiRes)
            RenderHiRes(rgba);
        else if (_state.GraphicsOn) // lo-res
            RenderLoRes(rgba);
        else
            RenderText(rgba);
    }

    private void RenderHiRes(Span<uint> rgba)
    {
        for (int y = 0; y < Height192; y++)
        {
            uint rowBase = Apple2HiResAddress.RowBase(y, _state.Page2);
            int destRow = y * Width280;
            int x = 0;
            for (int b = 0; b < 40; b++)        // 40 bytes per row, 7 pixels each
            {
                byte data = _ram.Read8(rowBase + (uint)b);
                for (int bit = 0; bit < 7 && x < Width280; bit++, x++)
                {
                    bool on = (data & (1 << bit)) != 0; // bit 0 = leftmost (the dot order)
                    rgba[destRow + x] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                }
            }
        }
    }

    private void RenderLoRes(Span<uint> rgba)
    {
        // 40x24 byte grid; each byte = two stacked 4-bit colour blocks (low nibble top, high nibble
        // bottom). Rendered onto the 280x192 grid: each lo-res cell is 7px wide x 4px tall (48 rows).
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, _state.Page2);
            for (int c = 0; c < 40; c++)
            {
                byte data = _ram.Read8(rowBase + (uint)c);
                uint top = Apple2Palette.LoRes[data & 0x0F];
                uint bottom = Apple2Palette.LoRes[(data >> 4) & 0x0F];
                FillCell(rgba, c * 7, r * 8, 7, 4, top);
                FillCell(rgba, c * 7, r * 8 + 4, 7, 4, bottom);
            }
        }
    }

    private void RenderText(Span<uint> rgba)
    {
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, _state.Page2);
            for (int c = 0; c < 40; c++)
            {
                byte ch = _ram.Read8(rowBase + (uint)c);
                int glyph = ch & 0x7F;          // strip the inverse/flash high bits (basic render)
                for (int gy = 0; gy < 8; gy++)
                {
                    byte rowBits = _charRom[glyph * 8 + gy];
                    for (int gx = 0; gx < 7; gx++)
                    {
                        bool on = (rowBits & (0x40 >> gx)) != 0; // bit 6 = leftmost
                        int px = c * 7 + gx, py = r * 8 + gy;
                        rgba[py * Width280 + px] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                    }
                }
            }
        }
    }

    private static void FillCell(Span<uint> rgba, int x0, int y0, int w, int h, uint color)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
            {
                int px = x0 + dx, py = y0 + dy;
                if (px < Width280 && py < Height192)
                    rgba[py * Width280 + px] = color;
            }
    }
}
```

- [ ] **Step 4: Run the hi-res gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoTests.HiRes|FullyQualifiedName~Apple2VideoTests.A_set|FullyQualifiedName~Apple2VideoTests.Page2|FullyQualifiedName~Apple2VideoTests.Render_raises"`
Expected: PASS. **This is the hi-res render gate** — a bit set at the verified `addr(y)` lights exactly its pixel, page 2 reads `$4000`, FrameReady fires, a too-small span throws.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2Video.cs tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs
git commit -m "feat(peripherals): Apple2Video (IDisplayDevice) — bind RAM, 60Hz tick, hi-res render"
```

---

## Task 5: The text + lo-res render gates

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs` (add text + lo-res cases)

- [ ] **Step 1: Write the failing/passing text + lo-res gates**

Append to `Apple2VideoTests`:

```csharp
    private static (Apple2Video video, AddressSpace ram, Apple2VideoState state) BuildText(bool loRes)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var state = new Apple2VideoState { GraphicsOn = loRes, HiRes = false };
        return (new Apple2Video(space, state), space, state);
    }

    [Fact]
    public void Text_renders_a_glyph_at_its_cell_via_GBASCALC()
    {
        var (video, ram, _) = BuildText(loRes: false);
        // Put a printable glyph at row 8, col 0 -> base $428 (a GBASCALC landmark).
        uint cellBase = Apple2HiResAddress.TextRowBase(8, page2: false); // $428
        ram.Write8(cellBase, (byte)'A');

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        // The 'A' glyph cell occupies x in [0,7), y in [64,72). At least one pixel there is ON.
        bool anyOn = false;
        for (int gy = 0; gy < 8; gy++)
            for (int gx = 0; gx < 7; gx++)
                if (rgba[(64 + gy) * Apple2Video.Width280 + gx] == Apple2Palette.MonoOn) anyOn = true;
        Assert.True(anyOn, "the 'A' glyph should light pixels in its cell at row 8 / $428");
    }

    [Fact]
    public void LoRes_paints_the_two_stacked_colour_blocks_of_a_byte()
    {
        var (video, ram, _) = BuildText(loRes: true);
        uint cellBase = Apple2HiResAddress.TextRowBase(0, page2: false); // $400
        // Low nibble = 15 (white) top; high nibble = 1 (magenta) bottom.
        ram.Write8(cellBase, (byte)((1 << 4) | 15));

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        Assert.Equal(Apple2Palette.LoRes[15], rgba[0 * Apple2Video.Width280 + 0]);     // top block
        Assert.Equal(Apple2Palette.LoRes[1], rgba[4 * Apple2Video.Width280 + 0]);      // bottom block
    }
```

- [ ] **Step 2: Run them to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoTests.Text_renders|FullyQualifiedName~Apple2VideoTests.LoRes_paints"`
Expected: PASS. **This is the text + lo-res render gate** — a glyph lands at its GBASCALC cell; a lo-res byte paints its two stacked colour blocks. All gates run on synthetic RAM, **no ROM**.

- [ ] **Step 3: Run the full Apple2 render suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Expected: PASS — PR-B's IOU/board/state gates + PR-C's address/palette/font/render gates all green.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2VideoTests.cs
git commit -m "test(apple2): text (GBASCALC) + lo-res stacked-block render gates"
```

---

## Task 6: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **C** status to ✅, and update the **Last updated** banner with the date + "PR-C merged".

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-C (video) done"
```

---

## Done-when

- `Apple2Video` implements `IDisplayDevice`, reads live main RAM (no VRAM), and renders text / lo-res / hi-res from the shared `Apple2VideoState` the IOU writes.
- The hi-res render uses the **verified** `addr(y)` formula (landmarks gated; the refuted swapped-stride variant is excluded by the bijection test); page 2 reads `$4000`; the text render uses the GBASCALC row bases; lo-res paints the two stacked colour blocks.
- The chip carries a built-in fallback font so the text gate runs **without** a char-gen ROM (the real ROM injects in PR-H); it ships correct mono + basic artifact + the lo-res palette (the full NTSC-phase model is a later dial).
- All render gates run on synthetic RAM, **no ROM** — the un-fakeable Spectrum-style render posture.
- Queue row **C** is ✅; PR-D (keyboard/speaker `IAudioSink`) and PR-H (the surface + ROM-boot gate, which wires `Apple2Video` into `Apple2Surface` and injects the real char ROM) build on this.

---

## Notes for the PR-H planner (deferred — when PR-H reaches the front)

- `Apple2Video` is constructed + `Realize`d directly in PR-C's tests over a built space; **PR-H wires it into the surface** (the same way `SpectrumSurface` hands the ULA to `MachineHost` as the `IDisplayDevice`). The video chip is added to `Apple2Board`'s peripheral list (or constructed beside it) so its `Realize` binds the live program space — mirror `SpectrumMachine.Build`'s "same instance mapped + handed to the surface" wiring.
- PR-H's `get-apple2-roms` fetches the **char-gen ROM**; inject it into `Apple2Video`'s `charRom` arg (replacing the fallback font) when present, skip-with-note when absent. The ROM-boot gate (Applesoft `]`) asserts real glyphs only when the char ROM is cached.
