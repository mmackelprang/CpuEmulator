namespace CpuEmulator.SpecImporter;

/// <summary>
/// A single field-level disagreement between two datasets for the same plane-qualified key.
/// The <see cref="Opcode"/> field carries the plane-qualified <see cref="OpcodeEntry.Key"/>
/// ("0xNN" for a base-plane row, "0xPREFIX:0xNN" for a prefixed row) — so a printed table shows
/// "0xED:0xB0" for an ED-plane disagreement, distinct from a base "0xB0".
/// </summary>
public sealed record FieldDisagreement(
    string Opcode,
    string Field,
    string Left,
    string Right);

/// <summary>
/// The result of comparing two opcode datasets.
/// </summary>
public sealed record DiffResult(
    IReadOnlyList<FieldDisagreement> Disagreements,
    IReadOnlyList<string>            MissingInOther,
    IReadOnlyList<string>            ExtraInOther)
{
    /// <summary>True when any difference exists (disagreements, missing, or extra opcodes).</summary>
    public bool HasDifferences =>
        Disagreements.Count > 0 || MissingInOther.Count > 0 || ExtraInOther.Count > 0;
}

/// <summary>
/// Row-by-row field comparison of two opcode datasets, keyed by the PLANE-QUALIFIED
/// <see cref="OpcodeEntry.Key"/> (M3.3 fix — was the bare opcode hex string).
/// Compares: mnemonic, mode, bytes, cycles, pageCrossPenalty.
/// The 'source' field is intentionally excluded — provenance citations are
/// expected to differ between independent extraction sources; that is the point.
///
/// Why the Key (not the bare Opcode): the Z80 reuses opcode bytes across prefix planes — base 0xB0
/// (OR B) and ED 0xB0 (LDIR) are different instructions sharing the byte 0xB0. Keying on the bare
/// Opcode would collide them and report a spurious OR-vs-LDIR disagreement. The Key ("0xED:0xB0" vs
/// "0xB0") keeps the planes distinct. For a 6502 row (null prefix) the Key IS the bare Opcode, so the
/// 6502 diff behavior is byte-identical.
/// </summary>
public static class DatasetDiff
{
    public static DiffResult Compare(OpcodeEntry[] left, OpcodeEntry[] right)
    {
        var leftMap  = left.ToDictionary(e => e.Key,  StringComparer.OrdinalIgnoreCase);
        var rightMap = right.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

        var missing = leftMap.Keys
            .Except(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extra = rightMap.Keys
            .Except(leftMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var disagreements = new List<FieldDisagreement>();
        var commonKeys = leftMap.Keys
            .Intersect(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var key in commonKeys)
        {
            var l = leftMap[key];
            var r = rightMap[key];

            if (l.Mnemonic != r.Mnemonic)
                disagreements.Add(new(key, "mnemonic", l.Mnemonic, r.Mnemonic));

            if (l.Mode != r.Mode)
                disagreements.Add(new(key, "mode", l.Mode, r.Mode));

            if (l.Bytes != r.Bytes)
                disagreements.Add(new(key, "bytes",
                    l.Bytes.ToString(), r.Bytes.ToString()));

            if (l.Cycles != r.Cycles)
                disagreements.Add(new(key, "cycles",
                    l.Cycles.ToString(), r.Cycles.ToString()));

            if (l.PageCrossPenalty != r.PageCrossPenalty)
                disagreements.Add(new(key, "pageCrossPenalty",
                    l.PageCrossPenalty.ToString(), r.PageCrossPenalty.ToString()));
        }

        return new DiffResult(disagreements, missing, extra);
    }
}
