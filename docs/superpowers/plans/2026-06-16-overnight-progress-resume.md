# M4.5 arc — overnight autonomous session progress + resume state (2026-06-16)

> Written by the Coordinator after an autonomous overnight run (user asleep, "continue as far as possible").
> Hard rule honored: nothing seam-breaking or foundational was merged without the owner's sign-off.

## ✅ Merged to main (each independently verified green before merge, per the owner's standing
"merge PRs that pass test/UAT/review" permission)
- **PR #41 — cross-CPU benchmarking Milestone A** (merge `522b5ac`). The `ITierDriver` refactor (6502 numbers
  byte-identical), the Z80 driver + W1/W2 workloads, and all three third-party Z80 refs (Z80dotNet/C#,
  superzazu/C, DrGoldfire/Z80.js). W1=2.0B-Tstate ZEXDOC prefix, W2=50M-cycle kernel — **frozen** as the M6
  re-measure contract. "Baseline ships regardless" invariant verified.
- **PR #42 — M4.5c** (merge `23fb73d`). Shift/rotate + bit + BCD + Scc + CMPM + data-movement (+MOVEP). 68000
  TomHarte 109 theories green; the ~2,619 bundled CMPM cases now assert; ADR 0007 §7.1 verdict-(b) held.

## 📄 On main (docs only)
- `docs/architecture/0008-…-control-flow-exceptions-and-the-timing-axis.md` — **PROPOSED**. The M4.5d design;
  splits into d-1 (additive/data-axis) + d-2 (seam-breaking timing/prefetch).
- Plans: `2026-06-16-m4-5d-1-control-flow-exceptions.md`, `2026-06-15-cross-cpu-speed-benchmarking.md`,
  `2026-06-15-m4-5c-shift-rotate-bit-bcd.md`.

## ⛔ Open + HELD for owner review (NOT merged)
- **PR #43 — M4.5d-1** (branch `feat/m4-5d-1-control-flow-exceptions`). Control flow + the exception model.
  **Verified green** by the coordinator: build 0/0; 68000 TomHarte **185 theories / 0 fail / 0 skip** (the
  M4.5a–c regression guard with `assertExceptions=false` + the new gate with `=true`, ~16,433 exception cases
  asserted); non-TomHarte **1810 / 0 fail / 0 skip** (byte-identity + control/exception unit tests); pre-merge
  review = no confirmed bug. **Held** because it touches previously-frozen seam files (Alu.cs ÷0, Move.cs
  privilege, the runner's default-off `assertExceptions` flag, M68000Cpu.cs IPL hooks) and introduces the
  exception model — owner decisions. `M68000FetchStream.cs` + the bus helpers are UNTOUCHED.

## 🔵 Decisions awaiting Mark's sign-off (ADR 0008)
- **D** — the default-off `assertExceptions` runner-flag seam touch (in PR #43; only strengthens the gate).
- **C** — timing-axis cycle-accuracy depth (gates **M4.5d-2**; tier i full per-transaction vs tier ii pc+prefetch).
- **E** — IPL model: the thin synthetic stub (in PR #43) vs. defer the whole IPL to M4.5d-2.
- **F** — address-error group-0 frame: assert trap-taken only (in PR #43) vs. assert the full frame words.

## Reviewer follow-ups on PR #43 (recommended, NOT applied)
- Finding 1: add a compose-to-`final.pc` guard to `IsAddressErrorCase` (latent; zero false positives today).
- Finding 3: drop the dead `_ = v0;` in `IsAddressErrorCase`.
- (Findings 2/4/5 are honesty disclosures, captured in the PR body.)

## Blocked downstream chain (unblocks once PR #43 merges + C is decided)
Merge PR #43 → **M4.5d-2** (timing/prefetch, seam-breaking, needs decision C) → **M4.6** (68000 through the JIT,
all-fallback) → **benchmarking Milestone B** (68000 baseline) → **M5** (8086) → **M6** (hot-op JIT emit = the
benchmarking before/after speedup payoff).

## Infra note (the empty-spawn glitch)
- It recurred once: the first M4.5d-1 Planner returned a 0-tool-use no-op; re-dispatched fresh, succeeded.
- The M4.5d-1 **Builder** completed the implementation + the pre-merge review (green) but glitched out before its
  final commit+push+PR. The Coordinator finished those mechanical steps: committed the Builder's already-written
  reconciliation fixes (the Bcc.b `0xFF` correctness fix, ÷0 CCR+PC, CHK-trap CCR, LINK A7-edge), deleted the
  self-labeled throwaway `M68000ZzzCountDump.cs`, independently re-verified the gate, and opened PR #43 (held).
