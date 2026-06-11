# Building a pluggable, IL-emitting CPU-emulation framework in C#

> **Status:** Research / scoping document
> **Date:** 2026-06-11
> **Context:** Foundational research for the CpuEmulator project — a multi-architecture CPU
> emulation framework in modern C# that uses the .NET runtime's JIT as a dynamic-recompilation
> backend, with pluggable CPU specifications and pluggable bus peripherals.

---

## Verdict up front

The instinct is sound and the design space is well-charted — but by other communities, in other
languages. Three findings shape everything:

1. **The two halves of the idea are individually solved problems with canonical reference designs.**
   "Pluggable CPU spec" → Ghidra **SLEIGH**, **Sail**, **ArchC**, **Pydgin**. "Pluggable peripherals
   on a bus" → **MAME's device-interface model** and **QEMU's QOM/qdev**. Nobody has combined *both*
   into a clean **.NET IL-JIT** framework. That is the genuine — and defensible — gap.

2. **One strategic fork dominates the architecture: JIT-deploy vs AOT-deploy.** Runtime IL emission
   (the fast dynarec path) is *fundamentally incompatible* with .NET NativeAOT. This single fact
   constrains the codegen backend more than any performance consideration. Decide it first.

3. **An extraordinary validation asset exists.** TomHarte's **SingleStepTests/ProcessorTests** are
   language-agnostic, cycle-by-cycle JSON test vectors *with bus activity* covering almost exactly the
   target list (6502/65C02, 68000, 8088≈8086, Z80, …), ~10,000 tests per opcode.

---

## 1. Prior-art map

### (a) C# IL-dynarec — direct precedents (small but real)
- **Chip8CIL** — CHIP-8 with CIL dynarec, **~100× over its interpreter**. Proof the approach works and
  is approachable. <https://github.com/exelix11/Chip8CIL>
- **Dotnet6502** — 6502 → its own IR → **MSIL per instruction** into a dynamic assembly, with an
  `IJitCustomizer` hook for per-machine hardware. Closest existing thing to the 8-bit tier.
  <https://github.com/KallDrexx/Dotnet6502>
- **Ryujinx / ARMeilleure** — the cautionary giant: *started* as ARM→IL via `Reflection.Emit`, then
  **abandoned IL** for its own native x86/ARM backend to control register allocation and escape
  RyuJIT's per-method overhead. Defines where the IL ceiling is.
  <https://blog.ryujinx.org/summer-progress-report/>

### (b) Multi-arch frameworks to steal abstractions from (not C#, but the designs are the point)
- **MAME** — `device_t` + six composable **device interfaces** (execute, memory, state, nvram, disasm,
  sound); `address_space` with separate **program / data / I/O** spaces; memory handlers call out to
  peripheral code. The gold standard for *pluggable peripherals*.
  <https://docs.mamedev.org/techspecs/memory.html> ·
  <https://docs.mamedev.org/techspecs/device_memory_interface.html>
- **QEMU** — **TCG** (guest → IR → JIT, cached translation blocks, **softmmu TLB**, MMIO calls out to
  C) for the dynarec, plus **QOM/qdev** (object model, bus/device tree, `realize` lifecycle, "container"
  devices for SoCs, property-based wiring) for the device model.
  <https://www.qemu.org/docs/master/devel/tcg.html> ·
  <https://qemu-project.gitlab.io/qemu/devel/qom.html>
- **Unicorn Engine** — QEMU's core repackaged as a clean *architecture-neutral API* with `uc_mmio_map`
  MMIO read/write callbacks and memory hooks. Essentially the API ergonomics to aim for, in C.
  <https://www.unicorn-engine.org/>
- **BizHawk** — the C# reference for *hosting heterogeneous cores* under standard interfaces
  (`IEmulator`, `IMemoryDomains`, `IDebuggable`). <https://github.com/TASEmulators/BizHawk>

### (c) ISA-description languages — backing for "one spec drives everything"
- **Ghidra SLEIGH** — describes encoding + operands + **semantics as P-code** (an RTL). One spec →
  disassembly + semantics. <https://ghidra.re/ghidra_docs/languages/html/sleigh.html>
- **Sail** — define a `decode` (bits→AST) and `execute` per instruction; **generates executable
  emulators in C/OCaml**. The official RISC-V/ARM formal model. <https://github.com/rems-project/sail>
- **Pydgin** (Cornell) — strongest evidence for the thesis: a concise ISA description in RPython, run
  through PyPy's **meta-tracing JIT**, yields a DBT simulator with performance "comparable to
  hand-coded DBT-ISSs." The .NET analogue: one declarative spec → source-generated interpreter *and*
  IL-emitter. <https://www.csl.cornell.edu/~cbatten/pdfs/lockhart-pydgin-ispass2015.pdf>

### Existing C# interpreter cores to crib from (per-ISA, all interpreters)
- **Asm6502** (cycle-accurate 6502 + pluggable 64 KiB bus) — <https://github.com/xoofx/Asm6502>
- **Z80dotNet** — <https://github.com/Konamiman/Z80dotNet>
- **Zem80** — <https://github.com/neilhewitt/Zem80>
- **crankery/emulate** (8080 + 6502, explicitly "pluggable") — <https://github.com/crankery/emulate>

The multi-arch ambition is well-trodden at the *interpreter* level; the unmet need is the unifying
JIT framework.

---

## 2. Abstraction #1 — the pluggable CPU/ISA spec

The deep lesson from SLEIGH/Sail/Pydgin: **separate three concerns** that hobby emulators usually fuse:

- **Decode** — bits → an instruction descriptor (opcode id + operands). Data-driven; a table.
- **Semantics** — what the instruction *does* to architectural state, expressed in a small **RTL/IR**
  (`reg[A] = reg[A] + mem[addr]; setflags(...)`), *not* in hand-written per-instruction C#.
- **Binding** — how that abstract state (registers, flags, PC, cycle counter) maps to concrete storage.

Why this matters here specifically: **if semantics are expressed in an IR, both the interpreter and the
IL-emitter come from the *same* spec.** Walk the IR → execute it = interpreter. Walk the IR → emit IL =
JIT. That is the Pydgin trick, and it makes "pluggable CPU" tractable instead of a doubling of work per
architecture.

**Recommendation:** define instructions in a C# **data-driven table** (think WinUAE's `table68k` but as
typed C# records), with semantics built from a small set of **micro-ops**. Then a **Roslyn source
generator** emits both tiers at build time — the modern, AOT-safe equivalent of WinUAE's `gencpu`.

---

## 3. Abstraction #2 — the pluggable bus + peripherals

Borrow MAME's model almost wholesale:

- **`IAddressSpace`** — the bus. Supports MAME's three flavors where needed: **program**, **data**
  (Harvard parts like the 8051), **I/O** (separate port space, e.g. 8086 `IN`/`OUT`, Z80 ports). The
  8051 and 8086 targets *require* this multi-space design from day one.
- **`IPeripheral`** mapped into a space over an address range, with `Read(offset,size)` /
  `Write(offset,size,value)` — exactly Unicorn's `uc_mmio_map` callback shape.
- **Address decoding** via a page/bank table: each page points to either backing RAM/ROM (fast path) or
  a device handler (slow path). MAME's "memory handlers call out to peripherals" and QEMU's "RAM/ROM
  vs MMIO" split.
- **Device lifecycle & wiring** from QOM: a two-phase `Construct` then `Realize` (wire to bus, claim
  IRQ lines, resolve properties), and **container devices** to compose SoCs/boards from smaller devices.

The target peripherals map cleanly: **memory** = RAM/ROM device; **serial** = a UART device exposing
data/status registers + a byte-stream sink/source; **digital/analog I/O** = GPIO/ADC/DAC devices on the
bus; plus the two you'll discover you need — **timers** and an **interrupt controller** — both
first-class MAME/QEMU device types.

---

## 4. The .NET code-generation backend — decide this first

| Backend | Control | Ease | Speed | AOT-safe? | Notes |
|---|---|---|---|---|---|
| Interpreter + `switch` | n/a | trivial | baseline | ✅ | Start here regardless. |
| Interpreter + **`delegate*`** table (C#9) | n/a | easy | faster dispatch, zero-alloc | ✅ | Best interpreter tier; no delegate heap/GC overhead. |
| **`System.Linq.Expressions`** | medium | high | fast (cache compiled delegate) | ⚠️ no (full compile needs runtime JIT; AOT falls back to slow interpretation) | Easiest dynarec to write; can't express `this`/ctors. |
| **`Reflection.Emit` / `ILGenerator`** | max | low (manual boxing, stack, lifting) | fastest IL path | ❌ no | Most control; what Ryujinx began with. |
| **Roslyn source generators** | high (compile-time) | medium | fast, fully optimized | ✅ yes | Compile-time codegen = modern `gencpu`. Can't translate ROM code discovered at runtime. |
| Custom native JIT (ARMeilleure-style) | total | very low | maximal | ✅ | Out of scope; only if IL is outgrown. |

**The bombshell:** `Reflection.Emit`, `DynamicMethod`, and full `Expression.Compile` **all require a
runtime JIT and are unsupported under NativeAOT** — there is no JIT to compile the IL
(<https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/>). Since *runtime discovery of
guest code is the entire point of a dynarec*, NativeAOT effectively forces either (a) interpretation, or
(b) source-generated translations of *known* ROMs ahead of time. Gate at runtime with
`RuntimeFeature.IsDynamicCodeSupported`.

**Recommended tiered strategy:**
- **Tier 0 — interpreter** via `delegate*` dispatch (AOT-safe, always present, the correctness oracle).
- **Tier 1 — IL-JIT** via `Reflection.Emit` for JIT deployments (emit IL, cache per basic block, let
  RyuJIT finish).
- **Source generators** produce *both* tiers from the one ISA spec, so adding a CPU is "write the spec,"
  not "write two emulators."

Keep tiers behind one interface so a JIT bug always falls back to the trusted interpreter.

---

## 5. The hard problems — where dynarec collides with pluggable peripherals

Sources: emudev.org *Common Dynarec Optimizations* (<https://emudev.org/2021/02/01/Dynarec.html>),
mupen64plus new_dynarec
(<https://github.com/mupen64plus/mupen64plus-core/blob/master/doc/new_dynarec.mediawiki>).

- **MMIO defeats inlining.** A load/store can only be inlined as a direct array access if the address
  *can't* hit a device. Universal solution: the **fastmem split** — emit a direct array read/write for
  RAM/ROM pages, and a **conditional call-out** to `IPeripheral` for MMIO pages (QEMU's exact
  RAM-vs-MMIO distinction). For fixed-map 8-bit machines addresses are often statically known; the 68k
  needs a page-table check in emitted code.
- **Self-modifying code** (and RAM-resident code) invalidates cached blocks. Three options on a cost
  curve: **page-protection faults** (coarse), **dirty-page bitmaps** (medium), **per-block checksums**
  (fine, costly). 8-bit machines that run code from RAM make this non-optional.
- **Cycle accuracy vs block translation.** Decrement a **cycle counter** inside each block; when it
  crosses zero, branch to an exit stub that checks interrupts (the mupen64plus `cc_interrupt` pattern).
  This gives **block-boundary** interrupt latency cheaply — fine for most retro software, but true
  *cycle-exact* mid-instruction timing fights dynarec hard. Pick the accuracy bar deliberately.
- **Block discovery & linking.** Decode until a branch; cache; then **chain** blocks with direct host
  branches (and maintain an unlink table so invalidation works). Pre-fill the dispatch table with a
  "compile-me" stub so the hot path has no "is it compiled?" check.

---

## 6. Recommended reference architecture

```
                ┌─────────────────────────────────────────┐
                │            Machine / Board                │  (QOM "container")
                │  wires CPU + peripherals, sets properties │
                └───────────────┬───────────────────────────┘
        ┌───────────────────────┼────────────────────────────┐
        ▼                       ▼                            ▼
  ICpuCore                 IAddressSpace[]              IClock / Scheduler
  - StateBlock             (program/data/IO)            - event queue
  - Tier0 interpret()      - page table:                - cycle budget
  - Tier1 jit(block)         RAM/ROM ► array
  - IRQ/NMI lines            MMIO    ► IPeripheral
        │                       ▲
        │ emitted code          │ Read/Write(offset,size)
        └── fast: array R/W ─────┤
            slow: callout ───────┴──► IPeripheral
                                       ├─ Ram/Rom
                                       ├─ Uart (serial in/out)
                                       ├─ Gpio / Adc / Dac
                                       ├─ Timer
                                       └─ InterruptController ──► CPU IRQ line
```

Core contracts: **`ICpuCore`** (register/flag/PC/cycle **StateBlock**, `Step`/`Run`, IRQ lines, both
execution tiers), **`IAddressSpace`** (decode + read/write + page table), **`IPeripheral`** (mapped MMIO
+ optional `IClocked` tick), **`IScheduler`** (event/cycle queue — the WinUAE `events.cpp` model),
**`IInterruptController`**. The **one ISA spec** feeds a source generator that emits the interpreter
*and* the IL-emitter, both reading/writing the same `StateBlock`.

---

## 7. Per-CPU reality check (target list)

- **6502 / 6800** — easy. Small, regular, fixed memory map. *Start here.*
- **Z80** — easy-medium. Prefix opcodes (CB/ED/DD/FD), `R` refresh register, separate I/O port space.
- **8051** — medium-weird. **Harvard** (needs the data/program split), bit-addressable memory, on-chip
  peripherals are part of the "CPU."
- **8086/8088** — hardest of the "simple" set. **Segmentation**, variable-length instructions,
  instruction prefixes. (Validate against TomHarte **8088**.)
- **68000 / 68020** — large but regular ISA; 68020 adds gnarly addressing modes and a bigger instruction
  set. Big but mechanical — ideal once the framework is proven (TomHarte **m68000** vectors exist).

---

## 8. Validation strategy (don't skip)

A pluggable multi-CPU framework needs a *uniform* correctness gate, and one exists:
**SingleStepTests/ProcessorTests** — JSON, language-agnostic, per-opcode (~10k cases each), **including
bus activity**, covering 6502/65C02, 68000, 8088, Z80, and more. Wire a generic harness that feeds these
vectors through any `ICpuCore` and diffs final state + bus transactions. Supplement with the classic
functional suites (Klaus Dörmann 6502, ZEXALL for Z80). This harness is also how the JIT tier is proven
to match the interpreter tier.

- <https://github.com/SingleStepTests/ProcessorTests>
- <https://github.com/SingleStepTests/m68000>

---

## 9. Suggested first milestone & honest risks

**Milestone 1 (proves the whole spine):** 6502 interpreter (`delegate*` tier) + `IAddressSpace` +
RAM/ROM + one UART, validated green against TomHarte 6502.
**Milestone 2:** add the `Reflection.Emit` JIT tier for the 6502, prove parity + a speedup.
**Milestone 3:** add Z80 *by writing only a spec*, proving the abstraction. Only then tackle
8086/68000.

**Risks to go in eyes-open:**
- **AOT vs JIT** is a real fork, not a footnote — it changes the deployment story and the backend.
- **Cycle-exact ambitions + dynarec are in tension.** Demo/game-grade timing accuracy makes the JIT
  dramatically harder; instruction-accurate is the dynarec sweet spot.
- **The IL ceiling is real** (Ryujinx's lesson). For 8/16-bit targets it will never be hit; don't
  pre-optimize toward a native backend.
- **Scope discipline.** The framework's value is the *abstractions*; resist perfecting any single CPU
  before the pluggable seams are proven across two.

---

## Sources

- Chip8CIL — <https://github.com/exelix11/Chip8CIL>
- Dotnet6502 — <https://github.com/KallDrexx/Dotnet6502>
- Ryujinx Summer 2019 progress report — <https://blog.ryujinx.org/summer-progress-report/>
- QEMU TCG (Translator Internals) — <https://www.qemu.org/docs/master/devel/tcg.html>
- QEMU Object Model (QOM) — <https://qemu-project.gitlab.io/qemu/devel/qom.html>
- MAME memory / address spaces — <https://docs.mamedev.org/techspecs/memory.html>
- MAME device memory interface — <https://docs.mamedev.org/techspecs/device_memory_interface.html>
- Unicorn Engine — <https://www.unicorn-engine.org/>
- BizHawk — <https://github.com/TASEmulators/BizHawk>
- Ghidra SLEIGH — <https://ghidra.re/ghidra_docs/languages/html/sleigh.html>
- Sail — <https://github.com/rems-project/sail>
- Pydgin (ISPASS 2015) — <https://www.csl.cornell.edu/~cbatten/pdfs/lockhart-pydgin-ispass2015.pdf>
- .NET NativeAOT limitations — <https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/>
- Expression trees vs Reflection.Emit — <https://www.infoq.com/articles/expression-compiler/>
- C# function pointers — <https://csharp-evolution.com/9.0/function-pointers>
- Common Dynarec Optimizations — <https://emudev.org/2021/02/01/Dynarec.html>
- mupen64plus new_dynarec — <https://github.com/mupen64plus/mupen64plus-core/blob/master/doc/new_dynarec.mediawiki>
- SingleStepTests/ProcessorTests — <https://github.com/SingleStepTests/ProcessorTests>
- SingleStepTests/m68000 — <https://github.com/SingleStepTests/m68000>
