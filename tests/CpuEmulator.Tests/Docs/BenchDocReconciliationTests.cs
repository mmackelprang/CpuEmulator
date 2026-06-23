using System.IO;
using Xunit;

namespace CpuEmulator.Tests.Docs;

/// <summary>Pins the 68000 bench doc-reconciliation (roadmap #3): the stale claims are gone, the resolution +
/// citations are present, and the cited evidence still exists in source — so the docs can't silently re-rot.
/// Doc-only gate (B68-DOC); no behavior under test.</summary>
public class BenchDocReconciliationTests
{
    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), relative));

    [Fact]
    public void The_stale_W3_missing_claims_are_gone()
    {
        string roadmap = Read("docs/ROADMAP.md");
        string bench = Read("docs/user-guide/benchmarks.md");
        // The exact stale phrasings (verified present pre-edit) must be absent post-edit.
        Assert.DoesNotContain("covers 68000 W1/W2 but not W3", roadmap);
        Assert.DoesNotContain("absence from the hot-op profiler arm", bench);
    }

    [Fact]
    public void The_resolution_and_its_citations_are_recorded()
    {
        string roadmap = Read("docs/ROADMAP.md");
        string bench = Read("docs/user-guide/benchmarks.md");
        // Both docs tie the resolution to its evidence: the W3-shipped commit AND the DECISION T2 reference.
        Assert.Contains("bc68ee7", roadmap);
        Assert.Contains("DECISION T2", roadmap);
        Assert.Contains("bc68ee7", bench);
        Assert.Contains("DECISION T2", bench);
    }

    [Fact]
    public void The_cited_evidence_still_exists_in_source()
    {
        // If a future change removes the W3 arm or the W2 gate, this fails — forcing the doc to re-reconcile.
        string profiler = Read("bench/hotop-profiler/Profiler.cs");
        Assert.Contains("Run68000(\"W3 sieve-kernel\"", profiler);

        // Pin the citation precisely (pre-merge review): the "## 68000 — W3 sieve-kernel" hot-op block lives in
        // hotop-profile-results.txt (NOT REPORT.md, which carries the m68k-W3 throughput rows) — so the doc's
        // file attribution can't silently drift back to the wrong artifact.
        string hotop = Read("bench/hotop-profiler/hotop-profile-results.txt");
        Assert.Contains("## 68000 — W3 sieve-kernel", hotop);
        string report = Read("bench/results/REPORT.md");
        Assert.Contains("m68k-W3 sieve-kernel", report);

        string benchSmoke = Read("tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs");
        Assert.Contains("DECISION T2", benchSmoke);
        Assert.Contains("<= 16", benchSmoke);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
