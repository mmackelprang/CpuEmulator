using System.Text.Json;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the SingleStepTests 8088 vector directory (<c>&lt;cache&gt;/8088/v2</c>) plus the skip-at-discovery
/// attributes. This is the FIRST pinned divergence from the 680x0 resolver (ADR 0006 Decision 5): the 8088 set
/// is HEX-keyed gzip (<c>00.json.gz</c>, <c>88.json.gz</c>, …) — closer to the 6502/Z80 layout than the
/// 680x0's mnemonic+size keying — but still GZIP-compressed (like 680x0). Fetch with
/// <c>tools/get-test-vectors-8088.ps1</c>, or set <c>CPUEMULATOR_TESTVECTORS</c>.
/// </summary>
internal static class M8088TomHarteVectors
{
    /// <summary>The cache root: the CPUEMULATOR_TESTVECTORS override, else ~/.cache/cpuemulator/vectors.</summary>
    private static string CacheRoot() =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");

    public static string? TryGetVectorDirectory() => ResolveVectorDirectory(CacheRoot());

    /// <summary>Resolve <c>&lt;root&gt;/8088/v2</c>, returning null when absent. Pure (no env read) so tests can
    /// exercise the path logic with an explicit root WITHOUT mutating the process-global CPUEMULATOR_TESTVECTORS
    /// — a mutation would race the vector-gated theories that read it in parallel.</summary>
    public static string? ResolveVectorDirectory(string root)
    {
        string dir = Path.Combine(root, "8088", "v2");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The per-file case-sample cap for the 8088 TomHarte sweeps. Mirrors the 6502/Z80/680x0
    /// convention: routine/CI runs cap the per-file case loop at CPUEMULATOR_TOMHARTE_SAMPLE (default 200);
    /// CPUEMULATOR_UAT=full removes the cap (int.MaxValue) so the milestone merge gate runs the full
    /// 10,000-case-per-file sweep. Caps the per-file case loop ONLY — it does NOT change which files run,
    /// which cases are deferred/filtered, or what is asserted.</summary>
    public static int ResolveSampleSize() => TomHarteSampling.ResolveSampleSize();
}

/// <summary>
/// The THIRD pinned divergence from the 680x0 loader (ADR 0006 Decision 5): MASK-AWARE flags. The 8088's
/// undefined flag bits vary by silicon, so the upstream <c>metadata.json</c> publishes a per-opcode
/// <c>flags-mask</c> — the set of flag bits that are DEFINED (compare only those). A flag comparison ANDs both
/// the expected and the actual <c>flags</c> word with this mask before asserting, so undefined bits never cause
/// a spurious mismatch.
///
/// <para><b>Layout (pinned against the live v2 metadata).</b> The file is a top-level object with an
/// <c>opcodes</c> dict keyed by the 2-hex-digit opcode. An entry is EITHER a leaf
/// <c>{"status": "...", "flags-mask": NNNNN}</c> OR — for an opcode GROUP (ModR/M <c>reg</c>-extended
/// opcodes like <c>80</c>/<c>81</c>/<c>F6</c>/<c>FF</c>) — a <c>{"reg": {"0": {...}, "1": {"flags-mask": NNNNN},
/// …}}</c> wrapper whose inner entries are keyed by the ModR/M <c>reg</c> field. A MISSING <c>flags-mask</c>
/// (at either level) ⇒ the mask is <c>0xFFFF</c> (compare ALL bits).</para>
///
/// <para><b>Location + skip-tolerance.</b> <c>metadata.json</c> lives in the SAME directory as the vectors
/// (<c>&lt;cache&gt;/8088/v2/metadata.json</c>), NOT the repo root. Loading is SKIP-TOLERANT: an absent file
/// (or an absent opcode/reg entry) yields the <c>0xFFFF</c> default, so the harness never hard-fails on a
/// missing metadata file.</para>
/// </summary>
internal sealed class M8088Metadata
{
    /// <summary>Compare-all-bits default — used for an absent metadata file, an unknown opcode, or any entry
    /// that omits <c>flags-mask</c>.</summary>
    public const ushort DefaultMask = 0xFFFF;

    // opcodeHex -> leaf mask (when the entry is a flat opcode).
    private readonly Dictionary<string, ushort> _leaf = new(StringComparer.OrdinalIgnoreCase);
    // opcodeHex -> (regField -> mask) (when the entry is a `reg` group).
    private readonly Dictionary<string, Dictionary<int, ushort>> _group = new(StringComparer.OrdinalIgnoreCase);

    private M8088Metadata() { }

    /// <summary>An empty metadata table — every <see cref="FlagsMask"/> query returns <see cref="DefaultMask"/>.
    /// The skip-tolerant fallback when <c>metadata.json</c> is absent.</summary>
    public static M8088Metadata Empty { get; } = new();

    /// <summary>Load <c>&lt;dir&gt;/metadata.json</c> (SAME directory as the vectors). Skip-tolerant: a null or
    /// nonexistent directory, or a missing file, returns <see cref="Empty"/> rather than throwing.</summary>
    public static M8088Metadata Load(string? vectorDirectory)
    {
        if (vectorDirectory is null) return Empty;
        string path = Path.Combine(vectorDirectory, "metadata.json");
        if (!File.Exists(path)) return Empty;
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        return Parse(doc.RootElement);
    }

    /// <summary>Parse a metadata root element (the top-level object with an <c>opcodes</c> dict). Exposed so a
    /// parse-proof test can build the table from an inline JSON string without a file.</summary>
    public static M8088Metadata Parse(JsonElement root)
    {
        var m = new M8088Metadata();
        if (!root.TryGetProperty("opcodes", out var opcodes) || opcodes.ValueKind != JsonValueKind.Object)
            return m;

        foreach (var entry in opcodes.EnumerateObject())
        {
            JsonElement v = entry.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;

            if (v.TryGetProperty("reg", out var reg) && reg.ValueKind == JsonValueKind.Object)
            {
                var byReg = new Dictionary<int, ushort>();
                foreach (var sub in reg.EnumerateObject())
                {
                    if (int.TryParse(sub.Name, out int regField) && sub.Value.ValueKind == JsonValueKind.Object)
                        byReg[regField] = MaskOf(sub.Value);
                }
                m._group[entry.Name] = byReg;
            }
            else
            {
                m._leaf[entry.Name] = MaskOf(v);
            }
        }
        return m;
    }

    /// <summary>Read a leaf entry's <c>flags-mask</c>, defaulting to <see cref="DefaultMask"/> when absent.</summary>
    private static ushort MaskOf(JsonElement entry) =>
        entry.TryGetProperty("flags-mask", out var fm) && fm.ValueKind == JsonValueKind.Number
            ? unchecked((ushort)fm.GetInt64())
            : DefaultMask;

    /// <summary>
    /// The defined-flag mask for an opcode (and, for an opcode group, the ModR/M <c>reg</c> field). A flat
    /// opcode ignores <paramref name="regField"/>; a group opcode looks up <paramref name="regField"/> in its
    /// <c>reg</c> sub-dict (and falls back to <see cref="DefaultMask"/> when the reg field is absent or null).
    /// An unknown opcode ⇒ <see cref="DefaultMask"/>.
    /// </summary>
    public ushort FlagsMask(string opcodeHex, int? regField)
    {
        if (_leaf.TryGetValue(opcodeHex, out ushort leaf))
            return leaf;
        if (_group.TryGetValue(opcodeHex, out var byReg))
        {
            if (regField is int rf && byReg.TryGetValue(rf, out ushort gm))
                return gm;
            return DefaultMask;
        }
        return DefaultMask;
    }

    /// <summary>AND a <c>flags</c> word with a defined-flag mask — the single comparison-path helper. With the
    /// <see cref="DefaultMask"/> (0xFFFF) it is the identity, so the mask never weakens a fully-defined opcode's
    /// flag assertion.</summary>
    public static ushort ApplyFlagsMask(ushort flags, ushort mask) => (ushort)(flags & mask);
}

/// <summary>TheoryAttribute that skips the whole theory at discovery when the 8088 vectors are absent — the
/// same skip-when-absent discipline as the 6502/Z80/680x0 harness.</summary>
public sealed class M8088TomHarteTheoryAttribute : TheoryAttribute
{
    public M8088TomHarteTheoryAttribute()
    {
        if (M8088TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "8088 TomHarte vectors not found — run tools/get-test-vectors-8088.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

/// <summary>FactAttribute that skips at discovery when the 8088 vectors are absent — the Fact-shaped twin of
/// <see cref="M8088TomHarteTheoryAttribute"/>, for per-file split sweep classes (one vector file == one Fact
/// == one xUnit collection, so the files distribute across cores). Identical skip logic; zero semantics
/// change.</summary>
public sealed class M8088TomHarteFactAttribute : FactAttribute
{
    public M8088TomHarteFactAttribute()
    {
        if (M8088TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "8088 TomHarte vectors not found — run tools/get-test-vectors-8088.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
