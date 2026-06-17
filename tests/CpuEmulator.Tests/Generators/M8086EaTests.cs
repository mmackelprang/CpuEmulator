using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M5.3 (ADR 0005 Decision 2 / ADR 0006 Decision 2) — the 8086 ModR/M effective-address +
/// SEGMENTATION layer. Proves, against the REAL <see cref="M8086Cpu"/> (it has the full register file + the
/// generated <c>ComputeX86Ea</c>/<c>DefaultSegmentForX86Rm</c> probes) + the Core
/// <see cref="AddressSpaceFetchStream"/>:
///
/// <list type="number">
///   <item>the 16-bit <c>(mod, r/m)</c> EA-offset table — every base+index form, the
///     <c>mod=00,r/m=110</c> ⇒ disp16 DIRECT exception, and the 16-bit-offset wrap (a near-0xFFFF
///     <c>[BX+SI]</c> wraps within the segment, it does NOT carry into the segment base);</item>
///   <item>the 20-bit physical resolution <c>(seg&lt;&lt;4)+offset &amp; 0xFFFFF</c> across default-segment +
///     override cases, including the high-memory 20-bit wrap (<c>seg=0xFFFF, offset=0xFFFF</c> → 0xFFFEF);</item>
///   <item>the default-segment-per-mode rule (BP-based ⇒ SS, the rest ⇒ DS) + the override replacing it;</item>
///   <item>the 20-bit <c>(CS&lt;&lt;4)+IP</c> physical instruction fetch (resolving M5.2's deferred MEDIUM —
///     the segmented <see cref="AddressSpaceFetchStream"/> + the generator wiring that selects it when CS is
///     declared, falling back to the flat 16-bit fetch for the synthetic decode fixture).</item>
/// </list>
///
/// The bus is UNCHANGED — the flat 20-bit little-endian <see cref="AddressSpace"/>. NO 8086 opcode is live
/// (the op bodies are M5.5a); this gates the EA/segmentation MACHINERY only.</summary>
public class M8086EaTests
{
    private static M8086Cpu NewCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return new M8086Cpu(bus);
    }

    // ── Part A: the (CS<<4)+IP 20-bit physical instruction fetch (AddressSpaceFetchStream segmented mode) ──

    [Fact]
    public void Segmented_fetch_reads_from_the_physical_seg_shifted_plus_offset()
    {
        // (seg<<4)+offset: CS=0x1000, IP=0x0234 → physical 0x10234.
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        bus.Write8(0x10234, 0xAB);
        bus.Write8(0x10235, 0xCD);

        var stream = new AddressSpaceFetchStream(bus, offset: 0x0234, segment: 0x1000);
        Assert.Equal(0xABu, stream.PeekUnit());     // peek does not advance
        Assert.Equal(0xABu, stream.NextUnit());     // first byte at (CS<<4)+IP
        Assert.Equal(0xCDu, stream.NextUnit());     // the cursor advanced one byte within the segment
        Assert.Equal(2, stream.UnitsConsumed);
    }

    [Fact]
    public void Segmented_fetch_offset_wraps_within_the_segment_not_into_the_segment_base()
    {
        // CS=0x2000 (base 0x20000). IP starts at 0xFFFF; consuming past it wraps the 16-bit offset to 0x0000
        // WITHIN the segment — so the next byte is at physical 0x20000, NOT 0x30000 (no carry into the base).
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        bus.Write8(0x2FFFF, 0x11);   // (0x2000<<4) + 0xFFFF
        bus.Write8(0x20000, 0x22);   // (0x2000<<4) + 0x0000 — the wrap target

        var stream = new AddressSpaceFetchStream(bus, offset: 0xFFFF, segment: 0x2000);
        Assert.Equal(0x11u, stream.NextUnit());   // at 0x2FFFF
        Assert.Equal(0x22u, stream.NextUnit());   // offset wrapped 0xFFFF→0x0000 → 0x20000 (segment-relative)
    }

    [Fact]
    public void Segmented_fetch_masks_the_physical_address_to_20_bits()
    {
        // CS=0xFFFF (base 0xFFFF0), IP=0x0010 → 0xFFFF0+0x10 = 0x100000, masked to 20 bits = 0x00000.
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        bus.Write8(0x00000, 0x7E);   // the 20-bit-wrap target

        var stream = new AddressSpaceFetchStream(bus, offset: 0x0010, segment: 0xFFFF);
        Assert.Equal(0x7Eu, stream.NextUnit());
    }

    [Fact]
    public void Flat_fetch_mode_is_unchanged_16bit_wrap()
    {
        // The (bus, ushort pc) ctor is the 6502/Z80 flat mode — UNCHANGED by M5.3 (16-bit origin wrap, no
        // segment). Prove it still wraps at 16 bits over the low 64 KB regardless of the 20-bit bus.
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        bus.Write8(0xFFFF, 0x33);
        bus.Write8(0x0000, 0x44);

        var stream = new AddressSpaceFetchStream(bus, (ushort)0xFFFF);
        Assert.Equal(0x33u, stream.NextUnit());   // at 0xFFFF
        Assert.Equal(0x44u, stream.NextUnit());   // 16-bit wrap 0xFFFF→0x0000 (flat, no segment base)
    }

    // ── Part B: the 16-bit (mod, r/m) EA-offset table (the generated ComputeX86Ea probe) ──────────────────

    private static M8086Cpu CpuWithIndexRegs(
        ushort bx = 0, ushort bp = 0, ushort si = 0, ushort di = 0)
    {
        var cpu = NewCpu();
        cpu.SetRegister("BX", bx);
        cpu.SetRegister("BP", bp);
        cpu.SetRegister("SI", si);
        cpu.SetRegister("DI", di);
        return cpu;
    }

    [Fact]
    public void Ea_offset_each_base_index_form_sums_the_right_registers()
    {
        // Distinct register values so each form's sum is unambiguous. mod=00 (no disp) for the base+index forms.
        var cpu = CpuWithIndexRegs(bx: 0x1000, bp: 0x2000, si: 0x0300, di: 0x0040);
        Assert.Equal(0x1300, cpu.ComputeX86EaProbe(0, 0, 0));   // r/m=000 [BX+SI] = 0x1000+0x0300
        Assert.Equal(0x1040, cpu.ComputeX86EaProbe(0, 1, 0));   // r/m=001 [BX+DI] = 0x1000+0x0040
        Assert.Equal(0x2300, cpu.ComputeX86EaProbe(0, 2, 0));   // r/m=010 [BP+SI] = 0x2000+0x0300
        Assert.Equal(0x2040, cpu.ComputeX86EaProbe(0, 3, 0));   // r/m=011 [BP+DI] = 0x2000+0x0040
        Assert.Equal(0x0300, cpu.ComputeX86EaProbe(0, 4, 0));   // r/m=100 [SI]
        Assert.Equal(0x0040, cpu.ComputeX86EaProbe(0, 5, 0));   // r/m=101 [DI]
        // r/m=110 at mod=00 is the disp16-direct EXCEPTION (covered below); at mod≠00 it is [BP].
        Assert.Equal(0x2000, cpu.ComputeX86EaProbe(1, 6, 0));   // r/m=110 mod=01 [BP] = 0x2000 (+disp 0)
        Assert.Equal(0x1000, cpu.ComputeX86EaProbe(0, 7, 0));   // r/m=111 [BX]
    }

    [Fact]
    public void Ea_offset_mod00_rm110_is_the_disp16_direct_exception()
    {
        // mod=00,r/m=110 ⇒ disp16 DIRECT: base/index = 0, the offset IS the disp16 (BP is NOT used).
        var cpu = CpuWithIndexRegs(bp: 0x9999);   // BP set, to prove it is NOT added in the direct case
        Assert.Equal(0x1234, cpu.ComputeX86EaProbe(0, 6, 0x1234));   // offset == disp16, BP ignored
        // Contrast: mod=01,r/m=110 DOES use BP (it is [BP+disp8], not the exception).
        Assert.Equal((ushort)(0x9999 + 0x10), cpu.ComputeX86EaProbe(1, 6, 0x10));
    }

    [Fact]
    public void Ea_offset_adds_displacement_with_sign_extension()
    {
        // disp is the SIGN-EXTENDED value (the decode walk sign-extends disp8→disp16 before the EA call).
        var cpu = CpuWithIndexRegs(bx: 0x1000, si: 0x0020);   // [BX+SI] = 0x1020
        Assert.Equal(0x1030, cpu.ComputeX86EaProbe(1, 0, 0x10));            // +disp8 +0x10
        Assert.Equal((ushort)(0x1020 - 0x10), cpu.ComputeX86EaProbe(1, 0, unchecked((ushort)(short)-0x10))); // -0x10
        Assert.Equal(0x2020, cpu.ComputeX86EaProbe(2, 0, 0x1000));         // +disp16 +0x1000
    }

    [Fact]
    public void Ea_offset_wraps_at_16_bits_near_0xFFFF()
    {
        // [BX+SI] whose 16-bit sum exceeds 0xFFFF wraps WITHIN the segment (it does NOT carry out).
        var cpu = CpuWithIndexRegs(bx: 0xFFFF, si: 0x0002);   // 0xFFFF + 0x0002 = 0x10001 → wraps to 0x0001
        Assert.Equal(0x0001, cpu.ComputeX86EaProbe(0, 0, 0));
        // And with a displacement pushing it over: [BX] + disp16 0x0005 with BX=0xFFFE → 0x0003.
        var cpu2 = CpuWithIndexRegs(bx: 0xFFFE);
        Assert.Equal(0x0003, cpu2.ComputeX86EaProbe(2, 7, 0x0005));
    }

    // ── Part C: the 20-bit physical resolution (seg<<4)+offset & 0xFFFFF (M8086Cpu.Physical) ──────────────

    [Theory]
    [InlineData(0x0000, 0x0000, 0x00000u)]   // origin
    [InlineData(0x1000, 0x0234, 0x10234u)]   // a plain segmented address
    [InlineData(0x07C0, 0x0000, 0x07C00u)]   // the classic boot segment 07C0:0000
    [InlineData(0x0000, 0xFFFF, 0x0FFFFu)]   // offset-only
    [InlineData(0xF000, 0xFFF0, 0xFFFF0u)]   // the reset vector F000:FFF0
    public void Physical_forms_seg_shifted_plus_offset(int seg, int offset, uint expected)
    {
        Assert.Equal(expected, M8086Cpu.Physical((ushort)seg, (ushort)offset));
    }

    [Fact]
    public void Physical_masks_the_20bit_wrap_at_the_top_of_memory()
    {
        // The famous high-memory wrap: FFFF:FFFF = 0xFFFF0 + 0xFFFF = 0x10FFEF, masked to 20 bits = 0xFFEF.
        Assert.Equal(0xFFEFu, M8086Cpu.Physical(0xFFFF, 0xFFFF));
        // FFFF:0010 = 0xFFFF0 + 0x10 = 0x100000 → 0x00000 (the A20-style wrap).
        Assert.Equal(0x00000u, M8086Cpu.Physical(0xFFFF, 0x0010));
    }

    // ── Part D: the default-segment rule (BP⇒SS, else DS) + the segment override ─────────────────────────

    [Theory]
    // (mod, r/m) → expected default segment. BP-based forms (r/m 010/011, and 110 EXCEPT the mod=00 direct) ⇒ SS.
    [InlineData(0, 0, false)]   // [BX+SI] → DS
    [InlineData(0, 1, false)]   // [BX+DI] → DS
    [InlineData(0, 2, true)]    // [BP+SI] → SS
    [InlineData(0, 3, true)]    // [BP+DI] → SS
    [InlineData(0, 4, false)]   // [SI]    → DS
    [InlineData(0, 5, false)]   // [DI]    → DS
    [InlineData(0, 6, false)]   // mod=00,r/m=110 disp16 DIRECT → DS (NO BP)
    [InlineData(1, 6, true)]    // mod=01,r/m=110 [BP+disp8]    → SS (uses BP)
    [InlineData(2, 6, true)]    // mod=10,r/m=110 [BP+disp16]   → SS (uses BP)
    [InlineData(0, 7, false)]   // [BX]    → DS
    public void Default_segment_picks_SS_for_BP_based_forms_DS_otherwise(int mod, int rm, bool expectSs)
    {
        var cpu = NewCpu();
        cpu.SetRegister("SS", 0x9000);
        cpu.SetRegister("DS", 0x1000);
        ushort got = cpu.DefaultSegmentForX86RmProbe((uint)mod, (uint)rm);
        Assert.Equal(expectSs ? (ushort)0x9000 : (ushort)0x1000, got);
    }

    [Fact]
    public void Segment_override_replaces_the_default()
    {
        var cpu = NewCpu();
        cpu.SetRegister("DS", 0x1000);
        cpu.SetRegister("ES", 0x2000);
        cpu.SetRegister("CS", 0x3000);
        cpu.SetRegister("SS", 0x4000);

        // No override ⇒ the per-mode default is kept.
        Assert.Equal((ushort)0x1000, cpu.ResolveSegment(0x1000, M8086Cpu.X86SegmentOverride.None));
        // Each override selects its segment register, replacing the default.
        Assert.Equal((ushort)0x2000, cpu.ResolveSegment(0x1000, M8086Cpu.X86SegmentOverride.Es));
        Assert.Equal((ushort)0x3000, cpu.ResolveSegment(0x1000, M8086Cpu.X86SegmentOverride.Cs));
        Assert.Equal((ushort)0x4000, cpu.ResolveSegment(0x1000, M8086Cpu.X86SegmentOverride.Ss));
        Assert.Equal((ushort)0x1000, cpu.ResolveSegment(0x9999, M8086Cpu.X86SegmentOverride.Ds));
    }

    // ── Part E: the end-to-end ResolveEaPhysical composition (offset → segment → physical) ───────────────

    [Fact]
    public void ResolveEaPhysical_composes_offset_segment_and_the_20bit_shift()
    {
        var cpu = CpuWithIndexRegs(bx: 0x0100, si: 0x0020);   // [BX+SI] offset = 0x0120
        cpu.SetRegister("DS", 0x2000);
        cpu.SetRegister("SS", 0x9000);

        // [BX+SI], mod=10, disp16 0x0004 → offset 0x0124; r/m=000 ⇒ DS default → (0x2000<<4)+0x0124 = 0x20124.
        Assert.Equal(0x20124u, cpu.ResolveEaPhysical(2, 0, 0x0004, M8086Cpu.X86SegmentOverride.None));

        // A BP-based form (mod=01,r/m=110 [BP+disp8]) defaults to SS.
        cpu.SetRegister("BP", 0x0050);
        Assert.Equal((0x9000u << 4) + 0x0055u, cpu.ResolveEaPhysical(1, 6, 0x0005, M8086Cpu.X86SegmentOverride.None));

        // An override on the DS form selects ES instead.
        cpu.SetRegister("ES", 0x3000);
        Assert.Equal(0x30124u, cpu.ResolveEaPhysical(2, 0, 0x0004, M8086Cpu.X86SegmentOverride.Es));
    }

    [Fact]
    public void ResolveEaPhysical_wraps_the_offset_then_the_20bit_physical()
    {
        // The two wraps compose: a near-top offset wraps at 16 bits, then the (seg<<4)+offset wraps at 20.
        var cpu = CpuWithIndexRegs(bx: 0xFFFF, si: 0x0001);   // 0x10000 → offset wraps to 0x0000
        cpu.SetRegister("DS", 0xFFFF);                        // base 0xFFFF0
        // offset 0x0000, seg 0xFFFF → 0xFFFF0, masked to 20 bits = 0xFFFF0.
        Assert.Equal(0xFFFF0u, cpu.ResolveEaPhysical(0, 0, 0, M8086Cpu.X86SegmentOverride.None));
    }

    // ── Part F: the EA read/write helpers over the resolved physical address (LE byte/word) ──────────────

    [Fact]
    public void Ea_byte_round_trips_at_a_resolved_physical_address()
    {
        var cpu = NewCpu();
        uint phys = M8086Cpu.Physical(0x1000, 0x0234);   // 0x10234
        cpu.WriteEaByte(phys, 0x5A);
        Assert.Equal((byte)0x5A, cpu.ReadEaByte(phys));
    }

    [Fact]
    public void Ea_word_is_little_endian_two_byte_cycles()
    {
        var cpu = NewCpu();
        uint phys = M8086Cpu.Physical(0x1000, 0x0010);   // 0x10010
        cpu.WriteEaWordPhysical(phys, 0xBEEF);
        // Little-endian: low byte at the lower address.
        Assert.Equal((byte)0xEF, cpu.ReadEaByte(phys));
        Assert.Equal((byte)0xBE, cpu.ReadEaByte(phys + 1));
        Assert.Equal((ushort)0xBEEF, cpu.ReadEaWordPhysical(phys));
    }

    // ── Part G: the generator wiring — the x86 Step emits the (CS<<4)+IP segmented fetch when CS exists ──

    // A synthetic x86-decode CPU WITH the four segment registers (so the x86 Step emits the SEGMENTED fetch).
    // It declares no EA index registers and a different architecture string than "m8086", so EmitX86Ea does
    // NOT fire — this isolates the fetch-wiring assertion (the decode SHAPE is M5.2's fixture).
    private const string X86WithSegmentsSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("x86segtest")]
        public static class X86SegTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("CS", 16),
                new("IP", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly X86DecodeStructure Decode = new(
                Prefixes: [],
                Opcodes: [ new X86Opcode(0x90) ]);

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x90, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class X86SegTestCpu
        {
            private readonly IAddressSpace _bus;
            public X86SegTestCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void X86_step_emits_the_segmented_cs_ip_fetch_when_a_CS_register_is_declared()
    {
        var result = GeneratorTestHost.Run(X86WithSegmentsSpec);
        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n", result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        // The Step constructs the SEGMENTED fetch stream over (CS<<4)+IP — the 3-arg ctor (offset, segment).
        Assert.Contains("new CpuEmulator.Core.Jit.AddressSpaceFetchStream(_bus, IP, CS)", result.GeneratedText);
    }

    // A synthetic x86-decode CPU with NO segment register (A/IP only) — the M5.2 decode-SHAPE fixture shape.
    // Its x86 Step must keep the FLAT 16-bit fetch (M5.3 does not perturb the decode-SHAPE proof).
    private const string X86NoSegmentsSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("x86noseg")]
        public static class X86NoSegSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 16),
                new("IP", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly X86DecodeStructure Decode = new(
                Prefixes: [],
                Opcodes: [ new X86Opcode(0x90) ]);

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x90, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class X86NoSegCpu
        {
            private readonly IAddressSpace _bus;
            public X86NoSegCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void X86_step_falls_back_to_the_flat_fetch_when_no_segment_register_is_declared()
    {
        // No segment register ⇒ the FLAT 16-bit fetch (the M5.2 decode-SHAPE proof is unchanged by M5.3).
        var result = GeneratorTestHost.Run(X86NoSegmentsSpec);
        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n", result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        Assert.Contains("new CpuEmulator.Core.Jit.AddressSpaceFetchStream(_bus, IP)", result.GeneratedText);
        Assert.DoesNotContain("AddressSpaceFetchStream(_bus, IP, ", result.GeneratedText);
    }
}
