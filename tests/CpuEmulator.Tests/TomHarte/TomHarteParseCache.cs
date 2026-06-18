using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>A path-keyed parse cache for the TomHarte loaders (lever 1). The SAME vector file is parsed once per
/// sweep today — the interpreter sweep, the JIT sweep, and (on the 680x0) up to ~5-8 axis classes each call
/// LoadFile(path). This cache parses a path ONCE (to the requested sample size) and reuses the parsed list across
/// all sweeps in the run. Keyed by path; stores the high-water sample size it was parsed to, so a later request
/// for MORE cases (e.g. CPUEMULATOR_UAT=full → int.MaxValue) re-parses to the larger size and upgrades the entry,
/// while a request for the same-or-fewer cases is a pure hit (the caller's own `if (run >= sampleSize) break;`
/// loop caps a wider cached list down to its sample — verified: every sweep already has that loop).
///
/// <para>Thread-safe (the sweeps run in parallel collections): a ConcurrentDictionary holds a per-path lock object
/// so two threads racing the SAME path parse once, not twice; different paths never contend.</para></summary>
internal sealed class TomHarteParseCache<TCase>
{
    private sealed class Entry { public List<TCase>? Cases; public int Water; }

    private readonly ConcurrentDictionary<string, Entry> _byPath = new();

    /// <summary>Return at least <paramref name="maxCases"/> parsed cases for <paramref name="path"/> (or the whole
    /// file if it has fewer), parsing via <paramref name="parse"/> only when the cache cannot satisfy the request.
    /// The returned list may be WIDER than maxCases (a cached larger parse) — the caller caps with its own loop.</summary>
    public List<TCase> Get(string path, int maxCases, Func<int, List<TCase>> parse)
    {
        var entry = _byPath.GetOrAdd(path, static _ => new Entry());
        lock (entry)
        {
            if (entry.Cases is not null && entry.Water >= maxCases)
                return entry.Cases;
            // Cache miss or the cached parse is too small → parse to the requested size and upgrade.
            entry.Cases = parse(maxCases);
            // Water = the requested cap, NOT the returned count: when the file is shorter than maxCases the parse
            // returns fewer than maxCases, but a later equal request is still a hit (the file can't yield more).
            entry.Water = maxCases;
            return entry.Cases;
        }
    }
}

/// <summary>The four concrete singletons (one per case type), so a sweep just calls the cache for its CPU.</summary>
internal static class TomHarteCaches
{
    public static readonly TomHarteParseCache<TomHarteCase> Mos6502 = new();
    public static readonly TomHarteParseCache<Z80TomHarteCase> Z80 = new();
    public static readonly TomHarteParseCache<M68000TomHarteCase> M68000 = new();

    // NOTE: keyed by PATH ONLY. All current 8088 sweeps are DATA-axis (they fetch via
    // LoadFile(path, max, parseCycles: false)). If a TIMING-axis 8088 sweep is ever added that needs the carried
    // cycle tuples (parseCycles: true) AND can run in the same process as the data-axis sweeps, this cache must be
    // re-keyed on (path, parseCycles) so a cycles-free cached list is never served to a timing-axis caller (or
    // vice-versa). Out of scope for PR-T1 — pinned here so the constraint lives in code, not only in the plan.
    public static readonly TomHarteParseCache<M8088TomHarteCase> M8088 = new();
}
