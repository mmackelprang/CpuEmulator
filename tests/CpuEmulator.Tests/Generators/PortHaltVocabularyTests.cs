using CpuEmulator.Core.Jit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.2 Tasks 1-3 — the additive port/halt VOCABULARY (modes, micro-ops, the Port class)
/// proven at the generator + JIT-data-layer level, in isolation from the full synthetic CPU. These
/// are the smallest red→green units: the new AddrMode/JitMode members round-trip; a row using
/// PortIn("A")/Halt() is recognized; a Port-class row classifies + is mode-gated. None of this
/// touches a 6502 path (the 6502 declares no port op, no halt) — the byte-identical-6502 invariant
/// (Ground truth E) is pinned by the unchanged generator snapshot + the .g.cs hash at Task 10.</summary>
public class PortHaltVocabularyTests
{
    // ── Task 1: the IoPort* modes exist in the JIT data-layer mirror (JitMode) ────────────────

    [Fact]
    public void JitMode_admits_the_two_IoPort_modes()
    {
        // The JIT data-layer copy of AddrMode (JitMode) gains IoPortImmediate/IoPortIndirect —
        // additive enum members the 6502 never names (Ground truth A.2 mirror-table tax).
        Assert.True(System.Enum.IsDefined(typeof(JitMode), JitMode.IoPortImmediate));
        Assert.True(System.Enum.IsDefined(typeof(JitMode), JitMode.IoPortIndirect));
    }

    [Fact]
    public void OpcodeDescriptor_round_trips_an_IoPort_mode()
    {
        // A descriptor constructed with the new mode round-trips it — the record shape is UNCHANGED
        // (the port op rides JitOp.Kind, no new field; Ground truth A.3), so this is a pure
        // additive-enum-value proof. (The Port CLASS is Task 3 — proven there.)
        var d = new OpcodeDescriptor(
            0xDB, "IN", JitMode.IoPortImmediate, JitOpClass.Load,
            LengthRule.Fixed, FixedLength: 2, BaseCycles: 3, PageCrossPenalty: false,
            NeedsFallback: false, EndsBlock: false,
            Ops: [new JitOp("PortIn", "A", "", 0, false)]);

        Assert.Equal(JitMode.IoPortImmediate, d.Mode);
        Assert.Equal("PortIn", d.Ops[0].Kind);
        Assert.Equal("A", d.Ops[0].RegA);
    }
}
