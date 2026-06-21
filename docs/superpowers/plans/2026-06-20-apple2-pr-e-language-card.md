# Apple ][+ PR-E — Language Card mapper (`$C080–$C08F`): the first real `AddressSpace.Remap` consumer

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Apple2LanguageCard : IPeripheral` (ADR 0014 Decision 4) — a code **mapper** that owns the `$C080–$C08F` soft switches (via the IOU's delegation) and run-time bank-switches `$D000–$FFFF` between the system ROM and 16 KiB of card RAM by calling the **shipped** `IAddressSpace.Remap` (PR-A). It implements the exact ][+ rules (research §7): bit 0 selects bank 1 vs bank 2 at `$D000`; the read-ROM/read-RAM + write-enable bits; and the **two-consecutive-reads** pre-write flip-flop (a single read does not write-enable LC RAM). **This is the first shipping consumer of the `Remap`/`RemapPeripheral` API PR-A landed** — its literal code calls the actually-shipped signatures. The un-fakeable interpreter-tier gate: two consecutive odd-`$C08x` reads write-enable `$D000` RAM (one read does not); bank-1/bank-2 + read-ROM/read-RAM select the right backing; and a real 6502 program **runs code out of LC RAM** on the interpreter (the oracle).

**Architecture:** The LC banks `$D000–$FFFF` (12 KiB) as two regions that switch **independently** (research §7):

- **`$D000–$DFFF`** (4 KiB) has **two** RAM banks (bank 1 / bank 2), selected by the `$C08x` offset.
- **`$E000–$FFFF`** (8 KiB) is a **single shared** RAM region (no banking — both banks see the same `$E000`).

So the LC holds **three** RAM arrays: `bankD1` (4 KiB), `bankD2` (4 KiB), `sharedE` (8 KiB) — plus a reference to the **system ROM image** (the `$D000–$FFFF` 12 KiB `byte[]` the board mapped). On each `$C08x` access it computes the read source (ROM or RAM) + the write-enable + the active `$D000` bank and calls `Remap` to re-point the pages:

- Read-ROM: `Remap($D000, romSlice_D000, writable: false)` + `Remap($E000, romSlice_E000, writable: false)` — but reads from ROM while writes (when armed) still go to RAM is the subtle ][+ rule (see below). On the ][+, the LC's "read ROM, write RAM" state means **reads** resolve to ROM and **writes** resolve to LC RAM. The shipped page table is single-backing-per-page (a page is either ROM or RAM), so PR-E models the dominant, correct-for-DOS/ProDOS/CP/M cases with a **read-select** drive: the page's backing + writability is `(readSource, writeEnabled)` collapsed to the page's single backing — see Task 2's truth table + the explicit note on the "read-ROM/write-RAM" split.
- Read-RAM: `Remap($D000, bankD1 | bankD2, writable: writeEnabled)` + `Remap($E000, sharedE, writable: writeEnabled)`.

`Remap` fires the JIT `IMapInvalidationListener` (PR-A) so the JIT re-classifies + evicts the banked pages — making this also the first device to exercise the JIT remap path through a real consumer (the JIT-tier gate is the separately-gated follow-on; the interpreter is correct with no listener, re-reading the live page table every access).

**IOU delegation:** the shipped IOU (PR-B) has `default: break;` for the `$C080–$C08F` offsets. PR-E adds a delegate seam: the IOU forwards a `$C08x` access (offsets `0x80–0x8F`) to an optional `Apple2LanguageCard` it holds, calling the card's `Access(offset, isRead)`. The card returns a bus value for the read (floating bus / the data byte) — but the **side effect (the remap)** is what matters. This keeps the IOU the single page owner (it owns `$C000–$C0FF`, which includes `$C08x`) while the LC owns the bank logic.

**Tech Stack:** C# / .NET 10, the shipped `IAddressSpace.Remap(uint start, byte[] backing, bool writable)` (PR-A), `IPeripheral.Realize` → `context.Space(AddressSpaceKind.Program)`, the IOU delegate seam, xUnit on **both tiers** for the run-code-out-of-LC-RAM gate. **Depends on PR-A** (the `Remap` seam) **and PR-B** (the IOU + board). Namespace: `CpuEmulator.Peripherals` (the card) + a tiny `Apple2Iou` change.

---

## Recon facts this plan is built on (verified against `main` @ `97a44d5` — the ACTUALLY-SHIPPED PR-A/B API)

1. **`IAddressSpace.Remap` is shipped exactly as the ADR specified** (`src/CpuEmulator.Core/AddressSpace.cs:100` + `IAddressSpace.cs:33`):
   ```csharp
   public void Remap(uint start, byte[] backing, bool writable);          // re-point a range to RAM/ROM
   public void RemapPeripheral(uint start, uint length, IPeripheral peripheral); // re-point to MMIO
   ```
   `Remap` re-points an **already-mapped**, page-aligned range in place: per page it sets `Backing = backing`, `BackingOffset = i << 8`, `Writable = writable`, and **clears `Handler`** (memory wins). It calls `ValidateRange` (length must be a positive multiple of 256 and start page-aligned) then `FireRemap(firstPage, pageCount)`. **No "must be unmapped" check** (unlike `MapMemory`) — that is the point. **NO DRIFT from the ADR:** the signature, the in-place semantics, and the listener fire all match ADR 0014 Decision 4 / ADR 0009 §3.2 verbatim.
   - **Backing-offset note (load-bearing for the LC's array layout):** `BackingOffset = i << 8` indexes from the **start of the passed `backing` array**. So to point `$D000` (4 KiB = 16 pages) at a bank, pass a `byte[]` whose element 0 is that bank's first byte — i.e. a **standalone 4 KiB array per bank**, not a slice/offset into a larger array. This is why the LC holds three separate arrays (`bankD1`, `bankD2`, `sharedE`) and three ROM-slice arrays, each starting at index 0.
2. **`Remap` is on `IAddressSpace`** (the owner decision — ADR 0014 OQ3 ✅). `context.Space(AddressSpaceKind.Program)` returns `IAddressSpace`; the concrete `AddressSpace` implements `Remap` (the interface default throws `NotSupportedException` — the LC always gets the concrete bus from a real `Machine`, so it works). **No cast needed.**
3. **The IOU owns `$C000–$C0FF`** (`Apple2Board.cs`: `PeripheralSlot("iou", iou, 0xC000, 0x0100)`), which **includes** `$C080–$C08F`. The LC cannot map its own slot in that page (256-byte granularity — `EnsureRangeUnmapped` throws). So the IOU must **delegate** `$C08x` to the card. The IOU's `ApplyAnyAccessSideEffect` currently has `// $C080-$C08F (Language Card) ... are delegated in PR-E` with `default: break;` — PR-E fills that in.
4. **The system ROM is one 12 KiB image** mapped read-only at `$D000` (`Apple2Board.cs`: `new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, systemRom)`). The board passes the **same `systemRom` byte[]** into both `Apple2Board.Spec(...)` and (PR-E) the LC, so the card can `Remap` `$D000`/`$E000` back to ROM slices. **The LC needs ROM-slice arrays** (a 4 KiB `$D000` slice + an 8 KiB `$E000` slice) because `Remap`'s backing is index-0-based (fact 1's note). The card builds these slices from the ROM image in its constructor.
5. **`Machine` realizes peripherals over the built program space** (`Machine.cs:49-52`): maps each slot, then calls `Realize(this)` in registration order. The LC's `Realize` captures `context.Space(AddressSpaceKind.Program)` (the live bus it remaps) — the `Apple2Video`/`SpectrumUla` precedent.
6. **The two-consecutive-reads pre-write flip-flop** (research §7, ADR 0014 Decision 4): write-enabling LC RAM requires **two consecutive reads** of an odd `$C08x`. A single read does not arm it; a write access **resets** the count (only reads arm). The card holds an arm counter incremented on a qualifying **read** and cleared on any non-qualifying access.
7. **The `$C08x` decode (the standard LC truth table, research §7 / Sather *Understanding the Apple IIe*), as PR-E implements it (and as Task 2's per-offset tests pin it):**
   - **Bank select = bit 3** (the `$C088` line): `(offset & 0x08) == 0` → **bank 1** (`$C080–$C087`), else **bank 2** (`$C088–$C08F`). So `$C083` → bank 1, `$C08B` → bank 2.
   - **Read source = `(offset & 0x03)`:** read **RAM** when the low two bits are `00` or `11` (`$C080`/`$C083`); read **ROM** when `01` or `10` (`$C081`/`$C082`).
   - **Write-enable arm = bit 0** (odd address) on a **read**: an odd-`$C08x` read increments the pre-write counter; two consecutive arm-reads write-enable LC RAM; any non-qualifying access (a write, or an even address) resets it (fact 6).
   - Worked landmarks the gate asserts: `$C083` = bank 1, read-RAM, arm; `$C081` = bank 1, read-ROM, arm; `$C08B` = bank 2, read-RAM, arm; `$C080` = bank 1, read-RAM, no-arm. **Task 2 pins each via a per-offset test, so the implementation is gated, not guessed.**
8. **Both tiers run the run-code-out-of-LC-RAM gate.** `BoardMachineFactory.Build(spec, ExecutionTier.Interpreter | ExecutionTier.Jit)` — the interpreter is the oracle (correct with no listener); the JIT must re-classify + evict the remapped pages (PR-A's listener) so the new bank's code runs. The interpreter gate is the row-E deliverable; the JIT gate is run in the same test parameterised over the tier (it exercises PR-A's `OnRemap` → `Fastmem.Reclassify` + `BlockCache.InvalidatePages` through a real consumer).
9. **Presence detection (48K↔64K)** is a write-test to `$D000` LC RAM (research §7): once the LC RAM is mapped writable, a write to `$D000` then a read-back returns the written byte (ROM would not). No special handling — the remap making `$D000` writable is sufficient. The gate asserts this directly.

---

## Conventions to follow

- **Call the SHIPPED `Remap`** (`IAddressSpace.Remap(start, backing, writable)`) — never a guessed signature. Backing arrays are index-0-based per the fact-1 note.
- **The IOU stays the single page owner**; the LC is delegated `$C08x`. No new slot in the `$C000` page.
- **TDD per task**, literal code, commit per task. Warning-clean.
- **Gate on both tiers** for the run-code gate (interpreter is the oracle; JIT exercises PR-A's listener).
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/Apple2LanguageCard.cs` — `IPeripheral`: the `$C08x` decode + the pre-write flip-flop + the `Remap` drive; captures the program space in `Realize`.
- **Modify** `src/CpuEmulator.Peripherals/Apple2Iou.cs` — add an optional `Apple2LanguageCard` reference + delegate `$C080–$C08F` (offsets `0x80–0x8F`) to it from `ApplyAnyAccessSideEffect` (any access) + return its bus value from `BusValue`.

### `CpuEmulator.Machines`
- **Modify** `src/CpuEmulator.Machines/Apple2Board.cs` — an overload / optional param that wires the LC: the board passes the same `systemRom` to the LC, attaches the LC to the IOU, and adds the LC as a peripheral (so its `Realize` runs). The base no-LC overload stays (PR-B's tests use it).

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs` — the `$C08x` decode truth table, the two-consecutive-reads arming, the bank/read-source selection (asserting the `Remap`ped backing via bus reads), and the **both-tier run-code-out-of-LC-RAM** gate.

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **E** to ✅; update the banner.

---

## Task 1: The IOU `$C08x` delegate seam + the LC skeleton (`Realize` captures the bus)

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2LanguageCard.cs`
- Modify: `src/CpuEmulator.Peripherals/Apple2Iou.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs`

- [ ] **Step 1: Write the failing test (the IOU forwards `$C08x` to the LC; the LC sees the access)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs` (the first case proves the delegate seam; the bank/arming cases follow in Tasks 2–3):

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2LanguageCardTests
{
    // A 12 KiB system ROM with a recognisable byte at $D000 and a reset-vector NOP loop.
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x0000] = 0xA5;                                         // a MARKER byte at $D000 (ROM)
        rom[0x1000] = 0x5C;                                         // a marker at $E000 (ROM)
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;                     // reset -> $D000
        return rom;
    }

    // Build a real ][+ board WITH the Language Card wired (the PR-E overload).
    private static (Machine machine, IAddressSpace bus, Apple2LanguageCard lc) BuildWithLc(
        ExecutionTier tier = ExecutionTier.Interpreter)
    {
        byte[] rom = SystemRom();
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(rom);
        var iou = new Apple2Iou(state, lc);                        // PR-E: the IOU holds the LC
        BoardSpec spec = Apple2Board.SpecWithLanguageCard(rom, iou, lc);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        return (machine, machine.Space(AddressSpaceKind.Program), lc);
    }

    [Fact]
    public void At_reset_D000_reads_the_system_ROM()
    {
        var (_, bus, _) = BuildWithLc();
        Assert.Equal(0xA5, bus.Read8(0xD000));   // power-on: read-ROM
        Assert.Equal(0x5C, bus.Read8(0xE000));
    }

    [Fact]
    public void A_C08x_access_reaches_the_LC_through_the_IOU()
    {
        var (_, bus, lc) = BuildWithLc();
        long before = lc.AccessCount;            // a test-only counter on the LC
        _ = bus.Read8(0xC080);                   // a bus read of $C080 must route IOU -> LC
        Assert.Equal(before + 1, lc.AccessCount);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.At_reset|FullyQualifiedName~Apple2LanguageCardTests.A_C08x"`
Expected: FAIL — `Apple2LanguageCard`, the `Apple2Iou(state, lc)` ctor, and `Apple2Board.SpecWithLanguageCard` do not exist.

- [ ] **Step 3: Create the LC skeleton**

Create `src/CpuEmulator.Peripherals/Apple2LanguageCard.cs` (skeleton: capture the bus, count accesses, the array layout; the decode + Remap drive land in Task 2):

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ Language Card (ADR 0014 Decision 4): a code mapper over $C080-$C08F that
/// run-time bank-switches $D000-$FFFF between the system ROM and 16 KiB of card RAM by calling the
/// shipped IAddressSpace.Remap (PR-A) — the FIRST real consumer of that bank-switch primitive. The ][+
/// layout (research §7): $D000-$DFFF (4 KiB) has two RAM banks (bank 1 / bank 2); $E000-$FFFF (8 KiB)
/// is a single shared RAM region. Write-enabling LC RAM requires TWO CONSECUTIVE READS of an odd $C08x
/// (the 74LS175 pre-write count flip-flop) — a single read does not arm it. The card is delegated
/// $C08x by the IOU (which owns the $C000 page); it captures the program bus in Realize and remaps on
/// each access. Remap fires PR-A's JIT invalidation listener, so a remapped CODE page runs the new
/// bank (the LC commonly runs DOS/ProDOS/CP/M out of the banked RAM).</summary>
public sealed class Apple2LanguageCard : IPeripheral
{
    private const uint DBank = 0xD000;   // the banked 4 KiB region
    private const uint EShared = 0xE000; // the shared 8 KiB region
    private const int DBankLen = 0x1000; // 4 KiB
    private const int ESharedLen = 0x2000; // 8 KiB

    // The 16 KiB of card RAM as three index-0-based arrays (Remap backing must start at index 0).
    private readonly byte[] _bankD1 = new byte[DBankLen];
    private readonly byte[] _bankD2 = new byte[DBankLen];
    private readonly byte[] _sharedE = new byte[ESharedLen];

    // The system-ROM slices the card remaps $D000/$E000 back to (index-0-based copies of the image).
    private readonly byte[] _romD;   // $D000-$DFFF slice (4 KiB)
    private readonly byte[] _romE;   // $E000-$FFFF slice (8 KiB)

    private IAddressSpace _bus = default!;  // the live program bus, captured in Realize

    // Decoded LC state (power-on = read-ROM, write-protected, bank 1).
    private bool _readRam;        // false => read ROM, true => read LC RAM
    private bool _writeEnabled;   // LC RAM writable (armed by two consecutive odd-$C08x reads)
    private int _bank = 1;        // 1 or 2 (the $D000 bank)
    private int _armCount;        // consecutive-qualifying-read counter (0,1 -> 2 arms write)

    /// <summary>Test-only: total $C08x accesses seen (proves the IOU delegate seam).</summary>
    public long AccessCount { get; private set; }

    /// <param name="systemRom">The same 12 KiB $D000-$FFFF image the board maps as ROM; the card
    /// slices it for the read-ROM remaps.</param>
    public Apple2LanguageCard(byte[] systemRom)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        if (systemRom.Length != 0x3000)
            throw new ArgumentException("system ROM must be 12 KiB ($D000-$FFFF).", nameof(systemRom));
        _romD = systemRom[0x0000..0x1000];   // $D000-$DFFF
        _romE = systemRom[0x1000..0x3000];   // $E000-$FFFF
    }

    public string Name => "apple2lc";

    public void Realize(IMachineContext context)
    {
        _bus = context.Space(AddressSpaceKind.Program); // the live bus we Remap (the SpectrumUla precedent)
        // Power-on state = read-ROM: the board already mapped $D000-$FFFF to ROM, so no remap needed yet.
    }

    // The card maps no page of its own (the IOU owns $C000); these are unreachable.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Called by the IOU for every $C080-$C08F access (offset 0x80-0x8F, isRead = it was a
    /// Read). The DECODE + Remap land in Task 2; for now just count + return a floating-bus 0.</summary>
    public byte Access(byte offset, bool isRead)
    {
        AccessCount++;
        // (Task 2 fills in the decode + the Remap drive here.)
        return 0x00;
    }
}
```

- [ ] **Step 4: Add the IOU delegate seam**

Modify `src/CpuEmulator.Peripherals/Apple2Iou.cs`. Add an optional LC field, a second ctor, the `$C08x` forward in `ApplyAnyAccessSideEffect`, and the LC bus value in `BusValue`:

```csharp
    private readonly Apple2VideoState _state;
    private readonly Apple2LanguageCard? _lc;   // PR-E: $C080-$C08F delegate (null on the bare board)

    public Apple2Iou(Apple2VideoState state) : this(state, null) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _lc = lc;
    }
```

In `ApplyAnyAccessSideEffect(uint offset)`, replace the `// $C080-$C08F ... delegated in PR-E` comment + the `default: break;` so a `$C08x` access forwards to the LC (any access — read OR write — drives the flip-flop; the LC's `Access` takes `isRead` so it can arm only on reads):

```csharp
            // --- Language Card $C080-$C08F (delegated to the LC mapper; any access) ---
            case >= 0x80 and <= 0x8F:
                _lc?.Access(o, isRead);   // isRead threaded from Read/Write (see below)
                break;

            default: break;
```

`ApplyAnyAccessSideEffect` currently takes only `offset`. Thread an `isRead` flag so the LC can distinguish reads (which arm the pre-write flip-flop) from writes (which reset it). Update the two callers:

```csharp
    public uint Read(uint offset, AccessWidth width)
    {
        ApplyAnyAccessSideEffect(offset, isRead: true);
        return BusValue(offset);
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        ApplyAnyAccessSideEffect(offset, isRead: false);
    }

    private void ApplyAnyAccessSideEffect(uint offset, bool isRead)
    {
        byte o = (byte)offset;
        switch (o)
        {
            // ... the existing $C050-$C057 / $C010 / $C030 cases unchanged ...
            case >= 0x80 and <= 0x8F:
                _lc?.Access(o, isRead);
                break;
            default: break;
        }
    }
```

And in `BusValue`, return the LC's read value for `$C08x` (it is mostly floating bus, but keep the seam honest):

```csharp
    private byte BusValue(uint offset)
    {
        byte o = (byte)offset;
        if (o is >= 0x80 and <= 0x8F)
            return _lc?.Access(o, isRead: true) ?? 0x00;  // NOTE: see the double-call caveat below
        return o switch
        {
            0x00 => _state.KeyboardByte,
            _ => 0x00,
        };
    }
```

> **Caveat — avoid a double `Access` for `$C08x` reads.** `Read` calls `ApplyAnyAccessSideEffect` (which would call `Access`) AND `BusValue` (which would call `Access` again). For `$C08x`, route the side effect through **one** path: have `ApplyAnyAccessSideEffect` skip `$C08x` (do nothing) and let `BusValue` own the `$C08x` `Access` call for reads; for writes, have `Write` call `Access(o, isRead:false)` directly. **Implementer: pick ONE call site per access** — the cleanest is: `ApplyAnyAccessSideEffect` handles `$C08x` for **writes only** (it is called from `Write` with `isRead:false`), and `BusValue` handles `$C08x` for **reads** (it is called from `Read`). Verify with a test that a single `Read8($C080)` increments `AccessCount` by **exactly 1** (the `A_C08x_access_reaches_the_LC` gate asserts `before + 1`, so a double-call fails it — the gate catches the mistake).

- [ ] **Step 5: Add the board overload**

Modify `src/CpuEmulator.Machines/Apple2Board.cs` — add `SpecWithLanguageCard` that wires the LC as a peripheral so its `Realize` runs:

```csharp
    /// <summary>The ][+ board with the Language Card wired (ADR 0014 Decision 4). The LC owns no bus
    /// page of its own (the IOU owns $C000-$C0FF, which includes $C08x) — so the board spec is byte-for-
    /// byte the base Spec; the IOU (already holding the LC) Realizes it (IOU-forwards-Realize), which is
    /// how the LC captures the program bus it remaps. No extra slot is added.</summary>
    public static BoardSpec SpecWithLanguageCard(byte[] systemRom, Apple2Iou iou, Apple2LanguageCard lc)
    {
        ArgumentNullException.ThrowIfNull(lc);
        return Spec(systemRom, iou);   // the IOU (holding the LC) Realizes it; no extra slot needed
    }
```

To make the IOU Realize the LC, add one line to `Apple2Iou.Realize` (it currently does nothing):

```csharp
    public void Realize(IMachineContext context)
    {
        _lc?.Realize(context);   // PR-E: the LC owns no page, so the IOU (a mapped peripheral) Realizes it
    }
```

> **Why IOU-forwards-Realize (and not a spare slot).** `BoardMachineFactory` only calls `Realize` on peripherals it **maps** onto the bus (`Machine.cs:49-52`). The LC has no page of its own, so it cannot be a normal mapped slot. The clean fix is to let the **IOU** — which IS a mapped peripheral — forward `Realize` to the LC it holds. (The alternative, mapping the LC on a spare `$C100` page purely to get a `Realize`, is rejected: a stray `$C1xx` read would then reach the LC's no-op `Read`, a real-if-harmless correctness wart, and it muddies the IOU-is-the-sole-`$C000`-page-owner invariant.) `Realize` is called once per peripheral in registration order, so forwarding from the IOU runs the LC's `Realize` exactly once. **Verify** `BoardSpecValidator.Validate(spec)` is empty (the `At_reset_D000_reads_the_system_ROM` test builds the spec, so a validation failure surfaces immediately).

- [ ] **Step 6: Run the seam tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.At_reset|FullyQualifiedName~Apple2LanguageCardTests.A_C08x"`
Expected: PASS — `$D000`/`$E000` read ROM at power-on; a single `$C080` read reaches the LC exactly once (the double-call caveat is honoured).

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2LanguageCard.cs src/CpuEmulator.Peripherals/Apple2Iou.cs src/CpuEmulator.Machines/Apple2Board.cs tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs
git commit -m "feat(peripherals): Apple2LanguageCard skeleton + IOU \$C08x delegate seam + board overload"
```

---

## Task 2: The `$C08x` decode + the `Remap` drive (bank / read-source selection)

**Files:**
- Modify: `src/CpuEmulator.Peripherals/Apple2LanguageCard.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs`

- [ ] **Step 1: Write the failing test (the decode truth table — asserted via the remapped backing)**

The un-fakeable assertion: after a `$C08x` access, read `$D000`/`$E000` through the bus and check whether ROM (the marker bytes) or RAM (a byte the test poked into the bank) is visible. Append to `Apple2LanguageCardTests`:

```csharp
    [Fact]
    public void C083_selects_read_RAM_bank1_and_C081_keeps_read_ROM()
    {
        var (_, bus, _) = BuildWithLc();
        // First arm + enable bank-1 RAM (two consecutive reads of $C083 arm write; one read selects
        // read-RAM immediately). We do the two reads so the RAM is also writable for the poke.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);    // read-RAM, bank 1, write-enabled
        bus.Write8(0xD000, 0x11);                         // poke RAM bank 1 (write-enabled)
        bus.Write8(0xE000, 0x22);                         // poke shared $E000
        Assert.Equal(0x11, bus.Read8(0xD000));            // reads now see RAM bank 1
        Assert.Equal(0x22, bus.Read8(0xE000));

        // $C081 = read ROM again (the marker bytes reappear).
        _ = bus.Read8(0xC081);
        Assert.Equal(0xA5, bus.Read8(0xD000));            // ROM marker at $D000
        Assert.Equal(0x5C, bus.Read8(0xE000));            // ROM marker at $E000
    }

    [Fact]
    public void Bank2_is_a_distinct_D000_region_from_bank1_but_E000_is_shared()
    {
        var (_, bus, _) = BuildWithLc();
        // Bank 1: read-RAM + write-enable via two $C083 reads; poke a distinct byte.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);
        bus.Write8(0xD000, 0xB1);                          // bank-1 $D000
        bus.Write8(0xE000, 0xEE);                          // shared $E000

        // Bank 2: read-RAM + write-enable via two $C08B reads; poke a DIFFERENT byte at $D000.
        _ = bus.Read8(0xC08B); _ = bus.Read8(0xC08B);
        bus.Write8(0xD000, 0xB2);                          // bank-2 $D000 (distinct backing)
        Assert.Equal(0xB2, bus.Read8(0xD000));             // bank 2 shows its own byte
        Assert.Equal(0xEE, bus.Read8(0xE000));             // $E000 is SHARED -> bank-1's poke persists

        // Back to bank 1: its $D000 byte is intact (distinct region).
        _ = bus.Read8(0xC083);
        Assert.Equal(0xB1, bus.Read8(0xD000));
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.C083|FullyQualifiedName~Apple2LanguageCardTests.Bank2"`
Expected: FAIL — `Access` is still a no-op stub (no remap).

- [ ] **Step 3: Implement the decode + the `Remap` drive**

Replace `Apple2LanguageCard.Access` (and add the private `ApplyMapping`). The decode follows the standard LC truth table (Sather): **bit 3 of the offset (`$C088` line) selects the bank** (`(offset & 0x08) == 0` → bank 1, else bank 2); **bit 0 (odd address) arms write** and **`(offset & 0x03)` selects the read source** (read RAM when `(offset & 0x03) is 0 or 3`, else read ROM). The pre-write flip-flop: only **reads** of an **odd** `$C08x` increment the arm counter; any other access resets it; write-enable engages once two consecutive qualifying reads have occurred.

```csharp
    public byte Access(byte offset, bool isRead)
    {
        AccessCount++;
        int o = offset & 0x0F;   // $C080-$C08F low nibble

        // Bank select: the $C088 line (bit 3) picks the $D000 bank. Polarity is pinned by the Task-2
        // gate: $C083 (o=3, bit 3 clear) selects bank 1; $C08B (o=$B, bit 3 set) selects bank 2.
        _bank = (o & 0x08) == 0 ? 1 : 2;   // bit 3 clear => bank 1, set => bank 2 (gated by Task 2)

        // Read source: read RAM when (o & 0x03) is 0 or 3; read ROM when it is 1 or 2.
        int sel = o & 0x03;
        _readRam = sel is 0 or 3;

        // Pre-write flip-flop: an ODD address (bit 0 set) READ arms; two consecutive arm-reads enable
        // writes. Any non-qualifying access (a write, or an even address) resets the counter + disables.
        bool qualifies = isRead && (o & 0x01) != 0;
        if (qualifies)
        {
            if (_armCount < 2) _armCount++;
            _writeEnabled = _armCount >= 2;
        }
        else
        {
            _armCount = 0;
            _writeEnabled = false;
        }

        ApplyMapping();
        return 0x00;   // floating bus on a soft-switch read (the side effect is the remap)
    }

    /// <summary>Re-point $D000 (the active bank) + $E000 (shared) at ROM or RAM per the decoded state,
    /// via the shipped IAddressSpace.Remap (PR-A). Read-ROM -> map the ROM slices read-only. Read-RAM ->
    /// map the RAM arrays with the decoded writability. (The ][+ "read ROM / write RAM" split collapses
    /// to a single backing per page on the shipped single-backing page table — PR-E maps the READ source;
    /// the write-enable rides the same backing's Writable flag, so read-RAM+write-enabled is the writable
    /// case DOS/ProDOS/CP/M use. Read-ROM is read-only; a separate write-through-to-RAM-while-reading-ROM
    /// page is out of scope and not needed for the target software — noted for the JIT-tier follow-on.)</summary>
    private void ApplyMapping()
    {
        if (_readRam)
        {
            _bus.Remap(DBank, _bank == 1 ? _bankD1 : _bankD2, writable: _writeEnabled);
            _bus.Remap(EShared, _sharedE, writable: _writeEnabled);
        }
        else
        {
            _bus.Remap(DBank, _romD, writable: false);     // read system ROM at $D000
            _bus.Remap(EShared, _romE, writable: false);   // read system ROM at $E000
        }
    }
```

> **Implementer note — the bank/read-source bits are gated, not guessed.** The ][+ LC `$C08x` decode has a couple of equivalent conventions in the literature, so the **Task-2 tests are the source of truth**: `$C083` (low nibble 3) selects read-RAM **bank 1**; `$C08B` (low nibble `B` = `0b1011`) selects read-RAM **bank 2**. The code above is already aligned to the gate (`_bank = (o & 0x08) == 0 ? 1 : 2` → `$C083`→bank 1, `$C08B`→bank 2; `_readRam = (o & 0x03) is 0 or 3` → `$C083`/`$C080` read RAM, `$C081`/`$C082` read ROM). If a gate fails, adjust the bit polarity to match the test rather than the prose — the gate is authoritative. (This is exactly why the LC is planned JIT against shipped code + pinned by tests rather than guessed.)

- [ ] **Step 4: Run the decode gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.C083|FullyQualifiedName~Apple2LanguageCardTests.Bank2"`
Expected: PASS — read-RAM/read-ROM select the right backing via `Remap`; bank 1 and bank 2 are distinct `$D000` regions; `$E000` is shared. **This is the bank/read-source gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2LanguageCard.cs tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs
git commit -m "feat(peripherals): Apple2LanguageCard \$C08x decode + Remap drive (bank / read-source)"
```

---

## Task 3: The two-consecutive-reads pre-write flip-flop + 48K↔64K presence

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs`

The arming logic shipped in Task 2; Task 3 adds the dedicated gate that **one** read does not write-enable (only two consecutive do), and the write-test presence detection.

- [ ] **Step 1: Write the failing/passing arming + presence gates**

Append to `Apple2LanguageCardTests`:

```csharp
    [Fact]
    public void One_read_of_an_odd_C08x_does_NOT_write_enable_LC_RAM()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083);                 // ONE arm-read: read-RAM selected, but write NOT enabled
        bus.Write8(0xD000, 0x99);              // write to $D000 RAM -> should be IGNORED (write-protected)
        Assert.NotEqual(0x99, bus.Read8(0xD000));  // the poke did not take (RAM still write-protected)
    }

    [Fact]
    public void Two_consecutive_reads_of_an_odd_C08x_write_enable_LC_RAM()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);   // TWO consecutive arm-reads -> write-enabled
        bus.Write8(0xD000, 0x99);
        Assert.Equal(0x99, bus.Read8(0xD000));          // the poke took (RAM now writable)
    }

    [Fact]
    public void A_write_between_the_reads_resets_the_pre_write_flip_flop()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083);                 // arm 1
        bus.Write8(0xC083, 0x00);              // a WRITE to $C083 resets the counter (not a qualifying read)
        _ = bus.Read8(0xC083);                 // arm 1 again (not 2) -> still write-protected
        bus.Write8(0xD000, 0x77);
        Assert.NotEqual(0x77, bus.Read8(0xD000));
    }

    [Fact]
    public void Presence_detection_a_write_test_to_D000_RAM_reads_back_when_64K()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);   // read-RAM + write-enable
        bus.Write8(0xD000, 0x3C);
        Assert.Equal(0x3C, bus.Read8(0xD000));          // write-then-read-back succeeds => 64K present
    }
```

- [ ] **Step 2: Run them to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.One_read|FullyQualifiedName~Apple2LanguageCardTests.Two_consecutive|FullyQualifiedName~Apple2LanguageCardTests.A_write_between|FullyQualifiedName~Apple2LanguageCardTests.Presence"`
Expected: PASS. **This is the pre-write-flip-flop + presence gate** — one read does not arm; two consecutive do; a write between resets; the write-test read-back proves 64K.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs
git commit -m "test(apple2): LC two-consecutive-reads pre-write flip-flop + 48K/64K presence"
```

---

## Task 4: The un-fakeable interpreter-tier gate — run code OUT of LC RAM (both tiers)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs`

The row-E deliverable: a real 6502 program copies a routine into LC RAM, banks read-RAM, and **executes from `$D000`** — proving the remap re-points the live bus AND (on the JIT tier) PR-A's listener evicts/re-classifies so the new bank's code runs.

- [ ] **Step 1: Write the failing/passing run-code gate (parameterised over the tier)**

Append to `Apple2LanguageCardTests`:

```csharp
    [Theory]
    [InlineData(ExecutionTier.Interpreter)]   // the oracle: correct with no listener
    [InlineData(ExecutionTier.Jit)]           // exercises PR-A's OnRemap -> reclassify + evict
    public void A_real_program_runs_code_out_of_LC_RAM(ExecutionTier tier)
    {
        var (machine, bus, _) = BuildWithLc(tier);

        // 1) Arm + write-enable read-RAM bank 1 via two $C083 reads (done from RAM-resident setup code).
        //    For the test we drive the banking through the bus directly, then load a routine into LC RAM,
        //    then jump to it.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);     // read-RAM, bank 1, write-enabled

        // 2) Write a tiny routine into LC RAM at $D000:  LDA #$42 ; STA $0400 ; JMP $D008 (spin)
        //    $D000: A9 42      LDA #$42
        //    $D002: 8D 00 04   STA $0400
        //    $D005: 4C 05 D0   JMP $D005   (spin in LC RAM)
        bus.Write8(0xD000, 0xA9); bus.Write8(0xD001, 0x42);
        bus.Write8(0xD002, 0x8D); bus.Write8(0xD003, 0x00); bus.Write8(0xD004, 0x04);
        bus.Write8(0xD005, 0x4C); bus.Write8(0xD006, 0x05); bus.Write8(0xD007, 0xD0);

        // 3) Execute from LC RAM.
        machine.Cpu.SetRegister("PC", 0xD000);
        machine.Run(50);

        // The routine, fetched + run FROM the remapped LC RAM page, wrote $42 to $0400.
        Assert.Equal(0x42, bus.Read8(0x0400));
    }
```

- [ ] **Step 2: Run it on both tiers to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2LanguageCardTests.A_real_program_runs_code"`
Expected: PASS on **both** tiers. **This is the row-E interpreter-tier gate (interpreter-as-oracle) + the JIT remap-consumer gate:** a real 6502 routine, fetched from the `Remap`ped LC RAM page, executes and stores `$42` — on the interpreter (correct by re-reading the page table) AND on the JIT (PR-A's `OnRemap` re-classified the `$D000` page in `Fastmem` + evicted any stale blocks so the new bank's code runs).

> **If the JIT case fails** while the interpreter passes: the symptom is the JIT running stale/ROM bytes from `$D000` after the remap. That is PR-A's listener not covering the page — confirm `Remap($D000, …)` fires `OnRemap(firstPage=0xD0, pageCount=16)` and that `JittedCpu.OnRemap` calls `Fastmem.Reclassify` + `BlockCache.InvalidatePages` over that span (PR-A Task 5). The fix is in PR-A's already-shipped path; if it is genuinely missing, raise it to the owner (a PR-A gap) rather than working around it in the LC.

- [ ] **Step 3: Run the full Apple2 suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Expected: PASS — PR-B/C/D gates + PR-E's LC gates all green (and the PR-B `Apple2BoardTests` still pass: the base no-LC `Spec` overload is unchanged).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2LanguageCardTests.cs
git commit -m "test(apple2): LC run-code-out-of-LC-RAM gate (interpreter oracle + JIT remap consumer)"
```

---

## Task 5: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **E** status to ✅ and update the **Last updated** banner with the date + "PR-E merged".

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-E (Language Card) done"
```

---

## Done-when

- `Apple2LanguageCard : IPeripheral` owns `$C080–$C08F` (delegated by the IOU) and run-time bank-switches `$D000–$FFFF` between the system ROM and 16 KiB of card RAM (bank-1/bank-2 `$D000` + shared `$E000`) by calling the **shipped** `IAddressSpace.Remap`.
- The two-consecutive-reads pre-write flip-flop is correct (one read does not write-enable; two consecutive do; a non-qualifying access resets); read-ROM/read-RAM + bank selection pick the right backing; the 48K↔64K write-test reads back.
- A real 6502 program **runs code out of LC RAM** on **both** tiers — the interpreter is the oracle; the JIT exercises PR-A's `OnRemap` → `Fastmem.Reclassify` + `BlockCache.InvalidatePages` (the LC is the first real `Remap` consumer).
- The base no-LC `Apple2Board.Spec` overload is unchanged (PR-B's tests still pass).
- Queue row **E** is ✅.

---

## API-drift note for the owner

**No drift.** The shipped PR-A `Remap`/`RemapPeripheral` signatures and in-place semantics match ADR 0014 Decision 4 / ADR 0009 §3.2 verbatim — `void Remap(uint start, byte[] backing, bool writable)` on `IAddressSpace`, in-place page re-point, `Handler` cleared, listener fired. The **one** implementation consequence the ADR did not spell out is the **index-0-based backing** rule (`BackingOffset = i << 8` indexes from the passed array's start), which forces the LC to hold standalone per-bank / per-ROM-slice arrays rather than offsets into the 12 KiB image — captured in Task 1 fact 1. The **read-ROM/write-RAM split** (reads from ROM while writes land in RAM) cannot be expressed on the shipped single-backing-per-page table; PR-E maps the **read** source per page (the correct, write-protected-or-writable cases DOS/ProDOS/CP/M actually use), and the exotic simultaneous read-ROM/write-RAM page is noted as out of scope (no target software needs it). Flag if a title surfaces that depends on it.

---

## Notes for the PR-H / PR-J planner (deferred)

- PR-H boots Applesoft from the **real** system ROM; DOS 3.3 lives in LC RAM, so the LC is on the boot path. PR-H's ROM-boot gate exercises the LC implicitly.
- PR-J (SoftCard translation) reuses the LC `Remap` for the Z80's `$B000`/`$D000` view (ADR 0015) — the same `$D000`/`$E000` arrays the LC banks here. The LC's array layout (three index-0-based arrays) is the seam PR-J builds on.
- ADR 0013's per-bank block specialization (`(PC, BankConfigId)`) is the optional speed dial if LC bank-thrash shows in a profile — the LC would assign a `BankConfigId` per (bank, read-source, write-enable) configuration. **Deferred; the page-precise evict-on-remap PR-A ships is correct + sufficient.**
