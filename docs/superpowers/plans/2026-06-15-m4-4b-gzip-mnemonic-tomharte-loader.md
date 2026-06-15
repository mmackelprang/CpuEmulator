# M4.4b: The gzip, mnemonic-keyed 680x0 TomHarte Loader + the prefetch queue + the parse proof

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is the SECOND of the two M4.4 slices. **M4.4a (the FieldGrammar dataset + the importer
> arm + the real `M68000Spec.cs` + the regen guard) SHOULD be merged first** — but M4.4b is technically
> independent of it (the loader is test infrastructure; it does not consume the spec). M4.5 (the op BODIES +
> the green TomHarte sweep) depends on BOTH.

**Goal:** build the structurally-NEW SingleStepTests/680x0 TomHarte loader (the existing 6502/Z80 loaders are
opcode-hex JSON; the 680x0 set is `68000/v1/*.json.gz` — **gzipped, mnemonic+size-keyed** files like
`ADD.b.json.gz`, several thousand cases each), its per-case schema parse (the 32-bit `d0–d7`/`a0–a6` + the
SEPARATE `usp`/`ssp` + the 16-bit `sr` + `pc`, the **2-word `prefetch` queue checked INITIAL and FINAL**, the
`ram`, and the word-granular `transactions` with `.b/.w/.l` tags), a `tools/get-test-vectors-68000.ps1`
fetch script (cache `<cache>/680x0/v1`), and a skip-when-absent theory attribute — and PROVE it PARSES (a
committed fixture or one real file). **M4.4b does NOT assert any opcode green** — the op bodies don't exist
(that is M4.5). The deliverable is a loader + a runner SCAFFOLD that parses a 680x0 case faithfully and can
be pointed at the interpreter in M4.5.

**Architecture:** The 6502/Z80 loaders (`TomHarteCase.cs`, `Z80TomHarteCase.cs`) read plain JSON keyed by
opcode-hex filenames, with the case schema baked in. The 680x0 set differs on THREE structural axes the
existing loaders cannot handle: (1) the files are **gzip-compressed** (`*.json.gz`) — a `GZipStream` decode
before `JsonDocument.Parse`; (2) the files are **mnemonic+size-keyed** (`ADD.b.json.gz`, not `D0.json` —
the filename is the disassembly, not the opcode); (3) the case schema carries a **2-word prefetch queue**
(`prefetch: [w0, w1]`, checked in BOTH `initial` and `final`) and a **word-granular transaction trace** with
`.b/.w/.l` size tags (`["r", <field2>, fc, addr, ".w", value]` — the exact tuple layout confirmed against the
upstream format note at Task 1). M4.4b adds a NEW `M68000TomHarteCase`/`M68000TomHarteLoader` (gzip-aware,
prefetch-carrying), a `M68000TomHarteVectors` (the `680x0/v1` cache resolver + the skip attribute), the
fetch script, and a `M68000TomHarteRunner` SCAFFOLD that sets the state, (in M4.5) Steps, and diffs — but in
M4.4b the runner only PARSES + sets state (no Step assertion). The 6502/Z80 loaders/runners/scripts are
untouched.

**Tech Stack:** C# (.NET 10), `System.IO.Compression.GZipStream`, `System.Text.Json` (`JsonDocument`,
matching the Z80 loader's streaming parse), xUnit (the skip-at-discovery `TheoryAttribute` pattern), and a
PowerShell fetch script (mirroring `tools/get-test-vectors-z80.ps1`). The op BODIES + the green sweep are M4.5.

---

## Scope

**IN scope (the loader + the schema parse + the fetch script + the parse proof; NO opcode asserted green):**

1. **The 680x0 case schema records + the gzip+mnemonic loader.** `M68000TomHarteCase` (Name, Initial, Final,
   Transactions, …) + `M68000State` (the 32-bit registers, the separate `usp`/`ssp`, the 16-bit `sr`, `pc`,
   the **2-word `prefetch`**, `ram`) + `M68000Transaction` (direction, the field-2 element, function-code,
   32-bit address, the `.b/.w/.l` size tag, value) + `M68000TomHarteLoader.LoadFile(path)` that
   **`GZipStream`-decompresses** a `*.json.gz` then `JsonDocument.Parse`es the array (the Z80 loader's
   streaming shape, plus the gunzip).
2. **The prefetch queue (the load-bearing new dimension).** Parse `prefetch: [w0, w1]` in BOTH `initial` and
   `final` (it is NOT in the 6502/Z80 schema). The runner sets the initial prefetch (M4.5 wires it into the
   `M68000Cpu` 2-word prefetch queue) and the diff CHECKS the final prefetch. M4.4b parses + carries it; the
   final-prefetch ASSERTION lights up in M4.5 (no Step here).
3. **The transactions trace decode.** Parse the `transactions` array into `M68000Transaction[]`; decode the
   **`.b/.w/.l` size tag** and the direction; **decode the unconfirmed field-2 element against the upstream
   format note** (Task 1 — it is likely a strobe/cycle code; carry it as a raw int either way so the parse is
   lossless and M4.5's trace-diff can interpret it). The bus-trace ASSERTION is M4.5; M4.4b parses it.
4. **The `680x0/v1` cache resolver + the skip-when-absent attribute.** `M68000TomHarteVectors
   .TryGetVectorDirectory()` resolving `<cache>/680x0/v1` (the upstream `SingleStepTests/680x0` layout) +
   `M68000TomHarteTheoryAttribute` skipping the theory at discovery when absent (the Z80 pattern).
5. **The fetch script `tools/get-test-vectors-68000.ps1`.** Sparse-checkout `SingleStepTests/680x0` `v1/`
   into `<dest>/680x0/v1` (the Z80 script's `$LASTEXITCODE`-checked shape). **CONFIRM the repo name + the
   in-repo path at Task 1 (the Z80 script's finding was the test set is at the repo TOP LEVEL `v1/`, not
   `z80/v1/` — re-verify for 680x0).**
6. **A parse PROOF.** Either (a) a committed tiny `*.json.gz` FIXTURE (2-3 hand-built cases exercising the
   prefetch + a `.b`/`.w`/`.l` transaction each) under `tests/.../TomHarte/fixtures/`, asserted to load +
   carry the right state/prefetch/transactions; OR (b) if the real vectors are present, a skip-gated theory
   that loads one real `*.json.gz` and asserts the case count + the first case's shape. **Do BOTH** — the
   fixture is the always-on proof (no vectors needed); the real-file theory is the skip-gated confirmation.
7. **The runner SCAFFOLD.** `M68000TomHarteRunner` that builds a fresh `M68000Cpu` over a tracing wide bus,
   sets the FULL initial state (32-bit regs, usp/ssp, sr, pc, prefetch, ram), and EXPOSES a `RunCase` that —
   in M4.4b — sets state + returns "not-yet-asserted" (a TODO(M4.5) marker), so M4.5 only has to fill the
   Step + diff. **No Step, no assertion in M4.4b** (the bodies don't exist).

**OUT of scope (later — do NOT reach for them):**

- **Any opcode asserted TomHarte-green / the Step + the per-transaction diff + the final-prefetch assertion** =
  M4.5 (the op bodies). M4.4b parses + sets state; it asserts NOTHING about execution.
- **The op BODIES / the interpreter / reset / the real 2-word prefetch-queue mechanism in `M68000Cpu`** = M4.5.
  M4.4b carries the prefetch DATA; M4.5 wires it into the CPU.
- **The FieldGrammar dataset / the importer / the real `M68000Spec.cs`** = M4.4a (companion plan). M4.4b is
  test infrastructure and does not touch the spec/importer.
- **The 68000-through-JIT sweep** = M4.6. **The wide-bus JIT hot-op emit** = M6.

> **The honest one-liner for M4.4b's close-state:** a NEW gzip-aware, mnemonic-keyed 680x0 TomHarte loader
> parses a `*.json.gz` case into `M68000TomHarteCase` (the 32-bit regs, the separate usp/ssp, the 16-bit sr,
> pc, the 2-word prefetch queue [initial AND final], ram, and the word-granular `.b/.w/.l` transactions with
> the field-2 element decoded against the upstream format note); a `680x0/v1` cache resolver + a
> skip-when-absent theory attribute + a `get-test-vectors-68000.ps1` fetch script mirror the Z80 harness; a
> committed `.json.gz` fixture proves the parse always-on, and a skip-gated theory loads one real file; a
> runner SCAFFOLD sets the full state but Steps + asserts NOTHING (the op bodies are M4.5). NO 680x0 vector is
> asserted green. The 6502/Z80 loaders/runners/scripts are byte-identical (the 680x0 loader is net-new code).

---

## Ground truth — what the Z80 loader/runner/vectors + M4.1/M4.2 ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 1** — M4.4b MIRRORS or EXTENDS them.

- **The Z80 loader (the parse SHAPE to mirror, minus gzip + plus prefetch).**
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteCase.cs` — `Z80TomHarteLoader.LoadFile(path)` (`:47-55`:
  `File.OpenRead` → `JsonDocument.Parse(stream)` → enumerate the array → `Parse(element)`), the
  `ReadState`/`ReadCycle`/`ReadPort` helpers (`:82-115`), the typed `B`/`U16`/`I32`/`Bit` getters
  (`:70-80`), the `Z80State`/`Z80Cycle`/`Z80Ram` records (`:13-43`). **M4.4b's loader is this shape with a
  `GZipStream` wrapper before `JsonDocument.Parse` + 32-bit register getters + the prefetch array.**
- **The Z80 vectors resolver + the skip attribute (the cache convention to mirror).**
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteVectors.cs` — `TryGetVectorDirectory()` (`:12-19`: the
  `CPUEMULATOR_TESTVECTORS` env override, the `~/.cache/cpuemulator/vectors` default, `Path.Combine(root,
  "z80", "v1")`, `Directory.Exists`), `Z80TomHarteTheoryAttribute` (`:24-32`: the skip-at-discovery message).
  The 6502 sibling is `TomHarteVectors.cs`. **M4.4b mirrors with `Path.Combine(root, "680x0", "v1")`.**
- **The Z80 fetch script (the `$LASTEXITCODE`-checked sparse-checkout to mirror).**
  `tools/get-test-vectors-z80.ps1` — the `$Destination` param + the `CPUEMULATOR_TESTVECTORS` default
  (`:6-9`), the already-present short-circuit (`:11-12`), the `git clone --depth 1 --filter=blob:none
  --sparse` + `sparse-checkout set v1` with `$LASTEXITCODE` checks (`:17-28`), the `Move-Item v1 →
  <dest>/z80/v1` (`:26-27`). **NOTE the script's recorded finding: the Z80 repo's test set is at the repo
  TOP LEVEL `v1/`, NOT `z80/v1/`. Re-verify the 680x0 repo layout at Task 1 (it may be `v1/` at top level OR
  `68000/v1/` — the ADR cites `68000/v1/*.json.gz`).**
- **The Z80 runner (the state-set + diff shape the M4.5 runner will need; M4.4b builds only the SCAFFOLD).**
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs` — `RunCase` (`:25-90`: build a tracing
  `AddressSpace`, write the initial RAM, `SetRegister` the full state, `Step`, diff registers + RAM + ports +
  cycle count + bus trace). **M4.4b's runner sets the 68000 state (32-bit regs via `SetRegister`, usp/ssp, sr,
  pc, prefetch, ram) but does NOT Step (no bodies) — it returns a TODO(M4.5) sentinel.**
- **The 68000 state model + the wide BE bus (M4.1/M4.2) the runner targets.**
  `src/CpuEmulator.Cpus.M68000/M68000Spec.cs` — D0–D7, A0–A6, USP, SSP, PC 32-bit, SR 16-bit. `M68000Cpu.cs`
  — the A7 banking + `SetRegister`/`GetRegister`. `src/CpuEmulator.Core/AddressSpace.cs` — the M4.2
  `Read16/Read32`/`Endianness` (BigEndian) + `TracingAddressSpace` recording word/long transactions with
  size. **Confirm the `M68000Cpu` ctor signature (does it take one wide bus, or program + something?) +
  whether `usp`/`ssp` are settable via `SetRegister("USP"/"SSP", …)` at Task 1.**
- **The ADR's confirmed schema facts (the reconciliation the Coordinator flagged).** ADR 0004 §5
  (`docs/architecture/0004-…md:160-163`): the vectors are **mnemonic-keyed gzipped** with **word-granular
  `.b/.w/.l` transactions** and **separate `usp`/`ssp` (no `a7`)** + the **2-word prefetch queue**; and the
  **unresolved ambiguity:** "the exact `transactions` tuple field 2 (the `["r", 4, 6, 3076, ".w", 1657]`
  second element — likely a cycle-offset or a strobe code) is unconfirmed; M4.2/M4.5 must decode it against
  the upstream README/format-version note during setup." ADR 0003 §1.4 confirms the same. **M4.4b is where
  the loader is built, so M4.4b decodes field 2 against the upstream note (Task 1) — carry it as a raw int
  regardless, so the parse is lossless even if the meaning is still open.**

### RECON FINDINGS / open items the loader must settle (the data WINS — flagged)

> The implementer MUST re-confirm each at Task 1 against the LIVE upstream `SingleStepTests/680x0` repo
> (clone it once, read the README + one `*.json.gz`). These are the schema unknowns the loader pins down.

- **G1 — the transactions tuple layout is UNCONFIRMED past direction/addr/size/value.** ADR cites
  `["r", 4, 6, 3076, ".w", 1657]` = `[dir, field2(?), fc, addr, sizeTag, value]`. **Confirm the field order +
  field 2's meaning** by reading the upstream README + a real case at Task 1. Parse defensively: read by
  POSITION but tolerate length variation (some entries — e.g. an idle/internal cycle — may be shorter). Carry
  field 2 as a raw `int` (named `Field2`/`CycleCode` with a doc-comment recording what the README says) so the
  parse is lossless. **This is the primary Coordinator-flagged ambiguity; the loader RESOLVES the layout
  (position-by-position) even if field 2's semantics stay documented-as-uncertain.**
- **G2 — register naming: confirm the exact JSON keys.** ADR says `d0–d7`, `a0–a6`, `usp`, `ssp`, `sr`, `pc`,
  `prefetch`, `ram` — and explicitly **NO `a7`** (usp/ssp instead). Confirm at Task 1 (read a real case's
  `initial`). The `ram` shape (the Z80 uses `[[addr, value], …]`; 680x0 likely the same with 32-bit addrs +
  byte values — confirm). The `prefetch` shape (`[w0, w1]` of 16-bit words — confirm count = 2).
- **G3 — the gzip wrapper is the ONLY structural delta in the loader core.** The Z80 loader does
  `using var stream = File.OpenRead(path); using var doc = JsonDocument.Parse(stream);`. M4.4b does
  `using var fs = File.OpenRead(path); using var gz = new GZipStream(fs, CompressionMode.Decompress); using
  var doc = JsonDocument.Parse(gz);`. Everything else (enumerate-the-array → Parse-each-element) is the same.
  Confirm `JsonDocument.Parse(Stream)` accepts the `GZipStream` (it does — it reads to end).
- **G4 — file enumeration is mnemonic-keyed, not opcode-keyed.** The Z80 tests enumerate `*.json` and derive
  the opcode from the filename. The 680x0 tests enumerate `*.json.gz` and the filename IS the
  mnemonic+size (`ADD.b.json.gz`). M4.4b's loader takes a PATH (the test supplies it); the test-data
  discovery (enumerate the dir, parse each) is M4.5's sweep concern — M4.4b's parse proof loads ONE file (the
  fixture, or one real file), so no full enumeration is built here. (Record: the per-mnemonic enumeration +
  the `[Theory]` data source over 125 files is M4.5.)
- **G5 — the fixture must be a REAL gzip.** The committed `.json.gz` fixture (Task 4) must be actual gzip
  bytes (so the `GZipStream` path is exercised), not plain JSON renamed. Generate it at author time with a
  one-liner (`gzip` or a tiny C# `GZipStream` write) and commit the binary. **Document the regeneration
  command in a sibling `fixtures/README.md`** so the fixture is reproducible.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteCase.cs` | Create | The 680x0 case schema records + the gzip+mnemonic `M68000TomHarteLoader` (the prefetch + transactions parse). |
| `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs` | Create | The `680x0/v1` cache resolver + the `M68000TomHarteTheoryAttribute` skip-when-absent. |
| `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs` | Create | The runner SCAFFOLD: set the full initial state on a fresh `M68000Cpu`; RunCase returns a TODO(M4.5) sentinel (no Step). |
| `tests/CpuEmulator.Tests/TomHarte/fixtures/m68000-sample.json.gz` | Create | A committed tiny gzip fixture (2-3 cases: prefetch + a `.b`/`.w`/`.l` transaction each). |
| `tests/CpuEmulator.Tests/TomHarte/fixtures/README.md` | Create | How the fixture was generated (the regeneration command — G5). |
| `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs` | Create | The parse proof: the fixture loads + carries the right state/prefetch/transactions; + a skip-gated real-file theory. |
| `tools/get-test-vectors-68000.ps1` | Create | Sparse-checkout `SingleStepTests/680x0` → `<dest>/680x0/v1` (the Z80 script's `$LASTEXITCODE`-checked shape). |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502/Z80
> byte-identity guard `RegeneratedSpecTests` + the whole Z80 + 6502 suites green), then commit. The 680x0
> loader is net-new test infrastructure; the 6502/Z80 loaders/runners/scripts are untouched. NO 680x0 opcode
> is asserted green (M4.5).

### Task 1: Baseline + the upstream-schema recon (NO production code; the loader's facts get pinned here)

**Files:** none (read-only + a throwaway clone).

- [ ] **Step 1: Branch.** Create the branch off the current main:
  Run: `git switch -c feat/m4-4b-gzip-tomharte-loader`
  Expected: on the new branch (head at main, or atop M4.4a if it merged — both fine; the loader is independent).

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT count.
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean.

- [ ] **Step 3: Recon the in-tree precedent (read, do NOT edit):**
  - `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteCase.cs` (the loader shape — esp. `LoadFile` `:47-55`,
    `ReadState` `:82-93`, `ReadCycle` `:95-106`, the typed getters `:70-80`).
  - `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteVectors.cs` (the resolver + skip attribute) +
    `TomHarteVectors.cs` (the 6502 sibling).
  - `tools/get-test-vectors-z80.ps1` (the fetch-script shape + the "test set at repo TOP LEVEL v1/" finding).
  - `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs:25-90` (the state-set + diff shape the M4.5 runner
    needs; M4.4b builds only the scaffold).
  - `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs` (the ctor signature + `SetRegister`/`GetRegister` + the A7
    banking + whether `usp`/`ssp` are settable by name — G2/runner) + `M68000Spec.cs` (the register list).
  - `src/CpuEmulator.Core/AddressSpace.cs` + `TracingAddressSpace` (the M4.2 wide BE bus + the word/long
    transaction recording — confirm the trace records size).
  - `docs/architecture/0004-…md:160-163` (the confirmed schema + the field-2 ambiguity) + ADR 0003 §1.4.

- [ ] **Step 4: Clone the upstream repo ONCE + pin the schema (the load-bearing recon).** Run the throwaway
  clone (a temp dir, deleted after):
  ```bash
  git clone --depth 1 --filter=blob:none --sparse https://github.com/SingleStepTests/680x0.git /tmp/680x0-recon
  git -C /tmp/680x0-recon sparse-checkout set v1
  ```
  Then read (in `/tmp/680x0-recon`): the repo README/format note, the in-repo path to the `*.json.gz` files
  (top-level `v1/` vs `68000/v1/` — G1/script), ONE decompressed case (`gzip -dc v1/ADD.b.json.gz | head`),
  and RECORD in this task's notes:
  - the EXACT in-repo path of the `.json.gz` files (drives the fetch script's `sparse-checkout` target);
  - the EXACT JSON keys for the state (`d0..d7`, `a0..a6`, `usp`, `ssp`, `sr`, `pc`, `prefetch`, `ram`) — G2;
  - the `prefetch` shape (count = 2? word values?) — the prefetch dimension;
  - the `ram` shape (`[[addr, value], …]`? 32-bit addr, byte value?);
  - the `transactions`/`cycles` array name + the tuple layout + **field 2's documented meaning** — G1;
  - the `.b/.w/.l` size-tag spelling (with or without the leading dot?).
  Delete the clone: `rm -rf /tmp/680x0-recon`.
  > **If the upstream repo is unreachable** (offline), pin the schema from ADR 0004 §5 + ADR 0003 §1.4 (they
  > confirm the keys + the field-2 ambiguity) and build the loader position-defensively (G1); the parse proof
  > then relies on the committed fixture (Task 4), which you author to the ADR-confirmed shape. Flag to the
  > Coordinator that field-2 semantics remain documented-as-uncertain.

- [ ] **Step 5:** No commit (read-only). Record the pinned schema facts in the PR description draft. Proceed.

---

### Task 2: The 680x0 case schema records + the gzip+mnemonic loader (TDD)

> The records + the loader: `GZipStream`-decompress, `JsonDocument.Parse`, enumerate the array, parse each
> case (the 32-bit regs, usp/ssp, sr, pc, the 2-word prefetch [initial+final], ram, the `.b/.w/.l`
> transactions with field 2 carried raw). Driven by a tiny INLINE JSON string in the test (no gzip yet — the
> gzip wrapper is proven by the fixture in Task 4; here we prove the SCHEMA parse via a `Parse(JsonElement)`
> entry point the loader exposes).

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteCase.cs`
- Test: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs` (create — the schema-parse half here;
  the gzip+fixture half is Task 4)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs` (schema-parse half). **Adjust the JSON keys
  + the transaction layout to MATCH the Task-1 pinned schema before running** (the shape below follows ADR
  0004 §5 / ADR 0003 §1.4):

```csharp
using System.Text.Json;
using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteLoaderTests
{
    // One case in the ADR-confirmed shape: separate usp/ssp, 16-bit sr, 2-word prefetch (initial + final),
    // ram as [addr, value] pairs, transactions as [dir, field2, fc, addr, sizeTag, value].
    private const string OneCase = """
        [{
          "name": "ADD.w sample",
          "initial": {
            "d0": 1, "d1": 2, "d2": 0, "d3": 0, "d4": 0, "d5": 0, "d6": 0, "d7": 0,
            "a0": 4096, "a1": 0, "a2": 0, "a3": 0, "a4": 0, "a5": 0, "a6": 0,
            "usp": 16384, "ssp": 32768, "sr": 8192, "pc": 1024,
            "prefetch": [53328, 0],
            "ram": [[1024, 208], [1025, 65]]
          },
          "final": {
            "d0": 3, "d1": 2, "d2": 0, "d3": 0, "d4": 0, "d5": 0, "d6": 0, "d7": 0,
            "a0": 4096, "a1": 0, "a2": 0, "a3": 0, "a4": 0, "a5": 0, "a6": 0,
            "usp": 16384, "ssp": 32768, "sr": 8192, "pc": 1028,
            "prefetch": [0, 1],
            "ram": [[1024, 208], [1025, 65]]
          },
          "transactions": [
            ["r", 4, 6, 1024, ".w", 53328],
            ["r", 4, 6, 1026, ".w", 1]
          ]
        }]
        """;

    [Fact]
    public void Parses_the_full_68000_case_shape()
    {
        using var doc = JsonDocument.Parse(OneCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());

        Assert.Equal("ADD.w sample", c.Name);
        Assert.Equal(1u, c.Initial.D[0]);
        Assert.Equal(2u, c.Initial.D[1]);
        Assert.Equal(4096u, c.Initial.A[0]);
        Assert.Equal(16384u, c.Initial.Usp);
        Assert.Equal(32768u, c.Initial.Ssp);
        Assert.Equal((ushort)8192, c.Initial.Sr);
        Assert.Equal(1024u, c.Initial.Pc);
        Assert.Equal((ushort)53328, c.Initial.Prefetch[0]);
        Assert.Equal((ushort)0, c.Initial.Prefetch[1]);
        Assert.Equal(2, c.Initial.Ram.Length);
        Assert.Equal(1024u, c.Initial.Ram[0].Address);
        Assert.Equal((byte)208, c.Initial.Ram[0].Value);

        // Final prefetch is DIFFERENT from initial (the load-bearing new dimension; asserted in M4.5).
        Assert.Equal((ushort)0, c.Final.Prefetch[0]);
        Assert.Equal((ushort)1, c.Final.Prefetch[1]);
        Assert.Equal(3u, c.Final.D[0]);
        Assert.Equal(1028u, c.Final.Pc);

        Assert.Equal(2, c.Transactions.Length);
        var t0 = c.Transactions[0];
        Assert.True(t0.IsRead);
        Assert.Equal(1024u, t0.Address);
        Assert.Equal(".w", t0.SizeTag);
        Assert.Equal(53328u, t0.Value);
        Assert.Equal(4, t0.Field2);          // carried raw (semantics documented-as-uncertain — G1)
        Assert.Equal(6, t0.FunctionCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteLoaderTests"`
  Expected: FAIL — `M68000TomHarteLoader`/`M68000TomHarteCase` do not exist.

- [ ] **Step 3: Implement the records + the loader.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteCase.cs`. **Match the JSON keys + the tuple positions to
  the Task-1 pinned schema; the code below follows the ADR-confirmed shape:**

```csharp
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>One RAM cell (32-bit address, byte value) — the 680x0 ram array is [addr, value] pairs.</summary>
internal sealed record M68000Ram(uint Address, byte Value);

/// <summary>
/// One word-granular 680x0 bus transaction. Layout (confirmed at Task 1 against the upstream format note):
/// [direction "r"/"w", Field2, FunctionCode, Address, SizeTag ".b"/".w"/".l", Value]. Field2 is carried
/// RAW (likely a cycle-offset or strobe code — ADR 0004 §5 records it as unconfirmed; the parse is lossless
/// so M4.5's trace-diff can interpret it once the meaning is pinned). The bus-trace ASSERTION is M4.5.
/// </summary>
internal sealed record M68000Transaction(
    bool IsRead, int Field2, int FunctionCode, uint Address, string SizeTag, uint Value)
{
    public override string ToString() =>
        $"{(IsRead ? "R" : "W")}{SizeTag} {Address:X6}={Value:X} (fc {FunctionCode}, f2 {Field2})";
}

/// <summary>
/// One 680x0 processor state: the 32-bit data/address registers (D[0..7]/A[0..6]), the SEPARATE usp/ssp
/// (NOT a7 — ADR 0003 §1.4), the 16-bit sr, the 32-bit pc, the 2-word prefetch queue, and ram. The prefetch
/// queue is the load-bearing new dimension (checked in BOTH initial and final — M4.5 asserts the final).
/// </summary>
internal sealed record M68000State(
    uint[] D, uint[] A, uint Usp, uint Ssp, ushort Sr, uint Pc, ushort[] Prefetch, M68000Ram[] Ram);

internal sealed record M68000TomHarteCase(
    string Name, M68000State Initial, M68000State Final, M68000Transaction[] Transactions);

/// <summary>
/// The SingleStepTests/680x0 loader. STRUCTURALLY NEW vs the 6502/Z80 loaders: the files are GZIP-compressed
/// (*.json.gz) and MNEMONIC+SIZE-keyed (ADD.b.json.gz). LoadFile gunzips then parses; the schema carries the
/// 2-word prefetch queue + word-granular .b/.w/.l transactions. Mirrors Z80TomHarteLoader's streaming shape.
/// </summary>
internal static class M68000TomHarteLoader
{
    /// <summary>Load a gzipped 680x0 vector file (*.json.gz) into its case list.</summary>
    public static List<M68000TomHarteCase> LoadFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);   // the ONLY core delta vs Z80 (G3)
        using var doc = JsonDocument.Parse(gz);
        var cases = new List<M68000TomHarteCase>(capacity: 1024);
        foreach (var element in doc.RootElement.EnumerateArray())
            cases.Add(Parse(element));
        return cases;
    }

    public static M68000TomHarteCase Parse(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        ReadState(element.GetProperty("initial")),
        ReadState(element.GetProperty("final")),
        [.. element.GetProperty("transactions").EnumerateArray().Select(ReadTransaction)]);

    private static uint U32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : 0u;

    private static ushort U16(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (ushort)v.GetUInt32() : (ushort)0;

    private static M68000State ReadState(JsonElement e)
    {
        var d = new uint[8];
        for (int i = 0; i < 8; i++) d[i] = U32(e, $"d{i}");
        var a = new uint[7];
        for (int i = 0; i < 7; i++) a[i] = U32(e, $"a{i}");

        ushort[] prefetch = e.TryGetProperty("prefetch", out var pf) && pf.ValueKind == JsonValueKind.Array
            ? [.. pf.EnumerateArray().Select(x => (ushort)x.GetUInt32())]
            : [0, 0];

        M68000Ram[] ram = e.TryGetProperty("ram", out var r) && r.ValueKind == JsonValueKind.Array
            ? [.. r.EnumerateArray().Select(static pair =>
              {
                  using var items = pair.EnumerateArray();
                  items.MoveNext(); uint address = items.Current.GetUInt32();
                  items.MoveNext(); byte value = items.Current.GetByte();
                  return new M68000Ram(address, value);
              })]
            : [];

        return new M68000State(d, a, U32(e, "usp"), U32(e, "ssp"), U16(e, "sr"), U32(e, "pc"), prefetch, ram);
    }

    /// <summary>Parse one transaction tuple by POSITION, tolerant of length variation (an idle/internal
    /// cycle may be shorter — G1). [dir, field2, fc, addr, sizeTag, value].</summary>
    private static M68000Transaction ReadTransaction(JsonElement tuple)
    {
        var items = tuple.EnumerateArray().ToArray();
        string dir = items.Length > 0 ? items[0].GetString() ?? "r" : "r";
        int field2 = items.Length > 1 && items[1].ValueKind == JsonValueKind.Number ? items[1].GetInt32() : 0;
        int fc     = items.Length > 2 && items[2].ValueKind == JsonValueKind.Number ? items[2].GetInt32() : 0;
        uint addr  = items.Length > 3 && items[3].ValueKind == JsonValueKind.Number ? items[3].GetUInt32() : 0u;
        string sz  = items.Length > 4 ? items[4].GetString() ?? ".w" : ".w";
        uint val   = items.Length > 5 && items[5].ValueKind == JsonValueKind.Number ? items[5].GetUInt32() : 0u;
        return new M68000Transaction(dir == "r", field2, fc, addr, sz, val);
    }
}
```

  > **If Task 1 pinned a DIFFERENT tuple layout** (e.g. field 2 is the fc and field 3 is field2, or the array
  > name is `cycles` not `transactions`, or the size tag has no leading dot), adjust `ReadTransaction` + the
  > `GetProperty("transactions")` + the test's `SizeTag` assertion to match. The POSITION-by-position +
  > length-tolerant read is the robust shape; only the indices/names change.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteLoaderTests"`
  Expected: PASS — the full case shape parses (regs, usp/ssp, sr, pc, prefetch initial≠final, ram,
  transactions with field 2 raw).

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (additive test infrastructure; nothing else references the new types).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 untouched).

- [ ] **Step 6: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000TomHarteCase.cs \
        tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs
git commit -m "$(cat <<'EOF'
feat(test): add the 680x0 TomHarte case schema + the gzip+mnemonic loader (prefetch + .b/.w/.l txns)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (one rich case-shape assertion).

---

### Task 3: The `680x0/v1` cache resolver + the skip-when-absent theory attribute (TDD)

> Mirror `Z80TomHarteVectors`: resolve `<cache>/680x0/v1` (the upstream layout) + the skip-at-discovery
> theory attribute. Proven by a test that the resolver returns null when the dir is absent (the default CI
> state) and the attribute carries the actionable skip message.

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs`
- Test: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectorsTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectorsTests.cs`:

```csharp
using System;
using System.IO;
using CpuEmulator.Tests.TomHarte;
using Xunit;

public class M68000TomHarteVectorsTests
{
    [Fact]
    public void Resolver_returns_null_when_the_680x0_directory_is_absent()
    {
        string prev = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? "";
        try
        {
            // Point the cache at an empty temp dir → no 680x0/v1 → null.
            string empty = Path.Combine(Path.GetTempPath(), $"no-vectors-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", empty);
            Assert.Null(M68000TomHarteVectors.TryGetVectorDirectory());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", prev.Length == 0 ? null : prev); }
    }

    [Fact]
    public void Resolver_finds_a_present_680x0_directory()
    {
        string prev = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? "";
        try
        {
            string root = Path.Combine(Path.GetTempPath(), $"vectors-{Guid.NewGuid():N}");
            string dir = Path.Combine(root, "680x0", "v1");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", root);
            Assert.Equal(dir, M68000TomHarteVectors.TryGetVectorDirectory());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", prev.Length == 0 ? null : prev); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteVectorsTests"`
  Expected: FAIL — `M68000TomHarteVectors` does not exist.

- [ ] **Step 3: Implement the resolver + the attribute.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs` (mirror `Z80TomHarteVectors`):

```csharp
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the SingleStepTests 680x0 vector directory (&lt;cache&gt;/680x0/v1) + the skip-at-discovery
/// attribute, mirroring Z80TomHarteVectors. The 680x0 set is mnemonic-keyed gzip (*.json.gz). Fetch with
/// tools/get-test-vectors-68000.ps1, or set CPUEMULATOR_TESTVECTORS.
/// </summary>
internal static class M68000TomHarteVectors
{
    public static string? TryGetVectorDirectory()
    {
        string root = System.Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".cache", "cpuemulator", "vectors");
        string dir = System.IO.Path.Combine(root, "680x0", "v1");
        return System.IO.Directory.Exists(dir) ? dir : null;
    }
}

/// <summary>TheoryAttribute that skips the whole theory at discovery when the 680x0 vectors are absent —
/// the same skip-when-absent discipline as the 6502/Z80 harness.</summary>
public sealed class M68000TomHarteTheoryAttribute : TheoryAttribute
{
    public M68000TomHarteTheoryAttribute()
    {
        if (M68000TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "680x0 TomHarte vectors not found — run tools/get-test-vectors-68000.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteVectorsTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 6: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs \
        tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectorsTests.cs
git commit -m "$(cat <<'EOF'
feat(test): add the 680x0/v1 vector resolver + the skip-when-absent theory attribute

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 4: The committed gzip fixture + the gzip-path parse proof (TDD) (G5)

> Prove the FULL loader path (gunzip → parse) with a committed REAL `.json.gz` fixture (so `LoadFile`'s
> `GZipStream` is exercised, not just `Parse(JsonElement)`). Plus a skip-gated theory that loads one REAL
> vector file when present.

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/fixtures/m68000-sample.json.gz` (committed gzip binary)
- Create: `tests/CpuEmulator.Tests/TomHarte/fixtures/README.md`
- Modify: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs` (add the gzip-path + real-file tests)
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` (copy the fixture to the output dir)

- [ ] **Step 1: Author + commit the gzip fixture (G5 — REAL gzip bytes).** Create the plaintext, then gzip
  it. Use the SAME case shape Task 2 proved (2 cases is plenty — one `.b`, one `.w` transaction). Generate
  with a deterministic command and record it in the README:
  Run (from the repo root):
  ```bash
  mkdir -p tests/CpuEmulator.Tests/TomHarte/fixtures
  cat > /tmp/m68000-sample.json <<'JSON'
  [
    { "name": "ADD.w fixture",
      "initial": { "d0":1,"d1":2,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                   "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                   "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                   "prefetch":[53328,0],"ram":[[1024,208],[1025,80]] },
      "final":   { "d0":3,"d1":2,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                   "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                   "usp":16384,"ssp":32768,"sr":8192,"pc":1028,
                   "prefetch":[0,1],"ram":[[1024,208],[1025,80]] },
      "transactions":[["r",4,6,1024,".w",53328],["r",4,6,1026,".w",1]] },
    { "name": "CLR.b fixture",
      "initial": { "d0":255,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                   "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                   "usp":0,"ssp":1024,"sr":8192,"pc":2048,
                   "prefetch":[16896,0],"ram":[[2048,66],[2049,0]] },
      "final":   { "d0":0,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                   "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                   "usp":0,"ssp":1024,"sr":8196,"pc":2050,
                   "prefetch":[0,2],"ram":[[2048,66],[2049,0]] },
      "transactions":[["r",4,6,2048,".b",66]] }
  ]
  JSON
  gzip -c /tmp/m68000-sample.json > tests/CpuEmulator.Tests/TomHarte/fixtures/m68000-sample.json.gz
  ```
  > **If `gzip` is unavailable on the box,** generate the fixture with a one-off C# `GZipStream` write (a tiny
  > `dotnet script` or a throwaway `Main`); the only requirement is REAL gzip bytes. Record whichever command
  > you used in the README so the fixture is reproducible.

- [ ] **Step 2: Author the fixtures README.** Create
  `tests/CpuEmulator.Tests/TomHarte/fixtures/README.md`:

```markdown
# 680x0 TomHarte loader fixtures

`m68000-sample.json.gz` — a hand-built 2-case fixture in the SingleStepTests/680x0 schema (separate
usp/ssp, 16-bit sr, 2-word prefetch queue [initial + final], ram as [addr, value] pairs, word-granular
`.b/.w/.l` transactions `[dir, field2, fc, addr, sizeTag, value]`). It exercises the gzip + mnemonic-keyed
loader path WITHOUT requiring the multi-GB upstream vector download. The state transitions are illustrative,
NOT cycle-accurate — M4.4b asserts the PARSE only; execution-green is M4.5.

Regenerate:
    gzip -c m68000-sample.json > m68000-sample.json.gz
(see the plan 2026-06-15-m4-4b-…md Task 4 Step 1 for the source JSON).
```

- [ ] **Step 3: Make the fixture copy to the test output dir.** In
  `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`, add an item so the `.json.gz` is alongside the test
  assembly at runtime (mirror however the project already copies test data — confirm the existing convention
  at Task 1; if none, add):

```xml
  <ItemGroup>
    <None Include="TomHarte/fixtures/m68000-sample.json.gz" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Write the failing gzip-path + real-file tests.** Append to
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs`:

```csharp
    private static string FixturePath() =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory,
            "TomHarte", "fixtures", "m68000-sample.json.gz");

    [Fact]
    public void Loads_the_committed_gzip_fixture()
    {
        var cases = M68000TomHarteLoader.LoadFile(FixturePath());   // exercises GZipStream (G3/G5)
        Assert.Equal(2, cases.Count);
        Assert.Equal("ADD.w fixture", cases[0].Name);
        Assert.Equal(53328u, cases[0].Transactions[0].Value);
        Assert.Equal(".b", cases[1].Transactions[0].SizeTag);      // the CLR.b case's byte transaction
        // The prefetch queue parsed in both initial and final (the new dimension).
        Assert.Equal((ushort)53328, cases[0].Initial.Prefetch[0]);
        Assert.Equal((ushort)1, cases[0].Final.Prefetch[1]);
    }

    [M68000TomHarteTheory]
    [InlineData("ADD.b.json.gz")]   // a representative real file; skipped when vectors are absent
    public void Loads_one_real_vector_file_when_present(string fileName)
    {
        string dir = M68000TomHarteVectors.TryGetVectorDirectory()!;   // non-null (the attribute gates it)
        string path = System.IO.Path.Combine(dir, fileName);
        if (!System.IO.File.Exists(path)) return;   // the exact filename may differ; the parse is the proof
        var cases = M68000TomHarteLoader.LoadFile(path);
        Assert.NotEmpty(cases);
        var first = cases[0];
        Assert.NotNull(first.Name);
        Assert.Equal(2, first.Initial.Prefetch.Length);   // the 2-word prefetch queue
        Assert.Equal(8, first.Initial.D.Length);
        Assert.Equal(7, first.Initial.A.Length);
    }
```

- [ ] **Step 5: Run the tests to verify they fail (then pass after the fixture lands).**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteLoaderTests"`
  Expected: the gzip-fixture test FAILS first if the fixture isn't copied (file-not-found), then PASSES once
  the `.csproj` copy + the committed `.json.gz` are in place. The real-file theory is SKIPPED when vectors are
  absent (the default).

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green; the real-file theory shows as SKIPPED (not failed).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 7: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/fixtures/m68000-sample.json.gz \
        tests/CpuEmulator.Tests/TomHarte/fixtures/README.md \
        tests/CpuEmulator.Tests/TomHarte/M68000TomHarteLoaderTests.cs \
        tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj
git commit -m "$(cat <<'EOF'
test(680x0): prove the gzip loader path with a committed fixture + a skip-gated real-file theory

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 5: The fetch script `tools/get-test-vectors-68000.ps1` (manual proof)

> Mirror `get-test-vectors-z80.ps1`: sparse-checkout `SingleStepTests/680x0` `v1/` into `<dest>/680x0/v1`,
> `$LASTEXITCODE`-checked. **Use the Task-1-confirmed in-repo path** (top-level `v1/` vs `68000/v1/`).

**Files:**
- Create: `tools/get-test-vectors-68000.ps1`

- [ ] **Step 1: Write the script.** Create `tools/get-test-vectors-68000.ps1` (adjust the `sparse-checkout
  set` target + the in-repo `v1` location to the Task-1-confirmed path):

```powershell
#!/usr/bin/env pwsh
# Fetches the SingleStepTests 680x0 v1 vectors via sparse checkout. The 680x0 set is GZIP-compressed,
# MNEMONIC+SIZE-keyed files (ADD.b.json.gz). CONFIRM at fetch time (see plan Task 1): the repo is
# SingleStepTests/680x0; the test set's in-repo path (top-level v1/ vs 68000/v1/) drives the sparse target.
# We cache under <dest>/680x0/v1 so the harness (M68000TomHarteVectors) resolves it like the 6502/Z80.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$vectorDir = Join-Path $Destination "680x0/v1"
if (Test-Path $vectorDir) { Write-Host "680x0 vectors already present at $vectorDir"; exit 0 }

# Clone into a temp sibling, then move v1 into <dest>/680x0/v1. $LASTEXITCODE checks: native commands do
# not trip $ErrorActionPreference, so a failed clone must not report success (the Z80 script's finding).
$clone = Join-Path $Destination "680x0-clone"
if (Test-Path $clone) { Remove-Item -Recurse -Force $clone }
git clone --depth 1 --filter=blob:none --sparse `
    https://github.com/SingleStepTests/680x0.git $clone
if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed (exit $LASTEXITCODE)"; exit 1 }
# Task 1 confirms the in-repo path. If the set is at the repo TOP LEVEL v1/ (the Z80 pattern):
git -C $clone sparse-checkout set v1
if ($LASTEXITCODE -ne 0) { Write-Error "git sparse-checkout failed (exit $LASTEXITCODE)"; exit 1 }
if (-not (Test-Path (Join-Path $clone "v1"))) { Write-Error "clone succeeded but v1/ is missing" }

New-Item -ItemType Directory -Force (Join-Path $Destination "680x0") | Out-Null
Move-Item (Join-Path $clone "v1") $vectorDir
Remove-Item -Recurse -Force $clone
Write-Host "680x0 vectors fetched to $vectorDir"
```

  > **If Task 1 found the files under `68000/v1/` inside the repo** (not top-level `v1/`), change the
  > `sparse-checkout set v1` to `sparse-checkout set 68000/v1` and the `Move-Item` source to
  > `Join-Path $clone "68000/v1"`. The cache DESTINATION stays `<dest>/680x0/v1` (matching the resolver).

- [ ] **Step 2: Manual proof (run it; optional in CI).** If network is available:
  Run: `pwsh tools/get-test-vectors-68000.ps1`
  Expected: clones, sparse-checks-out, and reports `680x0 vectors fetched to …/680x0/v1`. Then re-run the
  real-file theory:
  Run: `dotnet test --filter "FullyQualifiedName~Loads_one_real_vector_file_when_present"`
  Expected: the theory now RUNS (not skipped) and PASSES (the real `ADD.b.json.gz` parses; case count > 0,
  prefetch length 2). **If the real filename differs from `ADD.b.json.gz`, update the `InlineData` to a name
  that exists** (the test's `File.Exists` guard already no-ops a missing name, so the suite stays green either
  way).

- [ ] **Step 3: Full gate.**
  Run: `dotnet test` → all green (the real-file theory skipped if you did not fetch).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 4: Commit.**

```bash
git add tools/get-test-vectors-68000.ps1
git commit -m "$(cat <<'EOF'
feat(test): add the 680x0 TomHarte vector fetch script (sparse-checkout to <cache>/680x0/v1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** 0 (manual proof; the parse proof is Task 4).

---

### Task 6: The runner SCAFFOLD — set the full state, no Step, TODO(M4.5) (TDD)

> Build the runner that M4.5 fills in: a fresh `M68000Cpu` over a tracing wide BE bus, the FULL initial state
> set (32-bit regs, usp/ssp, sr, pc, prefetch, ram). In M4.4b `RunCase` sets state + returns a TODO(M4.5)
> sentinel (no Step, no diff — the op bodies don't exist). Proven by a test that the scaffold sets state
> WITHOUT throwing (the state-set path is the M4.5-ready half).

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`
- Test: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunnerScaffoldTests.cs` (create)

  > **Confirm the `M68000Cpu` ctor + SetRegister surface at Task 1.** The Z80 runner does
  > `new Z80Cpu(bus, io)` + `cpu.SetRegister("PC", …)`. The 68000 has one wide bus (no separate I/O space).
  > Confirm `new M68000Cpu(bus)` (or its actual signature) + that `SetRegister("D0"/"A0"/"USP"/"SSP"/"PC"/
  > "SR", …)` exist. If `usp`/`ssp` are not settable by name (they may be the banked A7 view), set them via
  > whatever M4.1 exposed (read `M68000Cpu.cs`); record the accessor in the runner.

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunnerScaffoldTests.cs`:

```csharp
using System.Text.Json;
using CpuEmulator.Tests.TomHarte;
using Xunit;

public class M68000TomHarteRunnerScaffoldTests
{
    private const string OneCase = """
        [{
          "name": "scaffold case",
          "initial": { "d0":1,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                       "prefetch":[53328,0],"ram":[[1024,208]] },
          "final":   { "d0":1,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                       "prefetch":[53328,0],"ram":[[1024,208]] },
          "transactions": []
        }]
        """;

    [Fact]
    public void Scaffold_sets_full_state_and_reports_not_yet_executed()
    {
        using var doc = JsonDocument.Parse(OneCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());
        // M4.4b: the scaffold sets state without throwing and returns the not-yet-executed sentinel
        // (no op bodies → no Step → no assertion; M4.5 replaces the sentinel with the real diff).
        string result = M68000TomHarteRunner.RunCase(c);
        Assert.Equal(M68000TomHarteRunner.NotYetExecuted, result);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteRunnerScaffoldTests"`
  Expected: FAIL — `M68000TomHarteRunner` does not exist.

- [ ] **Step 3: Implement the scaffold.** Create
  `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`. **Match the ctor + SetRegister to the Task-1
  confirmed surface:**

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace (confirm the namespace at Task 1)

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 680x0 TomHarte runner SCAFFOLD. In M4.4b it builds a fresh M68000Cpu over a tracing wide BE bus and
/// sets the FULL initial state (32-bit D/A, usp/ssp, sr, pc, prefetch, ram) — then returns the NotYetExecuted
/// sentinel WITHOUT Stepping (the op bodies are M4.5). M4.5 replaces the sentinel body with: Step once, then
/// diff registers + ram + the per-transaction trace + the FINAL prefetch queue (the new dimension). The
/// state-set half built here is the M4.5-ready scaffold.
/// </summary>
internal static class M68000TomHarteRunner
{
    public const string NotYetExecuted = "M4.4b scaffold: state set, not executed (op bodies are M4.5)";

    public static string RunCase(M68000TomHarteCase c)
    {
        // Wide big-endian program bus (M4.2). 24-bit address space; map the full range writable.
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 24);
        inner.MapMemory(0x000000, new byte[0x1000000], writable: true);
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address, e.Value);
        var bus = new TracingAddressSpace(inner);

        var cpu = new M68000Cpu(bus);       // confirm ctor at Task 1 (one wide bus, no separate I/O)
        var s = c.Initial;
        for (int i = 0; i < 8; i++) cpu.SetRegister($"D{i}", s.D[i]);
        for (int i = 0; i < 7; i++) cpu.SetRegister($"A{i}", s.A[i]);
        cpu.SetRegister("USP", s.Usp);      // confirm USP/SSP are settable by name (M4.1) — else the M4.1 path
        cpu.SetRegister("SSP", s.Ssp);
        cpu.SetRegister("PC", s.Pc);
        cpu.SetRegister("SR", s.Sr);
        // NOTE: the 2-word prefetch queue (s.Prefetch) is carried but NOT wired into the CPU here — the
        // M68000Cpu prefetch-queue mechanism is M4.5. M4.5 will: set the initial prefetch, Step, and assert
        // the final prefetch (c.Final.Prefetch). The runner already carries it (c.Initial/Final.Prefetch).

        // M4.4b: do NOT Step (no op bodies) and do NOT diff. Return the sentinel.
        // TODO(M4.5): replace with — cpu.Step(); then diff D/A/usp/ssp/sr/pc + ram + bus.Trace +
        //             the final prefetch queue against c.Final / c.Transactions.
        _ = bus;   // the tracing bus is wired so M4.5's per-transaction diff has the trace ready.
        return NotYetExecuted;
    }
}
```

  > **`TracingAddressSpace` namespace + `AddressSpace` ctor + `addressBits: 24`** — confirm at Task 1 (the Z80
  > runner uses `CpuEmulator.Tests.Mos6502.TracingAddressSpace` and `addressBits: 16`; the 68000 is 24-bit per
  > ADR 0002). If a 16MB `byte[]` allocation per case is too heavy for the scaffold test, map a smaller window
  > around the case's RAM addresses instead (the Z80 runner maps the full 64K — for 24-bit, prefer mapping
  > only the touched pages; confirm `AddressSpace.MapMemory` supports a sub-range). For the scaffold's single
  > case this is fine; M4.5's sweep will want the page-windowed approach.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000TomHarteRunnerScaffoldTests"`
  Expected: PASS — the scaffold sets state without throwing and returns the sentinel.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 6: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs \
        tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunnerScaffoldTests.cs
git commit -m "$(cat <<'EOF'
test(680x0): add the TomHarte runner scaffold (sets full state; Step + diff are M4.5)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

## Final gate + PR (M4.4b)

- [ ] **Full suite:** `dotnet test` — 0 failures; the new loader/resolver/fixture/scaffold tests green; the
  real-file theory SKIPPED (not failed) when vectors absent; the whole 6502/Z80 suites + both
  `RegeneratedSpecTests` byte-identical. Record the new total.
- [ ] **Warnings-as-errors:** `dotnet build --no-incremental -warnaserror` — clean.
- [ ] **Byte-identity:** `git status` shows ONLY the new 680x0 loader/runner/vectors/fixture/script files +
  the `.csproj` fixture-copy line. No 6502/Z80 loader/runner/script touched; no spec/importer touched.
- [ ] **Docs:** update `docs/user-guide/testing.md` to document the 680x0 TomHarte harness (the gzip +
  mnemonic-keyed loader, the `get-test-vectors-68000.ps1` fetch, the `680x0/v1` cache, the prefetch-queue +
  `.b/.w/.l` transactions, and the honest state: **parse-proven, execution-green is M4.5**).
- [ ] **PR:** open against `main`. Body includes a **Docs Impact** section (`testing.md`) + the Task-1 pinned
  schema facts (the field-2 decode result) + the honest close-state: "the 680x0 loader PARSES (fixture +
  skip-gated real file); NO opcode is asserted green; the Step + the per-transaction/final-prefetch diff are
  M4.5."

---

## Self-review notes (Planner)

- **Spec coverage (task brief §Scope item 3):** the gzip mnemonic-keyed loader → Task 2; the prefetch queue
  (initial + final) → Tasks 2/4/6 (parsed + carried; final assertion deferred to M4.5); the transactions
  `.b/.w/.l` + field-2 decode → Tasks 1/2 (G1); the `get-test-vectors-68000.ps1` (cache `680x0/v1`) → Task 5;
  the skip-when-absent attribute → Task 3; the PARSE proof → Task 4 (fixture + real-file); the runner scaffold
  → Task 6. M4.4b builds + parses; asserts NO opcode green (M4.5).
- **Placeholder scan:** every code step carries literal code; the ONE genuinely-unknown (the transactions
  field-2 meaning + the exact in-repo vector path) is pinned by the Task-1 upstream recon and the loader is
  written position-defensively so it parses losslessly regardless — flagged to the Coordinator below.
- **Type consistency:** `M68000TomHarteCase`/`M68000State`/`M68000Transaction`/`M68000Ram`/
  `M68000TomHarteLoader`/`M68000TomHarteVectors`/`M68000TomHarteTheoryAttribute`/`M68000TomHarteRunner` used
  consistently; `Parse(JsonElement)` (schema-half, Task 2) vs `LoadFile(path)` (gzip-half, Task 4) are the two
  loader entry points the tests use; `RunCase`/`NotYetExecuted` consistent (Task 6).

### Decisions (the Coordinator-facing record)

- **The gzip wrapper is the only loader-core delta (G3);** everything else mirrors `Z80TomHarteLoader`.
- **The transactions field-2 element is carried RAW (`Field2`/int)** — the loader resolves the tuple
  POSITION-by-position and tolerates length variation, so the parse is lossless even with field 2's semantics
  documented-as-uncertain (the ADR-flagged ambiguity). Task 1's upstream recon pins the meaning; the loader
  does not depend on it.
- **The runner is a SCAFFOLD (state-set only, no Step);** M4.5 replaces the `NotYetExecuted` sentinel body
  with the Step + the full diff (incl. the FINAL prefetch-queue assertion — the new dimension). This keeps
  M4.4b honestly free of any execution assertion.
- **The committed gzip FIXTURE is the always-on parse proof** (no multi-GB download needed in CI); the
  skip-gated real-file theory is the confirmation when vectors are fetched.
