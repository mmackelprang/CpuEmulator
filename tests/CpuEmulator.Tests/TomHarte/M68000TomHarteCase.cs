using System.IO.Compression;
using System.Text.Json;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>One RAM cell (32-bit address, byte value) — the 680x0 ram array is [addr, value] pairs.</summary>
internal sealed record M68000Ram(uint Address, byte Value);

/// <summary>
/// One 680x0 bus slot. Pinned at Task 1 against the live SingleStepTests/680x0 (68000/v1) data, the
/// transactions array carries two tuple shapes:
/// <list type="bullet">
/// <item>an IDLE slot <c>["n", cycles]</c> (length 2) — no bus access (<see cref="IsIdle"/> true); and</item>
/// <item>a BUS access <c>[dir, cycles, fc, addr, sizeTag, value]</c> (length 6) — dir is "r"/"w".</item>
/// </list>
/// <see cref="Cycles"/> (field 2) is the per-slot CYCLE COUNT — CONFIRMED, not the ADR-flagged unknown:
/// the case's top-level "length" equals the sum of <see cref="Cycles"/> over its transactions. The 68000
/// bus is 16-bit, so <see cref="SizeTag"/> is only ".b"/".w" at the bus level (a .l access decomposes into
/// two .w transactions). The per-transaction bus-trace ASSERTION (incl. the cycle total) is M4.5; M4.4b
/// parses it losslessly.
/// </summary>
internal sealed record M68000Transaction(
    bool IsRead, bool IsIdle, int Cycles, int FunctionCode, uint Address, string SizeTag, uint Value)
{
    public override string ToString() => IsIdle
        ? $"idle ({Cycles}c)"
        : $"{(IsRead ? "R" : "W")}{SizeTag} {Address:X6}={Value:X} (fc {FunctionCode}, {Cycles}c)";
}

/// <summary>
/// One 680x0 processor state: the 32-bit data/address registers (D[0..7]/A[0..6]), the SEPARATE usp/ssp
/// (NOT a7 — ADR 0003 §1.4, confirmed in the upstream JSON keys), the 16-bit sr, the 32-bit pc, the 2-word
/// prefetch queue, and ram. The prefetch queue is the load-bearing new dimension (checked in BOTH initial
/// and final — M4.5 asserts the final).
/// </summary>
internal sealed record M68000State(
    uint[] D, uint[] A, uint Usp, uint Ssp, ushort Sr, uint Pc, ushort[] Prefetch, M68000Ram[] Ram);

/// <summary><see cref="Length"/> is the case's top-level total cycle count (== the sum of each
/// transaction's <see cref="M68000Transaction.Cycles"/>); the cycle-count ASSERTION is M4.5.</summary>
internal sealed record M68000TomHarteCase(
    string Name, M68000State Initial, M68000State Final, int Length, M68000Transaction[] Transactions);

/// <summary>
/// The SingleStepTests/680x0 loader. STRUCTURALLY NEW vs the 6502/Z80 loaders: the files are GZIP-compressed
/// (*.json.gz) and MNEMONIC+SIZE-keyed (ADD.b.json.gz), and the schema carries the 2-word prefetch queue +
/// word-granular .b/.w transactions + a per-slot cycle count. Mirrors Z80TomHarteLoader's streaming shape;
/// the ONLY core delta is the GZipStream decompress before JsonDocument.Parse.
/// </summary>
internal static class M68000TomHarteLoader
{
    /// <summary>Load a gzipped 680x0 vector file (*.json.gz) into its case list.</summary>
    public static List<M68000TomHarteCase> LoadFile(string path, int maxCases = int.MaxValue)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);   // the ONLY core delta vs Z80
        using var doc = JsonDocument.Parse(gz);
        var cases = new List<M68000TomHarteCase>(capacity: Math.Min(maxCases, 1024));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (cases.Count >= maxCases) break;   // lever 1: stop parsing once the sample is satisfied
            cases.Add(Parse(element));
        }
        return cases;
    }

    public static M68000TomHarteCase Parse(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        ReadState(element.GetProperty("initial")),
        ReadState(element.GetProperty("final")),
        element.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number ? len.GetInt32() : 0,
        [.. element.GetProperty("transactions").EnumerateArray().Select(ReadTransaction)]);

    // Read numbers via GetInt64 + unchecked cast rather than GetUInt32, so a value the upstream tooling
    // happened to write as a signed/negative decimal (e.g. -1 for 0xFFFFFFFF) parses losslessly instead of
    // throwing — the parse must be robust against any in-range bit pattern the corpus encodes.
    private static uint NumU32(JsonElement v) => unchecked((uint)v.GetInt64());

    private static uint U32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? NumU32(v) : 0u;

    private static ushort U16(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (ushort)NumU32(v) : (ushort)0;

    private static M68000State ReadState(JsonElement e)
    {
        var d = new uint[8];
        for (int i = 0; i < 8; i++) d[i] = U32(e, $"d{i}");
        var a = new uint[7];
        for (int i = 0; i < 7; i++) a[i] = U32(e, $"a{i}");

        ushort[] prefetch = e.TryGetProperty("prefetch", out var pf) && pf.ValueKind == JsonValueKind.Array
            ? [.. pf.EnumerateArray().Select(x => (ushort)NumU32(x))]
            : [0, 0];

        M68000Ram[] ram = e.TryGetProperty("ram", out var r) && r.ValueKind == JsonValueKind.Array
            ? [.. r.EnumerateArray().Select(static pair =>
              {
                  using var items = pair.EnumerateArray();
                  items.MoveNext(); uint address = NumU32(items.Current);
                  items.MoveNext(); byte value = (byte)(NumU32(items.Current) & 0xFF);
                  return new M68000Ram(address, value);
              })]
            : [];

        return new M68000State(d, a, U32(e, "usp"), U32(e, "ssp"), U16(e, "sr"), U32(e, "pc"), prefetch, ram);
    }

    /// <summary>Parse one transaction tuple by POSITION, tolerant of length variation: an idle slot is
    /// ["n", cycles] (length 2) and a bus access is [dir, cycles, fc, addr, sizeTag, value] (length 6).
    /// Field 2 (cycles) is the per-slot cycle count; the size tag carries its leading dot (".b"/".w").</summary>
    private static M68000Transaction ReadTransaction(JsonElement tuple)
    {
        var items = tuple.EnumerateArray().ToArray();
        string dir   = items.Length > 0 ? items[0].GetString() ?? "n" : "n";
        int cycles   = items.Length > 1 && items[1].ValueKind == JsonValueKind.Number ? items[1].GetInt32() : 0;
        int fc       = items.Length > 2 && items[2].ValueKind == JsonValueKind.Number ? items[2].GetInt32() : 0;
        uint addr    = items.Length > 3 && items[3].ValueKind == JsonValueKind.Number ? NumU32(items[3]) : 0u;
        string sz    = items.Length > 4 ? items[4].GetString() ?? "" : "";
        uint val     = items.Length > 5 && items[5].ValueKind == JsonValueKind.Number ? NumU32(items[5]) : 0u;
        bool isIdle  = dir == "n";
        return new M68000Transaction(IsRead: dir == "r", IsIdle: isIdle, cycles, fc, addr, sz, val);
    }
}
