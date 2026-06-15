using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Zex;

/// <summary>
/// M3.5-2 — the ZEXDOC/ZEXALL integration gate: the composition proof for the Z80 interpreter. TomHarte
/// proved each instruction is right per-T-state; ZEX proves they compose right (flag-after-flag,
/// sequencing — the cases the single-step vectors structurally cannot reach). ZEXALL (all flags incl.
/// the undocumented X/Y) is the strict gate; ZEXDOC (documented flags) is the faster pre-check.
///
/// Staged: the wiring-smoke fact runs on every CI invocation (seconds — proves the harness composes the
/// real binary). The FULL runs (minutes — billions of T-states) are skip-gated to CPUEMULATOR_ZEX=full,
/// mirroring CPUEMULATOR_UAT=full. Binaries are fetched (tools/get-zexall.*); absent → skip (ZexFact).
///
/// The project does not reference Xunit.SkippableFact, so the env-gate uses the early-return form (which
/// xUnit reports as a PASS): a full run with CPUEMULATOR_ZEX unset returns immediately after a note. The
/// binary-absent skip is the ZexFact discovery-time Skip; the env-gate is layered on top of it.
/// </summary>
public class ZexallTests(ITestOutputHelper output)
{
    private const long SmokeBudget = 100_000_000;   // ~enough to clear ZEX init + the first sub-test name
    // A passing ZEXDOC/ZEXALL run is 46,734,975,782 T-states (measured Task 4, ~130 s in Release).
    // 80e9 gives ~1.7x headroom over the actual pass, bounded so a hang FAILS rather than runs forever.
    private const long FullBudget  = 80_000_000_000;

    private static bool FullEnabled =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_ZEX") == "full";

    /// <summary>Wiring smoke (DEFAULT, every CI run, ~1.3 s): load ZEXDOC, run a small bounded budget,
    /// and assert the harness composed the REAL binary — the ZEX banner printed, the first sub-test
    /// NAME printed (via BDOS fn-9 $-string), the progress dots printed (via BDOS fn-2 console-out),
    /// and NO 'ERROR' appeared. This proves the BDOS stub + loader + transcript capture work on the
    /// real binary in seconds, without the multi-minute full sweep.
    ///
    /// NOTE (Task-4 decision, empirically tuned): the FIRST ZEXDOC sub-test (&lt;adc,sbc&gt; hl,..)
    /// needs ~2.2 BILLION T-states to print its first 'OK' (~30 s on this host). Waiting for that 'OK'
    /// on every CI run would violate the fast-default requirement, so the smoke asserts the wiring
    /// signal (banner + sub-test name + dots + no ERROR) reachable in ~100 M T-states, NOT the first
    /// 'OK'. The full-OK correctness proof is the env-gated full run's job (Zexdoc/Zexall_all...).</summary>
    [ZexFact("zexdoc.com")]
    public void Smoke_zexdoc_harness_composes_the_real_binary()
    {
        string path = ZexVectors.TryGetBinaryPath("zexdoc.com")!;
        byte[] com = File.ReadAllBytes(path);
        var host = new CpmBdosHost(com);
        string transcript = host.Run(SmokeBudget);
        output.WriteLine(transcript);

        AssertNoError(transcript);
        // The ZEX startup banner — printed via BDOS fn-9 ($-string at DE).
        Assert.Contains("Z80 instruction exerciser", transcript, StringComparison.Ordinal);
        // The first sub-test name — also fn-9; proves the $-string path on the real binary.
        Assert.Contains("<adc,sbc>", transcript, StringComparison.Ordinal);
        // Progress dots — printed via BDOS fn-2 (console-out char in E); proves the fn-2 path.
        Assert.Contains("...", transcript, StringComparison.Ordinal);
    }

    [ZexFact("zexdoc.com")]
    public void Zexdoc_all_subtests_pass()
    {
        if (!FullEnabled)
        {
            output.WriteLine("skipped — set CPUEMULATOR_ZEX=full to enable the full ZEXDOC run.");
            return;
        }
        RunFull("zexdoc.com");
    }

    [ZexFact("zexall.com")]
    public void Zexall_all_subtests_pass()
    {
        if (!FullEnabled)
        {
            output.WriteLine("skipped — set CPUEMULATOR_ZEX=full to enable the full ZEXALL run.");
            return;
        }
        RunFull("zexall.com");
    }

    private void RunFull(string binary)
    {
        string path = ZexVectors.TryGetBinaryPath(binary)!;
        byte[] com = File.ReadAllBytes(path);
        var host = new CpmBdosHost(com);
        string transcript = host.Run(FullBudget);
        output.WriteLine(transcript);

        Assert.True(host.Terminated,
            $"{binary} did not terminate within {FullBudget} cycles — PC stuck or an infinite loop:\n{Tail(transcript)}");
        AssertNoError(transcript);
        // ZEX prints a completion banner when all sub-tests finish ("Tests complete").
        Assert.Contains("complete", transcript, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fail if ANY sub-test reported ERROR. ZEX prints "&lt;name&gt; ERROR &lt;crc&gt; &lt;expected&gt;"
    /// for a failure; "&lt;name&gt; OK" for a pass. The gate is zero ERROR lines.</summary>
    private static void AssertNoError(string transcript)
    {
        if (transcript.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            Assert.Fail("ZEX reported a failing sub-test (composition bug):\n" + transcript);
    }

    private static string Tail(string s) =>
        s.Length <= 400 ? s : "..." + s[^400..];
}
