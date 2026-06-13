# ADR 0002 — Address-space scaling beyond 24 bits

> **Status:** Accepted (decision recorded; no implementation now)
> **Date:** 2026-06-13
> **Context:** Raised during M3 planning — "is there a technical reason we limit address
> space to 24 bits, and can it change later, or is it an architectural concern now?"
> **Related:** ADR 0001 (Z80 second architecture); design spec §3–§4 (the page-table bus).

## Context

`CpuEmulator.Core.AddressSpace` validates `addressBits` in the range **8–24** and throws a
`MachineConfigurationException` outside it. The question is whether that ceiling is a
*fundamental* constraint or an *incidental* one, and whether supporting 32-bit (or wider)
address spaces must be designed now or can be deferred.

### Why the ceiling exists today

`AddressSpace` decodes addresses through a **flat page table** — one `PageEntry` (≈32 bytes:
a backing-array reference, offset, writability, an `IPeripheral?` handler, handler base) per
256-byte page, sized `1 << (addressBits − 8)`:

| `addressBits` | pages | flat-table footprint (≈32 B/entry) |
|---|---:|---:|
| 16 | 256 | ~8 KB |
| 24 | 65,536 | ~2 MB |
| **32** | **16,777,216** | **~512 MB of mostly-empty entries** |

The cap is therefore a **memory-footprint guard against a flat table at large widths**, not a
limit of the addressing model. It was a deliberate, recorded deferral from the start (M1
chunk-1 plan: *"32-bit spaces need a two-level table and are explicitly out of scope until a
32-bit CPU exists"*). This ADR formalizes that and maps the path forward.

### What is already wide enough vs. what is not

- **Address *values* are already 32-bit.** The bus contract is `Read8(uint)`/`Write8(uint)`,
  `MapMemory(uint, …)`, `MapPeripheral(uint, …)`, and `IPeripheral.Read(uint offset, …)` /
  `Write(uint offset, …)`. `uint` holds a full 32-bit address today, so **the public interface
  does not block 32-bit addressing** — only the internal page-table data structure does.
- **The blocker is purely the flat page table.** Nothing else in the decode path, the JIT
  fastmem seam (`AddressSpace.TryGetDirectAccess`), or the monitor assumes ≤24 bits beyond the
  table sizing.

### Two distinct scaling steps (they are not the same change)

1. **Up to and including 32-bit addresses — a contained internal data-structure change.**
   Replace the flat table with a **two-level (page-directory → page-table) or sparse table**, so
   only touched regions allocate (the standard MMU / QEMU-softmmu pattern). Decode stays
   effectively O(1) (two index operations instead of one). This lives **entirely inside the
   concrete `AddressSpace` class** behind the stable `IAddressSpace` interface — consumers
   (`Read8`/`Write8`/`MapMemory`/the monitor) do not change. The JIT's internal fastmem view
   learns the two-level shape, but that binding is already internal.
   - **Correctness wrinkle to flag for the implementer:** the current mask
     `(1u << addressBits) − 1` is wrong at *exactly* 32 (`1u << 32` wraps to `1`, giving mask
     `0`). `addressBits == 32` must special-case the mask to `uint.MaxValue`.

2. **Beyond 32-bit (true 64-bit addressing) — a pervasive, breaking interface change.**
   `uint` cannot represent >32-bit addresses; this would require **`uint → ulong`** across the
   `IAddressSpace` methods, every `IPeripheral` implementation, the emitted JIT address
   arithmetic, the monitor, and `AddressSpace` internals. This is wide and breaking — the kind
   of change that is cheaper to decide early *if it is likely*.

## Decision

**Defer both scaling steps; do not build either now. Record the path so each is an eyes-open,
scoped future task rather than an undocumented cap someone trips over.**

- **32-bit:** a deferred two-level/sparse page-table change behind the stable `IAddressSpace`
  interface (+ the `1u << 32` mask special-case). No public API change.
- **>32-bit:** the one genuinely-breaking axis (`uint → ulong`). The framework intentionally
  uses `uint` addresses, which comfortably cover 32-bit. Widening to `ulong` is a deliberate
  future project undertaken **only if a 64-bit-address CPU is targeted**, not a surprise.

### Why defer (not now)

- **Nothing on the locked roadmap needs >24 bits.** The committed genericity ladder is
  68000 (24-bit address bus) and 8086 (20-bit physical) — both *under* the current cap; the cap
  is not even reached. The ceiling first binds at a **68020 / 80386-class 32-bit-address CPU**,
  which is not in M3–M5.
- **Building two-level now is unused complexity plus a hot-path tax.** A two-level walk adds an
  indirection to *every* bus access — the single hottest path in the emulator — for address
  ranges nothing currently uses. Textbook YAGNI.
- **Choosing `ulong` now would be premature pessimization.** Wider arithmetic on every bus
  access and in every emitted fast-path index, to serve >32-bit ranges no planned target has.
  `uint` is the right, simpler choice for a framework whose stretch targets top out at 32-bit.

## Consequences

- The `addressBits` validation stays **8–24** until a 32-bit-address CPU is on the roadmap; the
  message should point here.
- When that CPU arrives: relax the cap to 32, swap the flat table for two-level/sparse, add the
  `addressBits == 32` mask special-case, and teach the JIT fastmem view the two-level structure.
  The `IAddressSpace` contract and all consumers are unaffected; expect new tests for sparse
  mapping, the 32-bit mask boundary, and JIT fastmem over a two-level table.
- If a 64-bit-address CPU is ever targeted, treat `uint → ulong` as its own scoped widening
  milestone (interface + peripherals + JIT emit + monitor), decided deliberately.
- **No code changes result from this ADR.** It is a recorded decision and map.

## Revisit triggers

- A roadmap addition of a CPU with a >24-bit address space (e.g. 68020, 80386) → implement
  scaling step 1 (two-level table).
- A roadmap addition of a CPU with a >32-bit address space (a 64-bit core) → evaluate scaling
  step 2 (`uint → ulong`) as a dedicated milestone.
