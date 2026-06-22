using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class Apl2Cpm3BootTests
{
    // ADR 0018 PR-1 (V80-1): the apl2cpm3 40-col boot gate is PRESENT but named-skipped until V80-2 closes
    // the Z80-entry handoff (the Z80 NOP-slides from $0000 after the slot fix until the entry vector is
    // placed -- Decision 3). PR-1 lands the slot fix (the Z80 activates + the CPMLDR loads to $1100+) but
    // does NOT reach A>. A passing gate here would lie; an asserting gate would be RED. So this is named +
    // skipped, visible + un-fakeable when V80-2 fills it in (the CPM-1 -> CPM-4 skip-then-fill cadence).
    //
    // V80-2 replaces this body with: build the SoftCard board at slot 4 (controlPortBase: $C400) over
    // Apl2Cpm3.LoadBootDisk(), run the cold boot, decode the 40-col text page, and assert the decoded "A>"
    // CCP prompt + a CP/M-3 sign-on substring ("CP/M V3.0" / "DIGITAL RESEARCH") + CoprocessorActive, gated
    // by [Apl2Cpm3Fact] (skip-with-note when the asset is absent).
    [Fact(Skip = "apl2cpm3 CP/M 3.1 A> (40-col) lands in V80-2 (ADR 0018 PR-2); V80-1 only lands the slot " +
                 "fix + the asset loader + this honest skipped gate. The Z80-entry handoff (Decision 3) is " +
                 "still absent in PR-1, so A> is not yet reached -- never false-passing.")]
    public void Cpm3_boots_to_the_A_prompt_on_the_interpreter_40col()
    {
        // Intentionally skipped until V80-2. See the comment above for what V80-2 asserts.
    }
}
