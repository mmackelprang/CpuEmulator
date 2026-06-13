# Task plan — M2-i JIT Tasks 4–6 (branch feat/m2-jit-i)

Baseline: head b2ec509, 1072 tests green, build 0 warnings.
Tasks 1–3 landed MORE than the plan literal (BlockCompiler split into .cs/.Emit.cs/.Flow.cs;
fastmem split + store arms + MMIO charge-before-callout + EmitFallbackStep ALREADY implemented).

## Key state discoveries
- Task 4 *implementation* is largely DONE in BlockCompiler.cs/.Emit.cs/.Flow.cs:
  fastmem split (LoadByteFromBus/EmitStoreByte), MMIO ordering (EmitChargeOneCycle BEFORE
  bus callout = GT-F(a)), dispatcher-side interrupt check (JittedCpu.Run), EmitFallbackStep.
  → Task 4 work is mostly WRITING THE PINS (FastmemTests.cs). If a pin fails it's a real bug.
- Task 5 invalidation literal in BlockCache.cs is the KNOWN-BUGGY stub (two Critical classes).
- Task 6 JitOptions exists; needs JitOptionsTests.cs + trace-bus wiring for DisableFastmem.

## Controller directives (binding)
1. Task 5: do NOT ship buggy InvalidateIfDirty. Failing tests first for BOTH classes:
   (a) between-block: store marks page P before a block is cached there; later block on P must
       re-decode (unconditional Dirty.Clear() drops the mark = stale). Fix: clear only the
       dirtied page's mark when its block recompiled, not all marks unconditionally.
   (b) intra-block: store within block to block's OWN page must force recompile.
2. Intra-block SMC: PRE-AUTHORIZED — END THE BLOCK when an emitted writable-RAM store targets the
   executing block's page range. Implement with a failing test (routine writes opcode ahead of PC
   in same block, executes; JIT must match interpreter). Prefer end-block guard; fallback is a
   documented jit.md exclusion + Klaus proof. Report which taken + why.
3. After 4–6: differential sanity (JIT vs interp, ~100 randomized incl self-modifying, cache ON).

## Phases
- [x] Phase 0: Read plan in full + handoff note + GTs. Orient in code. Baseline confirmed 1072.
- [ ] Phase 1 (Task 4): Write FastmemTests.cs pins (MMIO store/load, RAM round-trip, ROM drop,
      block-entry irq, budget==delta, GT-F a/b/c). Fix any real bug surfaced. Gate. Commit.
- [ ] Phase 2 (Task 5): Failing tests for both SMC classes + intra-block. Fix BlockCache +
      end-block guard in compiler. Gate. Commit.
- [ ] Phase 3 (Task 6): JitOptionsTests.cs + trace-bus wiring. Gate. Commit.
- [ ] Phase 4: differential sanity (~100 randomized incl SMC, cache ON). Report.
- [ ] Phase 5: Final report.

## Gates per task
build --no-incremental → 0 warnings; full suite green; 1072 baseline stays green.
Commit per task + plan checkbox updates + footer Co-Authored-By: Claude Fable 5.

## BLOCKED policy
Report exact IL/exception + failing seed rather than flailing on IL.
