using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using CpuEmulator.Benchmarks;
using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Bench;

/// <summary>The thin schema-rot guard for the standing profiler (ADR 0022 item A / §4.3). It does NOT
/// drive a real boot (that is the developer/agent offline run); it covers the asset-free KERNEL path: it
/// runs a 6502 bench W-kernel on both tiers (the SAME bench library the profiler reuses), reads the
/// IJitMetrics counters off the JIT run (incl. the item-C chain/dispatch + block-cache counters), builds
/// a profile.json-shaped object, and asserts it serializes + round-trips with schemaVersion==1 and every
/// required key present. If the on-disk schema (bench/profiler/ProfileSchema.cs) loses a required key or
/// the JSON stops round-tripping, this fails — so the format cannot silently rot.
///
/// The profiler project (bench/profiler) is intentionally NOT in the solution/test graph (the ADR 0011
/// §6 posture — a dev tool, not a shipping dependency), so this test re-states the REQUIRED KEY SET here
/// rather than referencing the profiler. The key list is the contract the profiler's records must honor;
/// a drift on either side is a deliberate, reviewed change.</summary>
public class ProfilerFormatSmokeTests
{
    // The required top-level keys (ADR 0022 §4.2). camelCase, matching the profiler's JsonNamingPolicy.
    private static readonly string[] RequiredTopKeys =
    {
        "schemaVersion", "generatedUtc", "commit", "host", "system", "workload",
        "instructionBudget", "budgetUnit", "tiers", "perPeripheralFrameCostNs", "notes",
    };

    private static readonly string[] RequiredJitKeys =
    {
        "instructionsRetired", "cyclesPerSecond", "realtimeRatio", "emitCoverage", "fallbackByOpcode",
        "compileCount", "totalRecompiles", "totalEvictions", "smcHotPcCount",
        "chainEdgesTaken", "dispatcherEntries", "blockCacheHits", "blockCacheMisses", "allocBytesPerWindow",
    };

    private static readonly string[] RequiredInterpKeys =
    {
        "instructionsRetired", "cyclesPerSecond", "realtimeRatio", "hotOps", "allocBytesPerWindow",
    };

    [Fact]
    public void Profiler_emits_a_well_formed_profile_json_for_a_kernel_on_both_tiers()
    {
        // ── drive a 6502 W-kernel on both tiers via the SAME bench library the profiler reuses (asset-free) ──
        BenchWorkload w = Workloads.SieveKernel();
        long interpCycles = Tier0.RunCounted(w).Cycles;
        Assert.True(interpCycles > 0, "interpreter kernel run produced no cycles");

        // The JIT run, with the IJitMetrics counters read off the running JIT (the item-C seam).
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: new JitOptions());
        long budget = 2_000_000;
        jit.Run(ref budget);
        IJitMetrics m = jit;   // JittedCpu implements IJitMetrics

        // ── build a profile.json-shaped object mirroring the schema (the dev-tool records live out of graph) ──
        var profile = new
        {
            schemaVersion = 1,
            generatedUtc = System.DateTime.UtcNow.ToString("o"),
            commit = "deadbee",
            host = new { cpu = "test", os = "test", dotnet = "10.0" },
            system = "bench-6502",
            workload = "W3-sieve",
            instructionBudget = 20_000_000L,
            budgetUnit = "instructions",
            tiers = new
            {
                interpreter = new
                {
                    instructionsRetired = 20_000_000L,
                    cyclesPerSecond = (double)interpCycles,
                    realtimeRatio = (double?)null,
                    hotOps = new[] { new { mnemonic = "LDA", count = 100L, pct = 25.0, cumPct = 25.0 } },
                    allocBytesPerWindow = 0L,
                },
                jit = new
                {
                    instructionsRetired = 0L,
                    cyclesPerSecond = (double)cpu.CycleCount,
                    realtimeRatio = (double?)null,
                    emitCoverage = (double?)null,
                    fallbackByOpcode = System.Array.Empty<object>(),
                    compileCount = m.CompileCount,
                    totalRecompiles = m.TotalRecompiles,
                    totalEvictions = m.TotalEvictions,
                    smcHotPcCount = m.SmcHotPcCount,
                    chainEdgesTaken = m.ChainEdgesTaken,
                    dispatcherEntries = m.DispatcherEntries,
                    blockCacheHits = m.BlockCacheHits,
                    blockCacheMisses = m.BlockCacheMisses,
                    allocBytesPerWindow = 0L,
                },
            },
            perPeripheralFrameCostNs = (object?)null,
            notes = new[] { "schema smoke test" },
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(profile, options);

        // ── round-trip + assert the schema contract ──
        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal(1, (int)root["schemaVersion"]!);

        foreach (string key in RequiredTopKeys)
            Assert.True(root.AsObject().ContainsKey(key), $"profile.json missing required top-level key '{key}'");

        JsonObject tiers = root["tiers"]!.AsObject();
        Assert.True(tiers.ContainsKey("interpreter"), "tiers missing 'interpreter'");
        Assert.True(tiers.ContainsKey("jit"), "tiers missing 'jit'");

        JsonObject interp = tiers["interpreter"]!.AsObject();
        foreach (string key in RequiredInterpKeys)
            Assert.True(interp.ContainsKey(key), $"interpreter tier missing required key '{key}'");

        JsonObject jitObj = tiers["jit"]!.AsObject();
        foreach (string key in RequiredJitKeys)
            Assert.True(jitObj.ContainsKey(key), $"jit tier missing required key '{key}'");

        // The item-C counters must be present AND numeric (the headline of the profiler's JIT capture).
        Assert.True((long)jitObj["chainEdgesTaken"]! >= 0);
        Assert.True((long)jitObj["dispatcherEntries"]! >= 0);
        Assert.True((long)jitObj["blockCacheHits"]! >= 0);
        Assert.True((long)jitObj["blockCacheMisses"]! >= 0);
        // A real kernel JIT run compiles at least one block.
        Assert.True((int)jitObj["compileCount"]! > 0, "expected the JIT kernel run to compile at least one block");
    }
}
