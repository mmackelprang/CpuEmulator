using System.Text.Json;
using CpuEmulator.Tests.Importer;   // TestRepo.FindRepoRoot()
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Parse-proof tests for the SingleStepTests/8088 (v2) loader — the M5.4 data-axis scaffold. The always-on
/// tests run against the committed 2-case gzip fixture (no multi-GB vector download): they prove the gzip +
/// hex-keyed schema parse, the THREE pinned divergences from the 680x0 loader (hex-keyed resolver, sparse-final
/// MERGE, mask-aware flags — ADR 0006 Decision 5), and that the runner scaffold's state-set wiring runs. The
/// skip-gated real-vector fact runs only when the upstream vectors are present.
/// </summary>
public class M8088TomHarteLoaderTests
{
    private static string FixturePath() =>
        Path.Combine(TestRepo.FindRepoRoot(),
            "tests/CpuEmulator.Tests/TomHarte/fixtures/m8088-sample.json.gz");

    [Fact]
    public void Loads_the_committed_gzip_fixture_with_two_cases()
    {
        var cases = M8088TomHarteLoader.LoadFile(FixturePath());   // exercises GZipStream (the gzip path)
        Assert.Equal(2, cases.Count);
    }

    [Fact]
    public void Case1_memory_form_parses_full_initial_state_and_carries_queue_and_cycles()
    {
        var c = M8088TomHarteLoader.LoadFile(FixturePath())[0];

        Assert.Equal("add byte [ss:bp+di-64h], cl", c.Name);
        Assert.Equal(new byte[] { 0x00, 0x4B, 0x9C }, c.Bytes);   // [0, 75, 156]

        // initial.regs carries ALL 14 keys (every presence flag set).
        var ip = c.Initial.Regs.Present;
        Assert.True(ip is { Ax: true, Bx: true, Cx: true, Dx: true, Cs: true, Ss: true, Ds: true, Es: true,
                            Sp: true, Bp: true, Si: true, Di: true, Ip: true, Flags: true });
        Assert.Equal((ushort)21153, c.Initial.Regs.Ax);
        Assert.Equal((ushort)694, c.Initial.Regs.Ip);
        Assert.Equal((ushort)62546, c.Initial.Regs.Flags);

        // initial.queue is non-empty (a prefetched case): [0, 75, 156, 144].
        Assert.Equal(new byte[] { 0, 75, 156, 144 }, c.Initial.Queue);

        // final.regs is SPARSE — only ip + flags present (nothing else changed at the register level).
        var fp = c.Final.Regs.Present;
        Assert.True(fp.Ip);
        Assert.True(fp.Flags);
        Assert.False(fp.Ax);
        Assert.False(fp.Bx);
        Assert.False(fp.Cx);
        Assert.False(fp.Dx);
        Assert.False(fp.Sp);

        // final.ram lists only the changed cell (the memory ADD result): 138493 => 220.
        Assert.Single(c.Final.Ram);
        Assert.Equal(138493u, c.Final.Ram[0].Address);
        Assert.Equal((byte)220, c.Final.Ram[0].Value);

        // cycles carried LOSSLESSLY (28 per-clock entries, 11 fields each) but NOT asserted for content.
        Assert.Equal(28, c.Cycles.Length);
        Assert.Equal(11, c.Cycles[0].Count);
    }

    [Fact]
    public void Case1_merge_overlays_sparse_final_over_full_initial()
    {
        var c = M8088TomHarteLoader.LoadFile(FixturePath())[0];
        var merged = M8088TomHarteLoader.LoadFile(FixturePath())[0].MergedFinalRegs();

        // The CHANGED registers take the final values...
        Assert.Equal((ushort)697, merged.Ip);
        Assert.Equal((ushort)62594, merged.Flags);
        // ...and every UNCHANGED register keeps its initial value (sparse-final MERGE, not replace).
        Assert.Equal(c.Initial.Regs.Ax, merged.Ax);   // 21153
        Assert.Equal(c.Initial.Regs.Bx, merged.Bx);   // 59172
        Assert.Equal(c.Initial.Regs.Cx, merged.Cx);   // 33224
        Assert.Equal(c.Initial.Regs.Dx, merged.Dx);
        Assert.Equal(c.Initial.Regs.Sp, merged.Sp);
        Assert.Equal(c.Initial.Regs.Bp, merged.Bp);
        Assert.Equal(c.Initial.Regs.Si, merged.Si);
        Assert.Equal(c.Initial.Regs.Di, merged.Di);
        Assert.Equal(c.Initial.Regs.Cs, merged.Cs);
        Assert.Equal(c.Initial.Regs.Ss, merged.Ss);
        Assert.Equal(c.Initial.Regs.Ds, merged.Ds);
        Assert.Equal(c.Initial.Regs.Es, merged.Es);
    }

    [Fact]
    public void Case2_register_form_merge_changes_bx_ip_flags_only()
    {
        var c = M8088TomHarteLoader.LoadFile(FixturePath())[1];

        Assert.Equal("add bh, cl", c.Name);
        Assert.Equal(new byte[] { 0x00, 0xCF }, c.Bytes);   // [0, 207]

        // A register-form case: the initial queue is EMPTY (non-prefetched), and there is no RAM change.
        Assert.Empty(c.Initial.Queue);
        Assert.Empty(c.Final.Ram);
        Assert.Empty(c.Final.Queue);

        // final.regs is SPARSE: only bx, ip, flags.
        var fp = c.Final.Regs.Present;
        Assert.True(fp.Bx);
        Assert.True(fp.Ip);
        Assert.True(fp.Flags);
        Assert.False(fp.Ax);
        Assert.False(fp.Cx);

        var merged = c.MergedFinalRegs();
        Assert.Equal((ushort)14190, merged.Bx);     // bh += cl ⇒ bx changed
        Assert.Equal((ushort)53824, merged.Ip);
        Assert.Equal((ushort)64515, merged.Flags);
        Assert.Equal((ushort)16234, merged.Ax);     // unchanged from initial
        Assert.Equal(c.Initial.Regs.Cx, merged.Cx); // unchanged from initial (58498)
    }

    [Fact]
    public void ApplyFlagsMask_clears_undefined_bits_and_is_identity_under_default_mask()
    {
        // 0xFFEF clears bit 4 (AF) — the undefined-flag mask for an ADD-family opcode.
        Assert.Equal((ushort)0xFFEF, M8088Metadata.ApplyFlagsMask(0xFFFF, 0xFFEF));
        // The default mask (0xFFFF) is the identity: it never weakens a fully-defined opcode's flag compare.
        Assert.Equal((ushort)0x1234, M8088Metadata.ApplyFlagsMask(0x1234, 0xFFFF));
        Assert.Equal(M8088Metadata.DefaultMask, (ushort)0xFFFF);
    }

    [Fact]
    public void Metadata_parses_leaf_and_group_flags_masks_with_default_fallback()
    {
        // Mirrors the live metadata.json shape: a flat opcode with flags-mask, a flat opcode WITHOUT one,
        // and a `reg`-group opcode whose reg=1 carries a flags-mask while reg=0 does not.
        const string json = """
            {
              "opcodes": {
                "08": { "status": "normal", "flags-mask": 65519 },
                "00": { "status": "normal" },
                "80": { "reg": {
                  "0": { "status": "normal" },
                  "1": { "status": "normal", "flags-mask": 65519 }
                } }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var md = M8088Metadata.Parse(doc.RootElement);

        Assert.Equal((ushort)0xFFEF, md.FlagsMask("08", null));   // 65519 == 0xFFEF (AF undefined)
        Assert.Equal((ushort)0xFFFF, md.FlagsMask("00", null));   // present but no flags-mask ⇒ default
        Assert.Equal((ushort)0xFFFF, md.FlagsMask("FF", null));   // unknown opcode ⇒ default
        Assert.Equal((ushort)0xFFEF, md.FlagsMask("80", 1));      // group reg=1 carries the mask
        Assert.Equal((ushort)0xFFFF, md.FlagsMask("80", 0));      // group reg=0 has no mask ⇒ default
        Assert.Equal((ushort)0xFFFF, md.FlagsMask("80", 7));      // absent reg field ⇒ default
    }

    [Fact]
    public void Absent_metadata_file_is_skip_tolerant()
    {
        // A null directory (vectors absent) yields the all-bits-compare default without throwing.
        var md = M8088Metadata.Load(null);
        Assert.Equal((ushort)0xFFFF, md.FlagsMask("08", null));
        Assert.Same(M8088Metadata.Empty, md);
    }

    [Fact]
    public void Resolver_returns_null_when_the_vector_directory_is_absent()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemulator-no-8088-vectors-" + Guid.NewGuid().ToString("N"));
        Assert.Null(M8088TomHarteVectors.ResolveVectorDirectory(root));
    }

    [Fact]
    public void Runner_steps_and_diffs_against_the_merged_final_state()
    {
        // M5.5b: the runner Steps the real M8086Cpu + diffs the 14 registers + RAM against the merged final.
        // The fixture is an ADD case (opcode 00, "add bh, cl"). In M5.5a ADD had NO body (it routed to
        // HandleUndefinedOpcode and the runner reported a mismatch); M5.5b adds the integer-ALU body, so ADD now
        // EXECUTES correctly and the merged-final regs + RAM match byte-exact — the runner returns null (a clean
        // pass). That null IS the proof the ALU body landed and the Step + diff path runs end-to-end on a real
        // ALU op. (FLAGS compare is mask-aware; M8088Metadata.Empty ⇒ all-bits, so ADD's flags must match fully.)
        var c = M8088TomHarteLoader.LoadFile(FixturePath())[1];   // the register-form case (no RAM change)
        string? diff = M8088TomHarteRunner.RunCase(c, M8088Metadata.Empty, "00");
        Assert.Null(diff);   // M5.5b: ADD now has a body ⇒ the merged-final state matches ⇒ no diff
    }

    /// <summary>Skip-gated real-vector proof: when the upstream 8088 v2 vectors are present, load
    /// <c>&lt;dir&gt;/00.json.gz</c> (opcode 00 = ADD r/m8, r8) and assert the full file shape — 10,000 cases,
    /// and the first case's 14-register initial state + non-empty bytes. Skipped at discovery when the vectors
    /// are absent.</summary>
    [M8088TomHarteFact]
    public void Loads_the_real_opcode00_vector_file_when_present()
    {
        string dir = M8088TomHarteVectors.TryGetVectorDirectory()!;   // non-null (the attribute gates it)
        string path = Path.Combine(dir, "00.json.gz");
        if (!File.Exists(path)) return;   // the file may be absent in a partial mirror; the parse is the proof

        var cases = M8088TomHarteLoader.LoadFile(path);
        Assert.Equal(10000, cases.Count);

        var first = cases[0];
        Assert.NotNull(first.Name);
        Assert.NotEmpty(first.Bytes);
        // The full 14-register initial state is present.
        var p = first.Initial.Regs.Present;
        Assert.True(p is { Ax: true, Bx: true, Cx: true, Dx: true, Cs: true, Ss: true, Ds: true, Es: true,
                           Sp: true, Bp: true, Si: true, Di: true, Ip: true, Flags: true });
    }
}
