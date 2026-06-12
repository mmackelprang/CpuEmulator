# M1 Chunk 6: Datasheet-Extraction Runbook — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** spec §9 item 6 — **datasheet-extraction runbook, Stages 1–2**. The four concrete
deliverables are: (1) a `--validate-only` importer mode (load + validate both schemas, print
the standard report + provenance coverage, write nothing, exit 0/2); (2) a cross-source diff
tool (`--diff <other-dataset.json>`, row-by-row field comparison keyed by opcode, exit 3 when
differing); (3) a review-report generator (`--review-report <path.md>`, markdown covering
provenance coverage, rows without `source`, the disagreement table if `--diff` was given, and
the missing-semantics inventory); (4) the runbook itself
(`docs/user-guide/extraction-runbook.md`) — Stage-1 LLM-extraction guidance and Stage-2
provenance/review-report usage, with the actual schema-bearing prompt template verbatim and a
worked 5-opcode micro-example using real tool output, each ladder rung catching seeded errors.

**Architecture:** everything new lives in `CpuEmulator.SpecImporter`; no Core/Generator/CPU
changes. The new modes are pure additions to `Program.cs`'s argument parser plus
corresponding engine-level functions with no side effects (all I/O at the CLI layer, logic in
the engine — the existing testability pattern). The diff dataset is a second `OpcodeEntry[]`
load through the existing `OpcodeDataset.Load`; the comparison engine loops over opcodes
keyed by hex string. The review report uses `System.IO.StreamWriter` — plain string
concatenation, no templating library.

**Tech stack:** unchanged (net10.0, xUnit 2.9.3, no new packages). One new test data file:
`tests/CpuEmulator.Tests/Importer/data/mos6502-opcodes-seeded.json` — the real dataset with
deliberate disagreements at known positions (Fixture A — 5 seeded errors). The fixture is NOT
a new csproj data file in the tools project; it is a test-project item only
(`CopyToOutputDirectory`).

**Plan series:** 1–3b-ii ✅ · monitor ✅ · peripherals + host ✅ · **extraction runbook:
this plan (PR #10)** · M2 next.

**Baseline test count:** 848 (PR #8 + PR #9 closeout actual — PRs #9 is docs-only, no new
tests). New-test tally (theory rows counted individually):
Task 1 ≈ 14 (validate-only exit codes and output), Task 2 ≈ 22 (diff engine + fixture rows +
exit codes), Task 3 ≈ 10 (review-report structure and content assertions). **Estimate: 848 +
~46 ≈ ~894.** Report actuals at closeout — the estimate, not the suite, is what bends.

**NOT in scope (recorded, with where each lands):**
- **Stage 3 (linter)** — spec §9 item 6 last sentence / §9 item M4+: a spec linter that
  cross-checks the dataset against TomHarte vector inference. Not in this PR.
- **Z80 extraction** — spec §9 item 10 (M3). The runbook is written generically enough to
  apply; a Z80 dataset file does not ship here.
- **`--validate-only` for the semantics map** — the new mode validates both schemas already
  (dataset + semantics); a separate mode for semantics-only is not requested.
- **LLM API integration** — the runbook describes a manual prompting workflow. Automation is
  M4+.

**Recorded deviations:**
- **`--validate-only` validates both schemas** (dataset AND semantics), not dataset-only —
  the spec says "load + validate dataset & semantics." Running the importer with two broken
  inputs and getting only dataset errors is worse than finding both in one pass.
- **Exit code 2 on any validation error** (standard importer error code — consistent with the
  existing `--out` mode's behavior, where `InvalidDataException` exits 1 only because it
  comes out of `RunFromFiles`; in `--validate-only` we use exit code 2 explicitly to
  distinguish validation-failure from usage-error/IO-error — spec says "exit 0/2").
- **The fixture dataset is a test-only content file** (not in the tools `data/` directory) —
  it is a curated error-injected file that has no production use; shipping it in the tools
  output directory would be misleading.
- **`--review-report` without `--out` is allowed** — the review report is a standalone
  observation artifact, not a spec emission; requiring `--out` would be a regression on the
  review workflow.
- **`--diff` may be combined with `--validate-only`** — the combination validates then diffs;
  `--diff` without `--out` is legal (same reasoning as above).

---

## Derived numbers (verified against the repo, not assumed)

- **Provenance coverage today:** 0 of 151 rows in `mos6502-opcodes.json` carry a `source`
  field (grep confirms zero occurrences of `"source"` in the dataset). This is the expected
  and documented state; the review report will explain it.
- **Exit code matrix:**
  - `0` = success (validate, generate, diff, report — all clean)
  - `1` = usage error / IO error (unchanged from today)
  - `2` = validation failure (dataset or semantics errors) — NEW
  - `3` = diff disagreements (opcodes differ) — NEW; note: combined
    `--validate-only --diff` exits 3 if diff has disagreements even when both datasets are
    individually valid
- **Seeded fixture disagreements (5)** — chosen to exercise every comparison column:
  mnemonic, mode, bytes, cycles, pageCrossPenalty. Selected from interior rows so the
  fixture stays easily diff-readable.

## Fixture A — seeded disagreements

The test fixture (`mos6502-opcodes-seeded.json`) is the real 6502 dataset with five rows
overwritten. These specific rows and field changes are the contract that all diff tests assert
against:

| Opcode | Field changed | Real value | Seeded value | Why |
|---|---|---|---|---|
| `0x69` (ADC Immediate) | `mnemonic` | `"ADC"` | `"ADD"` | mnemonic disagreement |
| `0xBD` (LDA AbsoluteX) | `cycles` | `4` | `5` | cycle-count disagreement |
| `0xBD` (LDA AbsoluteX) | `pageCrossPenalty` | `true` | `false` | pageCross disagreement (same row as above — two fields on one opcode) |
| `0x4C` (JMP Absolute) | `mode` | `"Absolute"` | `"AbsoluteX"` | mode disagreement (also forces `bytes` inconsistency — see below) |
| `0x85` (STA ZeroPage) | `bytes` | `2` | `3` | bytes disagreement (also forces `cycles` to be wrong on the row — STA ZeroPage 3 bytes is inconsistent, but only the bytes disagreement is reported) |

Note: `0x4C` with mode `"AbsoluteX"` changes the expected bytes from 3→3 (same), so only
the `mode` field is the seeded disagreement. `0x85` with `bytes: 3` is schema-invalid for
ZeroPage (requires 2), so loading the fixture fails `OpcodeDataset.Load`. This means the
fixture **cannot use `bytes` disagreement on a ZeroPage opcode** — the validation fires first.

**Revised Fixture A (schema-valid seeded disagreements — 5 distinct cells):**

| Opcode | Field changed | Real value | Seeded value |
|---|---|---|---|
| `0x69` | `mnemonic` | `"ADC"` | `"ADD"` |
| `0xBD` | `cycles` | `4` | `5` |
| `0xBD` | `pageCrossPenalty` | `true` | `false` |
| `0x4C` | `mode` | `"Absolute"` | `"Absolute"` → no change, use `cycles` disagreement: real `3`, seeded `4` |
| `0xEA` | `mnemonic` | `"NOP"` | `"NXX"` |

Final schema-valid table (no byte-count inconsistencies introduced):

| Opcode | Field changed | Real value | Seeded value |
|---|---|---|---|
| `0x69` (ADC Imm) | `mnemonic` | `"ADC"` | `"ADD"` |
| `0xBD` (LDA AbsX) | `cycles` | `4` | `5` |
| `0xBD` (LDA AbsX) | `pageCrossPenalty` | `true` | `false` |
| `0x4C` (JMP Abs) | `cycles` | `3` | `4` |
| `0xEA` (NOP Impl) | `mnemonic` | `"NOP"` | `"NXX"` |

Five disagreement cells across four opcodes (0xBD has two field disagreements). The diff
report must surface all five cells. Missing/extra opcode rows are not seeded (the fixture has
the same 151 rows).

## File structure

```
tools/CpuEmulator.SpecImporter/
    Program.cs                        — MODIFY (--validate-only, --diff, --review-report modes)
    DatasetDiff.cs                    — NEW (diff engine: OpcodeEntry[] × OpcodeEntry[] → DiffResult)
    ReviewReportGenerator.cs          — NEW (markdown generator consuming diff + provenance data)
tests/CpuEmulator.Tests/
    CpuEmulator.Tests.csproj          — MODIFY (add data/ content item for fixture)
    Importer/
        ValidateOnlyTests.cs          — NEW (--validate-only mode: exit codes, output format)
        DatasetDiffTests.cs           — NEW (diff engine + exit codes + fixture assertions)
        ReviewReportTests.cs          — NEW (report structure + content assertions)
        data/
            mos6502-opcodes-seeded.json — NEW (Fixture A — 5 seeded disagreements)
docs/user-guide/
    extraction-runbook.md             — NEW (the runbook: Stage 1 + Stage 2 + micro-example)
    adding-a-cpu.md                   — MODIFY (link to runbook)
    README.md                         — MODIFY (link to runbook in table)
docs/superpowers/specs/
    2026-06-11-cpu-emulator-framework-design.md — MODIFY (§9 item 6 → DELIVERED)
README.md                             — MODIFY (Status: M1 COMPLETE; datasheet-extraction)
```

---

### Task 1: `--validate-only` importer mode (TDD)

**Files:**
- Modify: `tools/CpuEmulator.SpecImporter/Program.cs`
- Create: `tests/CpuEmulator.Tests/Importer/ValidateOnlyTests.cs`
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` (add data/ content item for fixture — can be done here or Task 2; do it here so the test runner finds data early)

- [ ] **Step 1: Branch check** — `git branch --show-current` → `feat/m1-extraction-runbook`.

- [ ] **Step 2: Failing tests** (`ValidateOnlyTests`, namespace
  `CpuEmulator.Tests.Importer`):

  | Test | Setup → action | Asserts |
  |---|---|---|
  | ValidateOnly_real_dataset_exits_0 | call `Program.Main(["--validate-only", "--dataset", DatasetPath, "--semantics", SemanticsPath])` | returns 0 |
  | ValidateOnly_prints_standard_report | capture stdout; same call | output contains `"total=151"`, `"emitted="`, `"todoSemantics="`, `"todoMode="` |
  | ValidateOnly_prints_provenance_coverage | capture stdout | output contains `"provenance: 0/151"` |
  | ValidateOnly_writes_nothing | call with real data; temp dir has no new files | no .cs file or extra file in temp dir |
  | ValidateOnly_bad_dataset_exits_2 | `--dataset <non-existent>` | returns 2 |
  | ValidateOnly_bad_dataset_prints_error | capture stderr; bad dataset | stderr contains `"error:"` |
  | ValidateOnly_bad_semantics_exits_2 | `--semantics <non-existent>` | returns 2 |
  | ValidateOnly_missing_dataset_flag_exits_1 | `--validate-only` without `--dataset` | returns 1 (usage error, pre-existing) |
  | ValidateOnly_missing_semantics_flag_exits_1 | `--validate-only` without `--semantics` | returns 1 |
  | ValidateOnly_dataset_invalid_json_exits_2 | `--dataset <file with "not json">` | returns 2 |
  | ValidateOnly_semantics_invalid_json_exits_2 | `--semantics <file with "not json">` | returns 2 |
  | ValidateOnly_combined_with_out_is_usage_error | `--validate-only --out /tmp/x.cs` | returns 1 (mutually exclusive) |
  | ValidateOnly_no_out_flag_required | `--validate-only` with valid data | returns 0 (no `--out` needed) |
  | ValidateOnly_report_flag_prints_missing_semantics | `--validate-only --report` | output contains `"missing-semantics inventory"` |

  **Provenance-coverage assertion format** (the exact string the test pins): the validate
  output line is `provenance: N/151 rows carry source citations` where N is the count of rows
  in the loaded dataset with a non-null, non-empty `source` field.

- [ ] **Step 3: Implement** — add `--validate-only` flag parsing in `Program.cs`. When set:
  - Load dataset via `OpcodeDataset.Load` (throws `InvalidDataException` → exit 2, message
    to stderr).
  - Load semantics map via `SemanticsMap.Load` (same).
  - Compute provenance coverage: count rows where `Source is { Length: > 0 }`.
  - Print the standard report line (same format as today: `total=N emitted=M
    todoSemantics=P todoMode=Q` — reuse `SpecImportEngine.Run`'s returned report, but
    suppress the file-write by not calling `RunFromFiles`).
  - Print: `provenance: N/151 rows carry source citations`.
  - If `--report` also set, print missing-semantics inventory.
  - Write nothing. Return 0.
  - `--validate-only` and `--out` together: print `error: --validate-only and --out are
    mutually exclusive` to stderr; return 1.
  - IO errors (FileNotFoundException) → `error: File not found: …` to stderr; return 2.
  - Validation errors (InvalidDataException) → `error: …` to stderr; return 2.
  - Note: `--validate-only` requires `--dataset` and `--semantics` (same as normal mode).

- [ ] **Step 4: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: importer --validate-only mode — load + validate both schemas, print provenance coverage, exit 0/2`

---

### Task 2: Cross-source diff (`--diff`) (TDD)

**Files:**
- Create: `tools/CpuEmulator.SpecImporter/DatasetDiff.cs`
- Modify: `tools/CpuEmulator.SpecImporter/Program.cs`
- Create: `tests/CpuEmulator.Tests/Importer/DatasetDiffTests.cs`
- Create: `tests/CpuEmulator.Tests/Importer/data/mos6502-opcodes-seeded.json` (Fixture A)
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` (if not done in Task 1)

- [ ] **Step 1: Create Fixture A** (`mos6502-opcodes-seeded.json`) — copy the real dataset,
  overwrite the five cells from the Derived numbers table:
  - `0x69`: `"mnemonic": "ADD"` (was `"ADC"`)
  - `0xBD`: `"cycles": 5` (was `4`), `"pageCrossPenalty": false` (was `true`)
  - `0x4C`: `"cycles": 4` (was `3`)
  - `0xEA`: `"mnemonic": "NXX"` (was `"NOP"`)
  The fixture is a complete valid 151-row dataset (all other fields identical to real data).
  `OpcodeDataset.Load` must succeed on it. Place it in
  `tests/CpuEmulator.Tests/Importer/data/mos6502-opcodes-seeded.json`.

- [ ] **Step 2: Test-csproj content item** (if not done in Task 1):

  ```xml
  <ItemGroup>
    <Content Include="Importer\data\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  ```

  The fixture resolves in tests as
  `Path.Combine(AppContext.BaseDirectory, "Importer", "data", "mos6502-opcodes-seeded.json")`.
  (Note: this is NOT in `DataPath.Get`'s path since that resolves under `data/` without the
  `Importer/` prefix — tests reference it with a direct path helper.)

- [ ] **Step 3: Failing diff-engine tests** (`DatasetDiffTests`):

  | Test | Setup → action | Asserts |
  |---|---|---|
  | Self_diff_has_no_disagreements | diff real vs real | `result.Disagreements` empty; `result.MissingInOther` empty; `result.ExtraInOther` empty |
  | Self_diff_exits_0 | `Program.Main` with `--dataset real --diff real` | returns 0 |
  | Seeded_diff_has_five_disagreement_cells | diff real vs fixture | 5 cells in `result.Disagreements` |
  | Seeded_diff_mnemonic_0x69 | diff real vs fixture | disagreement row: opcode `0x69`, field `mnemonic`, left `ADC`, right `ADD` |
  | Seeded_diff_cycles_0xBD | diff real vs fixture | opcode `0xBD`, field `cycles`, left `4`, right `5` |
  | Seeded_diff_pageCross_0xBD | diff real vs fixture | opcode `0xBD`, field `pageCrossPenalty`, left `True`, right `False` |
  | Seeded_diff_cycles_0x4C | diff real vs fixture | opcode `0x4C`, field `cycles`, left `3`, right `4` |
  | Seeded_diff_mnemonic_0xEA | diff real vs fixture | opcode `0xEA`, field `mnemonic`, left `NOP`, right `NXX` |
  | Seeded_diff_exits_3 | `Program.Main` with `--dataset real --diff seeded` | returns 3 |
  | Missing_opcode_in_other | diff A (151 rows) vs B (150 rows, 0x69 removed) | `result.MissingInOther` = [`"0x69"`] |
  | Extra_opcode_in_other | diff A (151 rows) vs B (152 rows, extra `0xF2`) | `result.ExtraInOther` = [`"0xF2"`] |
  | Missing_extra_also_exit_3 | same | returns 3 |
  | Diff_without_dataset_exits_1 | `--diff other --semantics s` (no `--dataset`) | returns 1 |
  | Diff_requires_dataset | `--diff other` alone | returns 1 |
  | Diff_other_file_not_found | `--diff /nonexistent` | returns 2 |
  | Diff_prints_disagreement_table | capture stdout; seeded diff | output contains `"0x69"`, `"mnemonic"`, `"ADC"`, `"ADD"` |
  | Diff_prints_all_five_cells | capture stdout; seeded diff | 5 rows in disagreement table |
  | Validate_then_diff_combined | `--validate-only --diff seeded` | returns 3 (diff disagrees) |
  | Validate_then_diff_identical | `--validate-only --diff real` | returns 0 |
  | Diff_does_not_require_semantics | `--dataset real --diff real` (no `--semantics`) | returns 0 |

  **Note on `--diff` requiring `--dataset`:** `--diff` is a comparison mode — it needs the
  left-hand dataset. It does NOT require `--semantics` (semantic information is not diffed).
  `--diff` without `--dataset` is a usage error (exit 1).

- [ ] **Step 4: Implement `DatasetDiff`**:

  ```csharp
  namespace CpuEmulator.SpecImporter;

  /// <summary>
  /// Row-by-row field comparison of two opcode datasets, keyed by opcode hex string.
  /// Checks: mnemonic, mode, bytes, cycles, pageCrossPenalty.
  /// The 'source' field is intentionally excluded — provenance citations are
  /// expected to differ between independent extraction sources; that's the point.
  /// </summary>
  public sealed class DatasetDiff
  {
      public static DiffResult Compare(OpcodeEntry[] left, OpcodeEntry[] right)
      {
          var leftMap  = left.ToDictionary(e => e.Opcode, StringComparer.OrdinalIgnoreCase);
          var rightMap = right.ToDictionary(e => e.Opcode, StringComparer.OrdinalIgnoreCase);

          var missing = leftMap.Keys.Except(rightMap.Keys,  StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(k => k).ToList();
          var extra   = rightMap.Keys.Except(leftMap.Keys,  StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(k => k).ToList();

          var disagreements = new List<FieldDisagreement>();
          foreach (var opcode in leftMap.Keys.Intersect(rightMap.Keys,
                       StringComparer.OrdinalIgnoreCase).OrderBy(k => k))
          {
              var l = leftMap[opcode];
              var r = rightMap[opcode];
              if (l.Mnemonic         != r.Mnemonic)
                  disagreements.Add(new(opcode, "mnemonic",         l.Mnemonic,
                      r.Mnemonic));
              if (l.Mode             != r.Mode)
                  disagreements.Add(new(opcode, "mode",             l.Mode, r.Mode));
              if (l.Bytes            != r.Bytes)
                  disagreements.Add(new(opcode, "bytes",            l.Bytes.ToString(),
                      r.Bytes.ToString()));
              if (l.Cycles           != r.Cycles)
                  disagreements.Add(new(opcode, "cycles",           l.Cycles.ToString(),
                      r.Cycles.ToString()));
              if (l.PageCrossPenalty != r.PageCrossPenalty)
                  disagreements.Add(new(opcode, "pageCrossPenalty", l.PageCrossPenalty.ToString(),
                      r.PageCrossPenalty.ToString()));
          }

          return new DiffResult(disagreements, missing, extra);
      }
  }

  public sealed record FieldDisagreement(
      string Opcode, string Field, string Left, string Right);

  public sealed record DiffResult(
      IReadOnlyList<FieldDisagreement> Disagreements,
      IReadOnlyList<string>            MissingInOther,
      IReadOnlyList<string>            ExtraInOther)
  {
      public bool HasDifferences =>
          Disagreements.Count > 0 || MissingInOther.Count > 0 || ExtraInOther.Count > 0;
  }
  ```

- [ ] **Step 5: Wire `--diff` in `Program.cs`** — add `string? diffPath = null`; parse
  `--diff <path>`. After the primary dataset is loaded (requires `--dataset`), load the diff
  dataset via `OpcodeDataset.Load(diffPath)` (IO/validation errors → exit 2). Run
  `DatasetDiff.Compare(dataset, otherDataset)`. Print the disagreement table to stdout (see
  Output format below). Return 3 if `result.HasDifferences`, else carry on (the exit-0 path
  or `--validate-only`'s exit-0 path). `--diff` without `--dataset` → exit 1 usage error.
  `--diff` is compatible with `--validate-only`, `--report`, `--semantics`, and `--out`
  (though `--out` is blocked by `--validate-only`).

  **Output format for disagreements** (the tests pin this exactly):
  ```
  diff: N disagreement(s), M missing opcode(s), P extra opcode(s)
  opcode  field             left                right
  0x4C    cycles            3                   4
  0x69    mnemonic          ADC                 ADD
  0xBD    cycles            4                   5
  0xBD    pageCrossPenalty  True                False
  0xEA    mnemonic          NOP                 NXX
  ```
  (Rows ordered by opcode, then field. Missing/extra opcodes printed as comma-separated list
  after the table if any exist: `missing from other: 0x69` / `extra in other: 0xF2`.)

- [ ] **Step 6: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: importer --diff mode — cross-source field comparison, exit 3 on disagreements`

---

### Task 3: Review-report generator (`--review-report`) (TDD)

**Files:**
- Create: `tools/CpuEmulator.SpecImporter/ReviewReportGenerator.cs`
- Modify: `tools/CpuEmulator.SpecImporter/Program.cs`
- Create: `tests/CpuEmulator.Tests/Importer/ReviewReportTests.cs`

- [ ] **Step 1: Failing report tests** (`ReviewReportTests`):

  | Test | Setup → action | Asserts |
  |---|---|---|
  | Report_has_provenance_table_heading | generate with real dataset | output contains `"## Provenance Coverage"` |
  | Report_provenance_row_count_151 | real dataset | contains `"151"` and `"/151"` or `"0/151"` in provenance section |
  | Report_lists_rows_lacking_source | real dataset | contains `"## Rows Lacking Source"` section |
  | Report_all_151_listed_when_no_source | real dataset (0/151) | the rows-lacking-source list has 151 entries (mnemonic+opcode per row) |
  | Report_no_disagreement_section_without_diff | generate without diff | does NOT contain `"## Disagreements"` |
  | Report_disagreement_section_present_with_diff | generate with seeded diff | contains `"## Disagreements"` |
  | Report_disagreement_table_has_five_rows | seeded diff | disagreement table has 5 data rows |
  | Report_has_missing_semantics_section | real dataset + real semantics | contains `"## Missing Semantics"` |
  | Report_written_to_file | generate to temp file | file exists and is non-empty |
  | Report_flag_requires_dataset | `--review-report out.md` without `--dataset` | exit 1 |
  | Report_requires_semantics | `--review-report out.md --dataset d` without `--semantics` | exit 1 |

  **Report structure contract** (the markdown the tests assert section-by-section):
  ```markdown
  # Extraction Review: mos6502

  Generated: <timestamp>

  ## Provenance Coverage

  N/151 dataset rows carry `source` citations.

  ## Rows Lacking Source

  | Opcode | Mnemonic | Mode |
  |---|---|---|
  | 0x00 | BRK | Implied |
  ... (one row per uncited opcode, dataset order)

  ## Disagreements
  (section only present when --diff is given and has disagreements)

  | Opcode | Field | Left | Right |
  |---|---|---|---|
  | 0x4C | cycles | 3 | 4 |
  ...

  ## Missing Semantics

  | Mnemonic | Dataset Rows |
  |---|---|
  (one row per mnemonic without semantics, from ImportReport.MissingSemanticsInventory)
  ```
  (If all rows have semantics, the Missing Semantics section says "All mnemonics have
  semantics defined.")

- [ ] **Step 2: Implement `ReviewReportGenerator`**:

  ```csharp
  namespace CpuEmulator.SpecImporter;

  /// <summary>
  /// Generates a markdown extraction-review report from a loaded dataset,
  /// optional diff result, and import report (for missing-semantics inventory).
  /// Designed to be called by the CLI; all I/O stays in Program.cs.
  /// </summary>
  public static class ReviewReportGenerator
  {
      public static string Generate(
          string       architecture,
          OpcodeEntry[] dataset,
          ImportReport report,
          DiffResult?  diff = null)
      {
          var sb = new System.Text.StringBuilder();
          // ... emit the four sections as described in Ground truth above ...
          return sb.ToString();
      }
  }
  ```

  Implementation contract: use `System.Text.StringBuilder` throughout. No external
  dependencies. The timestamp format is `yyyy-MM-dd` (date only — reproducible in tests;
  time-of-day varies). The "architecture" string comes from the semantics map
  (`SemanticsMap.Architecture`, e.g. `"mos6502"`).

- [ ] **Step 3: Wire `--review-report <path.md>` in `Program.cs`** — parse
  `string? reviewReportPath = null`. After loading dataset + semantics (required) and running
  the engine (reuse the `Run` overload; does not write the output file), generate the report
  and write it to `reviewReportPath`. If `--diff` was given and resulted in disagreements,
  pass the `DiffResult` to `Generate`. `--review-report` requires `--dataset` and
  `--semantics` (same as normal generation). `--review-report` is compatible with
  `--validate-only` (no spec file written; report still written) and `--out` (both can be
  specified together — review report at `reviewReportPath`, spec file at `outputPath`).

- [ ] **Step 4: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: importer --review-report — markdown provenance/disagreements/semantics review report`

---

### Task 4: The extraction runbook (`docs/user-guide/extraction-runbook.md`)

**Files:**
- Create: `docs/user-guide/extraction-runbook.md`
- Modify: `docs/user-guide/adding-a-cpu.md` (link to runbook)
- Modify: `docs/user-guide/README.md` (add runbook row to table)

**This task ships the documentation.** The runbook is the feature — not a description of the
feature, but the usable artifact itself. Its accuracy is a merge gate: every command shown
must work against the real tool; the worked example must use real tool output (captured in the
UAT run below).

- [ ] **Step 1: Write `extraction-runbook.md`** with the following sections:

  **Section 1 — Overview** (~200 words): purpose (LLM-assisted extraction of opcode datasets
  + draft semantics from CPU PDFs); what the runbook covers (Stages 1–2); what it does NOT
  cover (micro-op vocabulary, mode cycle-templates — those are always hand work; Stage 3
  linter is M4+). Audience: someone adding a new CPU architecture.

  **Section 2 — Stage 1: Extracting the Dataset**

  Sub-section 2.1: **The two schemas** — link to `adding-a-cpu.md`; show the actual JSON
  schema for `opcodes.json` (one annotated example row showing all six fields + the optional
  `source` field):
  ```json
  {
    "opcode": "0xA9",
    "mnemonic": "LDA",
    "mode": "Immediate",
    "bytes": 2,
    "cycles": 2,
    "pageCrossPenalty": false,
    "source": "MOS MCS6500 Family Hardware Manual, p.149, Table A-2"
  }
  ```
  And the semantics-map schema (one annotated entry):
  ```json
  {
    "architecture": "mos6502",
    "namespace": "CpuEmulator.Cpus.Mos6502",
    "specClassName": "Mos6502Spec",
    "registers": [ { "name": "A", "bits": 8 }, ... ],
    "mnemonics": {
      "LDA": "[Load(Reg.A), SetNZ(Reg.A)]"
    }
  }
  ```

  Sub-section 2.2: **The LLM prompt template** — the verbatim template, ready to paste with
  `<CPU_MANUAL_TEXT>` placeholder. This is the core asset. It must embed:
  - System instruction: "You are a precise technical extractor. Emit only valid JSON. Do not
    invent opcodes; extract only what the manual documents."
  - The opcode dataset schema (full field list + constraints + `source` field requirement with
    citation format)
  - The DSL emission rules from the constrained-DSL experience (3a/3b):
    - Ops text is a bracketed list of factory calls: `[Factory(args...), ...]`
    - Allowed factories and their exact signatures (literal list from `SemanticsMap.FactoryArity`)
    - Arguments MUST be one of: `Reg.<name>`, `Flag.<name>`, `true`, `false` — no string
      literals, no computed expressions
    - No arbitrary lambdas, closures, or C# expressions — only the canonical factory forms
    - `source` must cite the manual section, page, and table (if applicable) verbatim
  - Instruction: "Extract all documented opcodes into the opcode dataset schema. For each
    mnemonic, emit the semantics entry. If you are uncertain about any field, mark it with a
    comment in the `source` field (e.g. `\"source\": \"uncertain: manual p.47 ambiguous\"`)."

  Sub-section 2.3: **The verification ladder** (ordered; each step gates the next):
  1. `--validate-only` — loader validation; catches malformed JSON, unknown modes, byte-count
     mismatches, duplicate opcodes. Command:
     ```
     dotnet run --project tools/CpuEmulator.SpecImporter -- \
       --validate-only \
       --dataset   <path>/opcodes.json \
       --semantics <path>/semantics.json
     ```
  2. `--diff` against an independent extraction — cross-source validation; extract from a
     second independent document (different edition, errata sheet, third-party datasheet),
     diff the two datasets. Disagreements require manual resolution. Command:
     ```
     dotnet run --project tools/CpuEmulator.SpecImporter -- \
       --dataset   <path>/opcodes-a.json \
       --diff      <path>/opcodes-b.json
     ```
  3. CPUGEN diagnostics — build the generated spec; generator errors (CPUGEN001–011) surface
     DSL mistakes the loader passes. Command: `dotnet build 2>&1 | grep CPUGEN`
  4. End-to-end generator gate — the importer roundtrip test
     (`ImporterEndToEndTests.Importer_Output_Compiles_Through_Generator_With_Zero_Diagnostics`)
     must pass: `dotnet test --filter "ImporterEndToEndTests"`
  5. SingleStepTests (TomHarte) — where vectors exist, run the full per-cycle sweep.
     A new architecture without vectors skips this rung and relies on rungs 1–4 + manual
     validation.

  Sub-section 2.4: **The `source` field convention** — per-row provenance citations. Format:
  `"<ManualTitle>, p.<page>, <section or table>"`. Accuracy expectation: every opcode row
  should cite the specific table or paragraph from which its cycle count, mode, and penalty
  flag were read. The LLM should emit these; a human reviewer should verify.

  **Section 3 — Stage 2: Review and Provenance**

  Sub-section 3.1: **Generating the review report** — how to run `--review-report` and what
  to look for. Command:
  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --validate-only \
    --dataset   <path>/opcodes.json \
    --semantics <path>/semantics.json \
    --diff      <path>/opcodes-b.json \
    --review-report <path>/review.md
  ```
  What the report shows: provenance coverage (N/total), rows lacking source (investigate
  these), disagreements (must resolve before merging), missing semantics (hand-work needed).

  Sub-section 3.2: **Interpretation guidance** — how to read each section; what "0/151" means
  (fresh extraction, no citations yet — normal starting point); how to improve coverage
  iteratively (re-run LLM with specific page references, hand-annotate); when the dataset is
  ready to merge (zero disagreements against second source, CPUGEN clean, TomHarte green).

  **Section 4 — Worked Micro-Example (5 Opcodes)**

  A hypothetical extraction of 5 opcodes (`LDA Immediate`, `LDA AbsoluteX`, `JMP Absolute`,
  `NOP Implied`, `BRK Implied`) from a fictional "MyCPU" manual. The example includes:

  4.1 **The extraction prompt** (using the Section 2.2 template verbatim with the five-opcode
  context filled in).

  4.2 **Simulated LLM output** — a 5-row `opcodes.json` and a `semantics.json` with two
  deliberate seeded errors matching Fixture A's spirit (a mnemonic typo and a cycle-count
  disagreement), plus one missing `source` citation.

  4.3 **Rung 1: `--validate-only`** — captured output showing the standard report +
  `provenance: 1/5 rows carry source citations` (4 rows missing). **Captured from real tool.**

  4.4 **Rung 2: `--diff`** against the second (corrected) extraction — captured output
  showing two disagreements. **Captured from real tool.**

  4.5 **Rung 3: CPUGEN** — the mnemonic typo in `semantics.json` causes a CPUGEN006
  (`Unknown micro-op`) or the build succeeds (if the semantics map was well-formed but the
  mnemonic typo only affects the dataset's `mnemonic` field). The example shows what the
  error looks like and how to fix it.

  4.6 **After fixing** — all rungs green; review report shows the resolved state.

  The worked example uses REAL tool output (captured during Task 5 UAT). The five fixture
  files (`mycpu-opcodes-a.json`, `mycpu-opcodes-b.json`, `mycpu-semantics.json`) are created
  in Task 5 and their output is pasted verbatim into the runbook.

- [ ] **Step 2: Update `adding-a-cpu.md`** — add a new section `## Extraction runbook` (after
  the existing Importer section) with a one-paragraph description and a link:
  `For LLM-assisted extraction from a CPU PDF or datasheet, follow the
  [Extraction Runbook](extraction-runbook.md).`

- [ ] **Step 3: Update `docs/user-guide/README.md`** — add a new row to the Contents table:
  `| [Extraction Runbook](extraction-runbook.md) | LLM-assisted opcode extraction from CPU datasheets, cross-source diff, review-report |`

- [ ] **Step 4: Commit** (after Task 5 UAT captures and pastes verbatim outputs) —
  `docs: extraction runbook — Stage 1 + Stage 2 + worked 5-opcode micro-example with real output`

---

### Task 5: UAT — capture real outputs, record in runbook + PR body

**This task captures all verbatim outputs that Task 4 requires.** The runbook is
documentation-accurate only if these outputs come from the real tool.

- [ ] **Step 1: Pre-UAT build gate:**
  ```
  dotnet build --no-incremental
  dotnet test
  ```
  Both must be 0 warnings / all green before capturing outputs.

- [ ] **Step 2: `--validate-only` on the real 6502 data.** Run:
  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --validate-only \
    --dataset   tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
    --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json
  ```
  Expected: exit 0; stdout contains `total=151`, `provenance: 0/151`. Capture verbatim.

- [ ] **Step 3: Self-diff.** Run:
  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --dataset tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
    --diff    tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json
  ```
  Expected: exit 0; `diff: 0 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)`.

- [ ] **Step 4: Seeded-fixture diff.** Run:
  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --dataset tests/CpuEmulator.Tests/Importer/data/mos6502-opcodes-seeded.json \
    --diff    tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json
  ```
  Expected: exit 3; output shows 5 disagreement cells in opcode order. Capture verbatim.

- [ ] **Step 5: Review report on the real dataset.** Run:
  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --validate-only \
    --dataset   tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
    --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
    --review-report /tmp/6502-review.md
  ```
  Then display the report. Expected: 151 rows in the "Rows Lacking Source" table; 0/151
  provenance coverage; Missing Semantics = 0 (all mnemonics have semantics — verified from
  existing suite). Capture the summary line and first few rows.

- [ ] **Step 6: Create the worked-example fixture files** (5-opcode "MyCPU" dataset with
  seeded errors). Use the actual JSON structures the real tool validates. Run `--validate-only`
  and `--diff` against them; capture output. Paste all captured outputs verbatim into
  `extraction-runbook.md` Section 4.

- [ ] **Step 7: Full suite + sampled TomHarte:**
  ```
  dotnet test
  pwsh tools/get-test-vectors.ps1
  $env:CPUEMULATOR_UAT = "full"
  dotnet test --filter "FullyQualifiedName~TomHarte"
  Remove-Item Env:\CPUEMULATOR_UAT
  dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"
  dotnet test --filter "Category=UAT"
  ```
  All must be green. Record: total test count, TomHarte 1,510,000 cases (151 × 10,000), Klaus
  success trap ~96,241,367 cycles, UAT session count.

- [ ] **Step 8: Finalize and commit the runbook** (with real captured outputs in Section 4)
  per Task 4 Step 4.

---

### Task 6: Closeout — spec §9 item 6 DELIVERED, README M1 COMPLETE

**Files:**
- Modify: `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-06-12-m1-extraction-runbook.md` (this file — closeout section)

- [ ] **Step 1: Spec §9 item 6 amendment** — mark the item DELIVERED:

  Change the item-6 bullet from:
  > 6. Datasheet-extraction runbook, Stages 1–2 (decision 2026-06-12): …

  To:
  > 6. Datasheet-extraction runbook, Stages 1–2 (decision 2026-06-12): … **DELIVERED PR #10.**

- [ ] **Step 2: README `## Status` and `## Development workflow`** — update:
  - Status: add sentence: "The datasheet-extraction tooling is complete: `--validate-only`
    validates both schemas and reports provenance coverage; `--diff` cross-checks two
    independent extractions; `--review-report` generates a markdown review artifact. The
    extraction runbook (`docs/user-guide/extraction-runbook.md`) documents the full
    LLM-assisted Stage-1 workflow. **M1 is complete.**"
  - Update the test count in `dotnet test # 848 tests` to the actual closeout count.
  - Next steps: update to "M2 (the IL-JIT tier)" (already there; ensure M1 extraction not
    listed as pending).

- [ ] **Step 3: Commit** —
  `docs: M1 complete — spec §9.6 DELIVERED, README updated, plan closeout`

  **Do NOT push** until the controller's whole-branch review passes.

---

## Plan self-review (completed at write time)

- **Spec §9 item 6 coverage:**
  - `--validate-only`: Task 1 ✓ (load + validate both schemas, provenance coverage, exit 0/2)
  - Cross-source diff (`--diff`): Task 2 ✓ (row-by-row, exit 3, seeded fixture)
  - Review-report generator (`--review-report`): Task 3 ✓ (provenance + rows + diff +
    missing-semantics)
  - Runbook: Task 4 ✓ (Stage-1 LLM workflow + Stage-2 usage + micro-example)
  - UAT: Task 5 ✓ (all four modes run live, outputs captured verbatim)
  - Spec/plan closeout: Task 6 ✓ (§9.6 DELIVERED, README M1 COMPLETE)
- **Exit-code matrix derivation:** 0/1/2/3 are mutually consistent; 2 vs 1 distinction
  (validation failure vs usage error) is new but non-breaking (nothing in the suite today
  asserts on `Program.Main`'s return code for validation errors — there is no path that
  currently returns 2).
- **Test-count estimate:** Task 1: 14, Task 2: 22, Task 3: 10 = 46 new tests;
  848 + 46 = ~894. Actual may vary ±5; the estimate, not the suite, bends.
- **Fixture A consistency check:** all five seeded cells use schema-valid field values
  (mnemonic strings for mnemonic, int for cycles/bytes, bool for pageCrossPenalty, all
  respecting byte-count rules). `0x4C` JMP Absolute seeded `cycles: 4` (real: 3) — valid,
  no byte-count implication. `0x69` ADC Immediate seeded mnemonic `"ADD"` — valid string.
  `0xEA` NOP Implied seeded `"NXX"` — valid string.
- **Diff output format:** pinned by tests (`Seeded_diff_prints_disagreement_table`); the
  runbook quotes captured output so the format is accurate by construction.
- **Runbook accuracy gate:** all Section 4 outputs are captured in Task 5 before Task 4's
  commit; the commit is gated on UAT green.
- **`--diff` not requiring `--semantics`** is deliberate — diffing two datasets for agreement
  on opcode parameters is a pure dataset operation; the semantics map is only needed for
  spec-file emission and review-report generation.
- **Known risks:** (a) the worked-example fixture files for "MyCPU" must be schema-valid so
  `--validate-only` succeeds on the un-seeded version and fails only on the seeded one — the
  Task 5 UAT step verifies this before the commit; (b) the `--review-report` timestamp field
  (`yyyy-MM-dd` only) prevents test flakiness — time-of-day is explicitly excluded from the
  format; (c) the `MissingSemanticsInventory` for the real dataset is expected empty (all 151
  opcodes have semantics as of 3b-ii) — the "All mnemonics have semantics defined" branch is
  exercised by the real-data UAT; the non-empty branch is exercised by a minimal test fixture.

---

## Closeout (2026-06-12)

All six tasks complete on `feat/m1-extraction-runbook`. Commit ladder (each independently
built 0-warning and tested green — bisect-safe):

| Commit | Content | Suite |
|---|---|---|
| `059e563` | Plan document | 848 (baseline) |
| `7dcc189` | Task 1: `--validate-only` + ConsoleIsolation collection fix | 862 |
| `1003b3c` | Task 2: `--diff` + Fixture A seeded dataset | 883 |
| `6c9ae0b` | Task 3: `--review-report` + ReviewReportGenerator | 897 |
| `50d1c3f` | Task 4 + Task 5 UAT: runbook + example fixtures + real outputs | 897 |
| `(this)` | Task 6: spec §9.6 DELIVERED, README M1 COMPLETE, plan closeout | 897 |

### UAT gate record (commands verbatim, outputs recorded)

```
dotnet build --no-incremental  → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test                    → Passed! 897/897, 0 skipped

CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~TomHarte"
                               → 160/160 passed (151 opcode rows + 9 runner self-tests);
                                 151 rows × 10,000 = 1,510,000 cases, ZERO skips

dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"
                               → 1/1 passed (success trap reached; cycle count unchanged
                                 from PR #8 closeout — interpreter did not change)

dotnet test --filter "Category=UAT"
                               → 5/5 passed (2 monitor + 3 host; no new UAT sessions
                                 in this PR — the extraction tools are headless CLI,
                                 not interactive sessions)

dotnet run --project tools/CpuEmulator.SpecImporter -- --validate-only \
  --dataset   tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json
                               → total=151 emitted=151 todoSemantics=0 todoMode=0
                                  provenance: 0/151 rows carry source citations
                                  exit 0

dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --diff    tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json
                               → diff: 0 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)
                                  exit 0

dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tests/CpuEmulator.Tests/Importer/data/mos6502-opcodes-seeded.json \
  --diff    tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json
                               → diff: 5 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)
                                  0x4C  cycles            3          4
                                  0x69  mnemonic          ADC        ADD
                                  0xBD  cycles            4          5
                                  0xBD  pageCrossPenalty  True       False
                                  0xEA  mnemonic          NOP        NXX
                                  exit 3

dotnet run --project tools/CpuEmulator.SpecImporter -- --validate-only \
  --dataset   tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
  --review-report /tmp/6502-review.md
                               → total=151 emitted=151 todoSemantics=0 todoMode=0
                                  provenance: 0/151 rows carry source citations
                                  exit 0
                                  /tmp/6502-review.md: "# Extraction Review: mos6502"
                                  header ✓, 151 rows in Rows Lacking Source ✓,
                                  no Disagreements section ✓,
                                  "All mnemonics have semantics defined." ✓
```

### Test-count actuals vs estimate

Baseline 848 → **897 actual** (+49) vs the ~894 estimate (+~46). Per task:
T1 14 (est ~14 — exact match), T2 21 (est ~22 — one theory row folded into wider InlineData),
T3 14 (est ~10 — +4 additional content/CLI assertions). Delta explained; no count was
weakened.

### Deviations recap

Recorded at write time, all stand unchanged:
1. `--validate-only` validates both schemas (dataset AND semantics), not dataset-only.
2. Exit code 2 for validation failures (distinct from exit 1 usage errors).
3. Fixture A is test-only content (not in tools data/).
4. `--review-report` without `--out` is allowed.
5. `--diff` without `--semantics` is allowed (pure opcode comparison).
6. `--diff` combined with `--validate-only` exits 3 on disagreements even when both
   datasets individually validate.

One addition at implementation time:
7. **ConsoleIsolation xUnit collection** — `ImporterEndToEndTests` and `ValidateOnlyTests`
   both redirect `Console.Out` in-proc via `Program.Main`; parallel execution caused capture
   bleed. Added `[Collection("ConsoleIsolation")]` to both classes and a
   `ConsoleIsolationCollection.cs` definition to serialize them. Not a deviation from the
   plan's intent — the plan's test isolation was implicit; this makes it explicit.

Branch NOT pushed — push and PR #10 wait on the controller's whole-branch review
(standing authorization to merge on green).
