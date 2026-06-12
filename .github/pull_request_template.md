## Summary

<!-- 1-3 bullet points describing what this PR does -->

## Test plan

<!-- How to verify this PR. UAT results go here (see checklist below). -->

## Checklist

- [ ] Unit and integration suite green (`dotnet test` — currently 848 tests)
- [ ] Two-phase code review passed (automated tooling + manual review)
- [ ] Scope-appropriate automated UAT run completed; results recorded below
- [ ] Feature documentation added or updated for all user-facing changes (see `docs/user-guide/`)
- [ ] Plan/spec amendments recorded if decisions changed (see `docs/superpowers/`)

## UAT results

<!-- For PRs touching the CPU interpreter, generator, or host:

Full TomHarte sweep:
  Total cases: _____ (must equal 1,510,000 for the 6502 full sweep)
  Failures: 0

Klaus functional test:
  Cycle count at success trap: _____ (expected ~96,241,367; any change is a STOP)

UAT sessions (`dotnet test --filter "Category=UAT"`):
  Passed: ___ / ___

For docs-only PRs: N/A
-->

---

🤖 Generated with [Claude Code](https://claude.com/claude-code)
