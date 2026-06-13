using System.Collections.Generic;

namespace CpuEmulator.Generators;

/// <summary>The per-spec flag-name → hardware-bit resolver (Ground truth B). Built from an
/// optional declared <c>FlagLayout</c> (the model's <see cref="SpecModel.Flags"/>): when present,
/// <see cref="BitOf"/> returns the spec's bit (the Z80's Z=6, N=1, …); when ABSENT (the 6502 — no
/// FlagLayout declared), it falls back to the <c>Flag</c> enum's numeric value (the 6502 bit
/// positions C=0 Z=1 I=2 D=3 V=6 N=7). Replaces the old hard-coded <c>CpuEmitter.FlagBit</c> switch
/// so the SAME emit arms are layout-driven, not 6502-hardcoded.</summary>
internal sealed class FlagBitMap
{
    private readonly Dictionary<string, int>? _declared;

    private FlagBitMap(Dictionary<string, int>? declared) => _declared = declared;

    /// <summary>Build from the spec's declared flag layout (null/empty ⇒ the enum-fallback map).</summary>
    public static FlagBitMap From(IReadOnlyList<FlagBitModel>? layout)
    {
        if (layout is null || layout.Count == 0)
            return new FlagBitMap(null);
        var dict = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var b in layout)
            dict[b.Name] = b.Bit;
        return new FlagBitMap(dict);
    }

    /// <summary>Resolve a flag NAME to its hardware bit position (0–7).</summary>
    public int BitOf(string flagName)
    {
        if (_declared is not null && _declared.TryGetValue(flagName, out int bit))
            return bit;
        // Enum fallback — the 6502 bit positions (the Flag enum's 6502 members' numeric values).
        return flagName switch
        {
            "C" => 0,
            "Z" => 1,
            "I" => 2,
            "D" => 3,
            "V" => 6,
            "N" => 7,
            _ => throw new System.ArgumentException(
                $"flag '{flagName}' has no bit position (no FlagLayout declares it and it is not a 6502 flag)"),
        };
    }
}
