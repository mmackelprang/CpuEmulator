using System.IO.Compression;
using System.Text.Json;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>One RAM cell — a 20-bit PHYSICAL address (up to 0xFFFFF) + a byte value. The 8088 v2 ram array
/// is <c>[addr, value]</c> pairs and is UNSORTED (access order — a v2 change). <c>final.ram</c> lists only
/// the cells that CHANGED.</summary>
internal sealed record M8088Ram(uint Address, byte Value);

/// <summary>
/// One 8088 processor register file. The FULL 14-register state is the 8088 program model: the four 16-bit
/// general registers (<c>ax bx cx dx</c>), the four segment registers (<c>cs ss ds es</c>), the two pointer
/// registers (<c>sp bp</c>), the two index registers (<c>si di</c>), the instruction pointer (<c>ip</c>),
/// and the combined 16-bit <c>flags</c> word (NOT split per-flag).
///
/// <para><b>The sparse-final divergence.</b> <c>initial.regs</c> always carries ALL 14 keys; <c>final.regs</c>
/// is SPARSE — only the registers that CHANGED appear (and the whole 16-bit <c>flags</c> appears if ANY flag
/// changed). <see cref="Present"/> records, per register, whether the key was present in the parsed object, so
/// the case can MERGE a sparse final over the full initial (<see cref="M8088TomHarteCase.MergedFinalRegs"/>)
/// rather than treating an absent key as zero. For an <c>initial</c> object every key is present; for a
/// <c>final</c> object only the changed keys are.</para>
/// </summary>
internal sealed record M8088Regs(
    ushort Ax, ushort Bx, ushort Cx, ushort Dx,
    ushort Cs, ushort Ss, ushort Ds, ushort Es,
    ushort Sp, ushort Bp, ushort Si, ushort Di,
    ushort Ip, ushort Flags,
    M8088Regs.Presence Present)
{
    /// <summary>Per-register present-in-the-JSON flags. An <c>initial.regs</c> object sets all 14; a sparse
    /// <c>final.regs</c> object sets only the keys it carried — this is the merge's load-bearing input.</summary>
    internal sealed record Presence(
        bool Ax, bool Bx, bool Cx, bool Dx,
        bool Cs, bool Ss, bool Ds, bool Es,
        bool Sp, bool Bp, bool Si, bool Di,
        bool Ip, bool Flags);
}

/// <summary>
/// One 8088 cycle (a per-CLOCK entry; the <c>cycles</c> array is per-clock, NOT per-bus-transaction). Each
/// upstream entry is an 11-element tuple:
/// <c>[pin-bitfield, mux-bus(20-bit addr/data), seg-status, mem-status, io-status, BHE, data-bus,
/// bus-status, T-state, queue-op, queue-byte]</c> (README + ADR 0006 Decision 6). Parsed LOSSLESSLY but
/// CARRIED-NOT-ASSERTED — the data axis (regs+ram) ignores it; the cycle/queue timing axis is M5.5e. The
/// raw field types are mixed (numbers + status strings), so each field is held as the parsed
/// <see cref="JsonElement"/>'s string-or-number rendering via <see cref="object"/>.
/// </summary>
internal sealed record M8088Cycle(object?[] Fields)
{
    public int Count => Fields.Length;
}

/// <summary>One 8088 processor state: the full register file, the (unsorted) ram cells, and the BIU prefetch
/// QUEUE (0..4 bytes; may be empty). The queue is the timing-axis dimension (ADR 0006 Decision 6) — parsed
/// but carried; the data-axis runner IGNORES it.</summary>
internal sealed record M8088State(M8088Regs Regs, M8088Ram[] Ram, byte[] Queue);

/// <summary>
/// One SingleStepTests/8088 v2 case. Top-level: <c>name</c> (disassembly), <c>bytes</c> (raw instruction
/// bytes), <c>initial</c>, <c>final</c>, <c>cycles</c> (per-clock, carried), and the optional <c>hash</c>
/// (SHA1) + <c>idx</c>.
/// </summary>
internal sealed record M8088TomHarteCase(
    string Name, byte[] Bytes, M8088State Initial, M8088State Final, M8088Cycle[] Cycles,
    string? Hash, int? Idx)
{
    /// <summary>
    /// MERGE the sparse <c>final.regs</c> over the full <c>initial.regs</c> — the SECOND pinned divergence from
    /// the 680x0 loader (ADR 0006 Decision 5). Start from the full initial register file and overlay ONLY the
    /// registers the sparse final actually carried (<see cref="M8088Regs.Present"/>); every register the final
    /// omitted keeps its initial value. The result is the full 14-register expected end state — what the M5.5
    /// runner will diff against. The returned record's <see cref="M8088Regs.Present"/> is all-true (it is a
    /// complete state, not a sparse delta).
    /// </summary>
    public M8088Regs MergedFinalRegs()
    {
        var i = Initial.Regs;
        var f = Final.Regs;
        var p = f.Present;
        return new M8088Regs(
            Ax:    p.Ax    ? f.Ax    : i.Ax,
            Bx:    p.Bx    ? f.Bx    : i.Bx,
            Cx:    p.Cx    ? f.Cx    : i.Cx,
            Dx:    p.Dx    ? f.Dx    : i.Dx,
            Cs:    p.Cs    ? f.Cs    : i.Cs,
            Ss:    p.Ss    ? f.Ss    : i.Ss,
            Ds:    p.Ds    ? f.Ds    : i.Ds,
            Es:    p.Es    ? f.Es    : i.Es,
            Sp:    p.Sp    ? f.Sp    : i.Sp,
            Bp:    p.Bp    ? f.Bp    : i.Bp,
            Si:    p.Si    ? f.Si    : i.Si,
            Di:    p.Di    ? f.Di    : i.Di,
            Ip:    p.Ip    ? f.Ip    : i.Ip,
            Flags: p.Flags ? f.Flags : i.Flags,
            Present: AllPresent);
    }

    private static readonly M8088Regs.Presence AllPresent =
        new(true, true, true, true, true, true, true, true, true, true, true, true, true, true);
}

/// <summary>
/// The SingleStepTests/8088 (v2) loader. Shares the 680x0 loader's CORE delta — the files are GZIP-compressed
/// (<c>*.json.gz</c>) so a <see cref="GZipStream"/> decompress precedes <see cref="JsonDocument.Parse(System.IO.Stream, JsonDocumentOptions)"/>
/// — but the schema diverges on three pinned axes (ADR 0006 Decision 5): hex-keyed filenames (handled by the
/// resolver, not here), a SPARSE <c>final.regs</c> (the loader records per-register presence so the case can
/// merge), and mask-aware flags (handled by <see cref="M8088Metadata"/>). The <c>queue</c> + <c>cycles</c> are
/// parsed losslessly but carried-not-asserted (the timing axis, M5.5e).
/// </summary>
internal static class M8088TomHarteLoader
{
    /// <summary>Load a gzipped 8088 vector file (<c>*.json.gz</c>) into its case list, stopping at
    /// <paramref name="maxCases"/> (lever 1). <paramref name="parseCycles"/> defaults to TRUE so every existing
    /// bare caller (the loader parse-proof tests, which assert <c>Cycles.Length</c>) is behaviour-preserving; the
    /// DATA-axis sweeps pass <c>parseCycles: false</c> explicitly to skip the carried-not-asserted 11-field cycle
    /// tuples (the data-axis runner ignores <c>Cycles</c>). A TIMING-axis sweep, if/when it asserts cycles, passes
    /// <c>parseCycles: true</c>.</summary>
    public static List<M8088TomHarteCase> LoadFile(string path, int maxCases = int.MaxValue,
                                                   bool parseCycles = true)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);   // the gzip path (shared with 680x0)
        using var doc = JsonDocument.Parse(gz);
        var cases = new List<M8088TomHarteCase>(capacity: Math.Min(maxCases, 1024));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (cases.Count >= maxCases) break;   // lever 1: stop parsing once the sample is satisfied
            cases.Add(Parse(element, parseCycles));
        }
        return cases;
    }

    public static M8088TomHarteCase Parse(JsonElement element, bool parseCycles = true)
    {
        byte[] bytes = element.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Array
            ? [.. b.EnumerateArray().Select(x => (byte)(NumU32(x) & 0xFF))]
            : [];
        string? hash = element.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString() : null;
        int? idx = element.TryGetProperty("idx", out var ix) && ix.ValueKind == JsonValueKind.Number
            ? ix.GetInt32() : null;
        return new M8088TomHarteCase(
            element.GetProperty("name").GetString()!,
            bytes,
            ReadState(element.GetProperty("initial")),
            ReadState(element.GetProperty("final")),
            parseCycles ? ReadCycles(element) : System.Array.Empty<M8088Cycle>(),
            hash,
            idx);
    }

    // Read numbers via GetInt64 + unchecked cast (the 680x0 loader's robustness helper) so a value the
    // upstream tooling happened to write as a signed decimal still parses losslessly into the bit pattern.
    private static uint NumU32(JsonElement v) => unchecked((uint)v.GetInt64());

    private static M8088State ReadState(JsonElement e)
    {
        JsonElement regsEl = e.GetProperty("regs");
        var regs = ReadRegs(regsEl);

        M8088Ram[] ram = e.TryGetProperty("ram", out var r) && r.ValueKind == JsonValueKind.Array
            ? [.. r.EnumerateArray().Select(static pair =>
              {
                  using var items = pair.EnumerateArray();
                  items.MoveNext(); uint address = NumU32(items.Current);
                  items.MoveNext(); byte value = (byte)(NumU32(items.Current) & 0xFF);
                  return new M8088Ram(address, value);
              })]
            : [];

        byte[] queue = e.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array
            ? [.. q.EnumerateArray().Select(x => (byte)(NumU32(x) & 0xFF))]
            : [];

        return new M8088State(regs, ram, queue);
    }

    /// <summary>Parse a <c>regs</c> object, recording per-register PRESENCE. An <c>initial.regs</c> object has
    /// all 14 keys (all present, all real values); a SPARSE <c>final.regs</c> object has only the changed keys
    /// present — an absent key reads back as 0 in the value field but its presence flag is false, so the merge
    /// never mistakes "absent" for "changed to 0".</summary>
    private static M8088Regs ReadRegs(JsonElement e)
    {
        ushort Get(string name, out bool present)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            {
                present = true;
                return (ushort)NumU32(v);
            }
            present = false;
            return 0;
        }

        ushort ax = Get("ax", out bool pax);
        ushort bx = Get("bx", out bool pbx);
        ushort cx = Get("cx", out bool pcx);
        ushort dx = Get("dx", out bool pdx);
        ushort cs = Get("cs", out bool pcs);
        ushort ss = Get("ss", out bool pss);
        ushort ds = Get("ds", out bool pds);
        ushort es = Get("es", out bool pes);
        ushort sp = Get("sp", out bool psp);
        ushort bp = Get("bp", out bool pbp);
        ushort si = Get("si", out bool psi);
        ushort di = Get("di", out bool pdi);
        ushort ip = Get("ip", out bool pip);
        ushort flags = Get("flags", out bool pflags);

        return new M8088Regs(ax, bx, cx, dx, cs, ss, ds, es, sp, bp, si, di, ip, flags,
            new M8088Regs.Presence(pax, pbx, pcx, pdx, pcs, pss, pds, pes, psp, pbp, psi, pdi, pip, pflags));
    }

    /// <summary>Parse the per-clock <c>cycles</c> array LOSSLESSLY into a carried representation: each entry's
    /// 11 fields become an <c>object?[]</c> (numbers as <see cref="long"/>, status tags as <see cref="string"/>,
    /// anything else as the element's text). The data axis never asserts these; the cycle/queue timing axis is
    /// M5.5e. Tolerant of a missing/absent <c>cycles</c> array (returns empty).</summary>
    private static M8088Cycle[] ReadCycles(JsonElement element)
    {
        if (!element.TryGetProperty("cycles", out var cy) || cy.ValueKind != JsonValueKind.Array)
            return [];
        return [.. cy.EnumerateArray().Select(static entry =>
        {
            object?[] fields = [.. entry.EnumerateArray().Select(static f => f.ValueKind switch
            {
                JsonValueKind.Number => (object?)f.GetInt64(),
                JsonValueKind.String => f.GetString(),
                JsonValueKind.Null   => null,
                _                    => f.GetRawText(),
            })];
            return new M8088Cycle(fields);
        })];
    }
}
