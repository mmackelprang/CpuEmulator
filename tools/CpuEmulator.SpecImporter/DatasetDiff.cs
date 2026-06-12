namespace CpuEmulator.SpecImporter;

/// <summary>
/// A single field-level disagreement between two datasets for the same opcode.
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
/// Row-by-row field comparison of two opcode datasets, keyed by opcode hex string.
/// Compares: mnemonic, mode, bytes, cycles, pageCrossPenalty.
/// The 'source' field is intentionally excluded — provenance citations are
/// expected to differ between independent extraction sources; that is the point.
/// </summary>
public static class DatasetDiff
{
    public static DiffResult Compare(OpcodeEntry[] left, OpcodeEntry[] right)
    {
        var leftMap  = left.ToDictionary(e => e.Opcode,  StringComparer.OrdinalIgnoreCase);
        var rightMap = right.ToDictionary(e => e.Opcode, StringComparer.OrdinalIgnoreCase);

        var missing = leftMap.Keys
            .Except(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extra = rightMap.Keys
            .Except(leftMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var disagreements = new List<FieldDisagreement>();
        var commonOpcodes = leftMap.Keys
            .Intersect(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var opcode in commonOpcodes)
        {
            var l = leftMap[opcode];
            var r = rightMap[opcode];

            if (l.Mnemonic != r.Mnemonic)
                disagreements.Add(new(opcode, "mnemonic", l.Mnemonic, r.Mnemonic));

            if (l.Mode != r.Mode)
                disagreements.Add(new(opcode, "mode", l.Mode, r.Mode));

            if (l.Bytes != r.Bytes)
                disagreements.Add(new(opcode, "bytes",
                    l.Bytes.ToString(), r.Bytes.ToString()));

            if (l.Cycles != r.Cycles)
                disagreements.Add(new(opcode, "cycles",
                    l.Cycles.ToString(), r.Cycles.ToString()));

            if (l.PageCrossPenalty != r.PageCrossPenalty)
                disagreements.Add(new(opcode, "pageCrossPenalty",
                    l.PageCrossPenalty.ToString(), r.PageCrossPenalty.ToString()));
        }

        return new DiffResult(disagreements, missing, extra);
    }
}
