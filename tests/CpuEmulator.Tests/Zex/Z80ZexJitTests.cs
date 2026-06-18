using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Zex;

/// <summary>M3.5-3a integration parity proof: the CP/M BDOS host drives the Z80 through
/// JittedCpu&lt;Z80Cpu&gt; (the same CpmBdosHost as M3.5-2, with useJit: true). In 5-3a every Z80 op
/// falls back to inner.Step, so a JIT run is byte-identical to the interpreter run — this proves the
/// generic compiler runs a real CP/M program (and, env-gated, the 46-billion-T-state ZEXDOC/ZEXALL
/// exercisers — the heaviest possible composition load) with identical results.
///
/// Staged like M3.5-2: a fast WIRING smoke runs on every CI invocation (a tiny hand-assembled .com
/// driven through the JIT — proves the JIT-driven host + the BDOS-at-PC-0x0005 intercept compose,
/// without the multi-minute full sweep). The FULL ZEXDOC/ZEXALL-through-JIT runs are skip-gated to
/// CPUEMULATOR_ZEX=full (the M3.5-2 env-gate precedent). The all-fallback op-granular JIT drive (one
/// jit.Run(budget=1) per op) adds dispatcher overhead per op over the ~130 s interpreter ZEX run, so
/// the full run is generously budgeted and env-gated, never on the default CI path.</summary>
public class Z80ZexJitTests(ITestOutputHelper output)
{
    private const long FullBudget = 80_000_000_000;   // ~1.7x headroom over the 46.7e9-T-state pass

    private static bool FullEnabled =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_ZEX") == "full";

    /// <summary>Wiring smoke (DEFAULT, every CI run, fast): a tiny hand-assembled CP/M .com driven
    /// THROUGH JittedCpu&lt;Z80Cpu&gt; — prints "OK" via BDOS fn-9 then warm-boots. Proves the JIT-driven
    /// host advances correctly AND the BDOS-at-PC-0x0005 intercept fires under the JIT (the CALL 0x0005
    /// is a fallback Step that sets PC=0x0005, surfaced exactly by the budget-1 JIT drive). The real
    /// ZEXDOC/ZEXALL-through-JIT correctness is the env-gated full run's job.</summary>
    [Fact]
    public void Smoke_jit_driven_host_services_BDOS_and_terminates()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // JIT-only proof; skip where dynamic code is disabled (AOT)

        // 0100: 11 0B 01  LD DE,0x010B (the string address)
        // 0103: 0E 09     LD C,0x09 (BDOS fn 9 = print $-string)
        // 0105: CD 05 00  CALL 0x0005
        // 0108: C3 00 00  JP 0x0000 (warm boot)
        // 010B: "OK" '$'  the $-terminated string
        byte[] com =
        {
            0x11, 0x0B, 0x01, 0x0E, 0x09, 0xCD, 0x05, 0x00,
            0xC3, 0x00, 0x00,
            (byte)'O', (byte)'K', (byte)'$',
        };

        var host = new CpmBdosHost(com, useJit: true);
        Assert.True(host.UsesJit, "the smoke must drive the CPU through the JIT");
        string transcript = host.Run(cycleBudget: 1_000_000);
        output.WriteLine(transcript);

        Assert.Equal("OK", transcript);
        Assert.True(host.Terminated, "the program should reach warm boot (PC == 0x0000) through the JIT");
    }

    // Triage budget: enough to clear ZEX init and run the first several sub-tests to an OK/ERROR verdict (a few
    // billion T-states), NOT the full ~46.7e9-T-state pass. ZEXALL-through-JIT (the strict superset) is the
    // authoritative composition gate (Zexall_passes_through_the_JIT); ZEXDOC-JIT-full is redundant with it, so
    // ZEXDOC-JIT is the fast triage signal.
    private const long ZexdocTriageBudget = 5_000_000_000;

    [ZexFact("zexdoc.com")]
    public void Zexdoc_triage_precheck_through_the_JIT()
    {
        if (!FullEnabled)
        {
            output.WriteLine("skipped — set CPUEMULATOR_ZEX=full to enable the ZEXDOC-through-JIT triage pre-check.");
            return;
        }
        string path = ZexVectors.TryGetBinaryPath("zexdoc.com")!;
        var host = new CpmBdosHost(File.ReadAllBytes(path), useJit: true);
        string transcript = host.Run(ZexdocTriageBudget);
        output.WriteLine(transcript);
        // Triage gate: any ERROR in the cleared sub-tests fails fast (cheaper than waiting for the full ZEXALL).
        if (transcript.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            Assert.Fail("ZEX reported a failing sub-test through the JIT (tier-parity bug):\n" + transcript);
    }

    [ZexFact("zexall.com")]
    public void Zexall_passes_through_the_JIT()
    {
        if (!FullEnabled)
        {
            output.WriteLine("skipped — set CPUEMULATOR_ZEX=full to enable the full ZEXALL-through-JIT run.");
            return;
        }
        RunFullThroughJit("zexall.com");
    }

    private void RunFullThroughJit(string binary)
    {
        string path = ZexVectors.TryGetBinaryPath(binary)!;
        byte[] com = File.ReadAllBytes(path);
        var host = new CpmBdosHost(com, useJit: true);
        string transcript = host.Run(FullBudget);
        output.WriteLine(transcript);

        Assert.True(host.Terminated,
            $"{binary} did not terminate within {FullBudget} cycles through the JIT — PC stuck or an " +
            $"infinite loop:\n{Tail(transcript)}");
        if (transcript.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            Assert.Fail("ZEX reported a failing sub-test through the JIT (tier-parity bug):\n" + transcript);
        Assert.Contains("complete", transcript, StringComparison.OrdinalIgnoreCase);
    }

    private static string Tail(string s) => s.Length <= 400 ? s : "..." + s[^400..];
}
