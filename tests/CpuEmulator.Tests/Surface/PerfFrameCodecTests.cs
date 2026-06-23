using System.Text;
using System.Text.Json;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

/// <summary>The un-fakeable data gate for the perf-overlay HUD (design handoff 2026-06-23-perf-overlay):
/// the <c>PF</c> frame round-trips through <see cref="FrameCodec.EncodePerf"/> with the documented shape +
/// omit-key rules, AND the additive host accessors read REAL counters — the tier reflects the BUILT tier,
/// cycles/sec is &gt; 0 after the machine actually runs, and the JIT stats reflect a genuinely JITted run
/// (a compiled block, not a hand-set number). These run a real <see cref="ReferenceSbc"/> Z80 program on
/// both tiers so nothing is mocked.</summary>
public class PerfFrameCodecTests
{
    // --- EncodePerf round-trip + omit-key rules (§6.2) ---

    [Fact]
    public void EncodePerf_is_a_PF_prefixed_json_text_frame_carrying_the_full_jit_softcard_shape()
    {
        var stats = new PerfStats(
            Board: "Apple ][+ SoftCard",
            CyclesPerSecond: 1_020_500,
            NominalClockHz: 1_020_484,
            RamBytes: 65_536,
            HostWorkingSetBytes: 42_991_616,
            IsJitted: true,
            Jit: new JitStats(Compiled: 312, Recompiled: 4, Evicted: 1, SmcHot: 2),
            Coprocessor: new CoprocessorStatus("Z80", Active: false));

        byte[] frame = FrameCodec.EncodePerf(stats);
        string text = Encoding.UTF8.GetString(frame);

        // The wire is "PF " + a JSON body — a sibling of ST, routed by the client's prefix check.
        Assert.StartsWith("PF ", text);

        using JsonDocument doc = JsonDocument.Parse(text["PF ".Length..]);
        JsonElement root = doc.RootElement;
        Assert.Equal("Apple ][+ SoftCard", root.GetProperty("board").GetString());
        Assert.Equal(1_020_500, root.GetProperty("cps").GetDouble());
        Assert.Equal(1_020_484, root.GetProperty("hz").GetDouble());
        Assert.Equal(65_536, root.GetProperty("ramBytes").GetInt64());
        Assert.Equal(42_991_616, root.GetProperty("hostBytes").GetInt64());
        Assert.Equal("jit", root.GetProperty("tier").GetString());

        JsonElement jit = root.GetProperty("jit");
        Assert.Equal(312, jit.GetProperty("compiled").GetInt32());
        Assert.Equal(4, jit.GetProperty("recompiled").GetInt64());
        Assert.Equal(1, jit.GetProperty("evicted").GetInt64());
        Assert.Equal(2, jit.GetProperty("smcHot").GetInt32());

        JsonElement cpu2 = root.GetProperty("cpu2");
        Assert.Equal("Z80", cpu2.GetProperty("name").GetString());
        Assert.False(cpu2.GetProperty("active").GetBoolean());

        // fps + ips are NEVER on the wire (fps is client-only; ips is a deferred follow-on).
        Assert.False(root.TryGetProperty("fps", out _));
        Assert.False(root.TryGetProperty("ips", out _));
    }

    [Fact]
    public void EncodePerf_omits_hz_jit_and_cpu2_keys_on_the_interpreter_single_cpu_no_clock_case()
    {
        var stats = new PerfStats(
            Board: "demo board",
            CyclesPerSecond: 0,
            NominalClockHz: null,        // no declared clock -> omit hz (client shows no ratio)
            RamBytes: 65_536,
            HostWorkingSetBytes: 10_000_000,
            IsJitted: false,             // interpreter -> tier "interpreter", no jit object
            Jit: null,
            Coprocessor: null);          // single-CPU -> no cpu2

        using JsonDocument doc = JsonDocument.Parse(
            Encoding.UTF8.GetString(FrameCodec.EncodePerf(stats))["PF ".Length..]);
        JsonElement root = doc.RootElement;

        Assert.Equal("interpreter", root.GetProperty("tier").GetString());
        Assert.False(root.TryGetProperty("hz", out _));     // omitted, not null
        Assert.False(root.TryGetProperty("jit", out _));    // omitted on the interpreter tier
        Assert.False(root.TryGetProperty("cpu2", out _));   // omitted on a single-CPU board
        Assert.Equal(0, root.GetProperty("cps").GetDouble()); // a truthful zero (boot), still present
    }

    // --- Host accessors read REAL counters (§7) ---

    private static Machine BuildAndRun(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var rom = new byte[0x2000];           // unused by the Z80 boot (runs from RAM at $0000)
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, uart, timer, rom);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        // A tiny loop the JIT will compile into a block (so CompileCount > 0 on the JIT tier): count down
        // from 64 in B, then HALT. Real work — not a hand-set counter.
        var space = machine.Space(AddressSpaceKind.Program);
        byte[] program =
        [
            0x06, 0x40,   // LD B,0x40
            0x05,         // DEC B          (loop body)
            0x20, 0xFD,   // JR NZ,-3       (back to DEC B)
            0x76,         // HALT
        ];
        for (int i = 0; i < program.Length; i++)
            space.Write8((uint)i, program[i]);

        machine.Reset();          // Z80: PC = 0
        machine.Run(10_000);      // ample; the loop + HALT finish well within it
        return machine;
    }

    [Fact]
    public void IsJitted_reflects_the_built_tier()
    {
        Assert.False(BuildAndRun(ExecutionTier.Interpreter).IsJitted);
        Assert.True(BuildAndRun(ExecutionTier.Jit).IsJitted);
    }

    [Fact]
    public void JitMetrics_is_null_on_the_interpreter_and_reflects_a_real_compile_on_the_jit()
    {
        Assert.Null(BuildAndRun(ExecutionTier.Interpreter).JitMetrics);

        IJitMetrics? m = BuildAndRun(ExecutionTier.Jit).JitMetrics;
        Assert.NotNull(m);
        // The loop ran, so the JIT compiled at least one block — a real counter, not a fabricated number.
        Assert.True(m!.CompileCount > 0, $"expected a compiled block on the JIT tier; got {m.CompileCount}");
        Assert.True(m.TotalRecompiles >= 0);
        Assert.True(m.TotalEvictions >= 0);
        Assert.True(m.SmcHotPcCount >= 0);
    }

    [Fact]
    public void AddressSpaceBytes_is_the_real_16bit_map_extent()
    {
        Assert.Equal(65_536, BuildAndRun(ExecutionTier.Interpreter).AddressSpaceBytes);
    }

    [Fact]
    public void PerfPusher_reports_a_positive_cycles_per_second_after_the_machine_runs()
    {
        // A real machine that has executed cycles, plus a fixed host-memory reader so the test is
        // deterministic. The first Tick primes the rate (cps = 0, no window yet); the second Tick — after
        // a real cycle advance between samples — must report cps > 0 (the rate is derived from a REAL
        // CycleCount delta over wall time, not faked).
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var rom = new byte[0x2000];
        Machine machine = BoardMachineFactory.Build(
            ReferenceSbc.Build(CpuKind.Z80, uart, timer, rom), ExecutionTier.Interpreter);
        var space = machine.Space(AddressSpaceKind.Program);
        // A non-halting spin (JR -2 to itself) so each Run advances CycleCount between the two samples.
        space.Write8(0, 0x18); space.Write8(1, 0xFE);   // JR -2 (PC <- PC, an eternal 1-instr loop)
        machine.Reset();

        var frames = new List<byte[]>();
        var pusher = new PerfPusher(machine, () => "ReferenceSbc-Z80", frames.Add,
            hostWorkingSetBytes: () => 12_345_678);

        machine.Run(2_000);
        pusher.Tick();              // prime (cps = 0)
        machine.Run(2_000);        // advance real cycles between samples
        pusher.Tick();              // now a non-zero window -> cps > 0

        Assert.Equal(2, frames.Count);
        double cps = ParseCps(frames[1]);
        Assert.True(cps > 0, $"expected cycles/sec > 0 after running; got {cps}");
        // The host working-set is read through the injected reader (server-side only; deterministic here).
        Assert.Equal(12_345_678, ParseHostBytes(frames[1]));
    }

    private static double ParseCps(byte[] frame)
    {
        using JsonDocument doc = JsonDocument.Parse(
            Encoding.UTF8.GetString(frame)["PF ".Length..]);
        return doc.RootElement.GetProperty("cps").GetDouble();
    }

    private static long ParseHostBytes(byte[] frame)
    {
        using JsonDocument doc = JsonDocument.Parse(
            Encoding.UTF8.GetString(frame)["PF ".Length..]);
        return doc.RootElement.GetProperty("hostBytes").GetInt64();
    }
}
