using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// One instruction-FAMILY row of the 68000 FieldGrammar dataset — the importer's analog of OpcodeEntry,
/// but field-encoded: a (mask, match) selects the family; size/EA fields fan it out across sizes × EA-modes
/// × registers at decode time (the M4.3a ExtensionWordCount / MapSize do the fan-out). Field names + types
/// match the FieldOp record (DecodeStructure.cs) 1:1 — the emitter writes FieldOp(Mask: .., Match: .., …).
/// </summary>
public sealed record FieldGrammarFamily(
    ushort Mask, ushort Match, string Operation,
    int SizeShift, int SizeWidth, string SizeEncoding,
    int EaShift, string LegalEa, string? Source = null);

public static class FieldGrammarDataset
{
    private static readonly HashSet<string> KnownSizeEncodings = new(StringComparer.Ordinal)
        { "Standard", "Move" };

    // Mirrors the EaCategory enum (DecodeStructure.cs). SYNC HAZARD: if EaCategory gains members, add here.
    private static readonly HashSet<string> KnownEaCategories = new(StringComparer.Ordinal)
        { "DataAddressing", "MemoryAlterable", "DataAlterable", "Control", "Alterable", "All" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class Dto
    {
        public string? Operation { get; set; }
        public string? Mask { get; set; }
        public string? Match { get; set; }
        public int?    SizeShift { get; set; }
        public int?    SizeWidth { get; set; }
        public string? SizeEncoding { get; set; }
        public int?    EaShift { get; set; }
        public string? LegalEa { get; set; }
        public string? Source { get; set; }
    }

    public static FieldGrammarFamily[] Load(string path) => Parse(File.ReadAllText(path));

    public static FieldGrammarFamily[] Parse(string json)
    {
        Dto[]? dtos;
        try { dtos = JsonSerializer.Deserialize<Dto[]>(json, JsonOptions); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"FieldGrammar dataset JSON is malformed: {ex.Message}", ex);
        }
        if (dtos is null || dtos.Length == 0)
            throw new InvalidDataException("FieldGrammar dataset is empty.");

        var families = new FieldGrammarFamily[dtos.Length];
        var seenMatch = new HashSet<(ushort, ushort)>();
        for (int i = 0; i < dtos.Length; i++)
        {
            var d = dtos[i];
            var ctx = $"family {i} ({d.Operation ?? "?"})";
            if (string.IsNullOrWhiteSpace(d.Operation))
                throw new InvalidDataException($"Missing 'operation' at {ctx}.");
            if (d.Mask is null) throw new InvalidDataException($"Missing 'mask' at {ctx}.");
            if (d.Match is null) throw new InvalidDataException($"Missing 'match' at {ctx}.");
            if (d.SizeShift is null) throw new InvalidDataException($"Missing 'sizeShift' at {ctx}.");
            if (d.SizeWidth is null) throw new InvalidDataException($"Missing 'sizeWidth' at {ctx}.");
            if (d.SizeEncoding is null) throw new InvalidDataException($"Missing 'sizeEncoding' at {ctx}.");
            if (d.EaShift is null) throw new InvalidDataException($"Missing 'eaShift' at {ctx}.");
            if (d.LegalEa is null) throw new InvalidDataException($"Missing 'legalEa' at {ctx}.");

            ushort mask = ParseHex16(d.Mask, $"mask at {ctx}");
            ushort match = ParseHex16(d.Match, $"match at {ctx}");
            if ((match & ~mask) != 0)
                throw new InvalidDataException(
                    $"'match' {d.Match} has bits outside 'mask' {d.Mask} at {ctx} (unreachable family).");
            // The generator's analyzer (SpecParser CPUGEN015) requires sizeWidth >= 1 — a zero-width
            // size field is not analyzable. Reject it here too so the importer fails loudly at load time
            // rather than emitting a spec the generator silently rejects. No-size families use an inert
            // 1-bit field (sizeShift: 0, sizeWidth: 1), not a zero-width one.
            if (d.SizeWidth.Value < 1 || d.SizeShift.Value < 0 ||
                d.SizeShift.Value + d.SizeWidth.Value > 16)
                throw new InvalidDataException(
                    $"size field [shift {d.SizeShift}, width {d.SizeWidth}] out of bounds at {ctx} " +
                    "(width must be >= 1 and shift + width <= 16).");
            if (d.EaShift.Value < 0 || d.EaShift.Value > 10)
                throw new InvalidDataException($"eaShift {d.EaShift} out of bounds (0..10) at {ctx}.");
            if (!KnownSizeEncodings.Contains(d.SizeEncoding))
                throw new InvalidDataException(
                    $"Unknown sizeEncoding '{d.SizeEncoding}' at {ctx}: expected Standard or Move.");
            if (!KnownEaCategories.Contains(d.LegalEa))
                throw new InvalidDataException(
                    $"Unknown legalEa '{d.LegalEa}' at {ctx}: expected one of " +
                    $"{string.Join(", ", KnownEaCategories)}.");
            // No two families may share an identical (mask, match) — the decode walk takes the FIRST hit,
            // so a duplicate is dead (and almost certainly an authoring error).
            if (!seenMatch.Add((mask, match)))
                throw new InvalidDataException(
                    $"Duplicate (mask {d.Mask}, match {d.Match}) at {ctx}: a second family can never fire.");

            families[i] = new FieldGrammarFamily(
                mask, match, d.Operation,
                d.SizeShift.Value, d.SizeWidth.Value, d.SizeEncoding,
                d.EaShift.Value, d.LegalEa, d.Source);
        }
        return families;
    }

    private static ushort ParseHex16(string text, string what)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var v))
            return v;
        throw new InvalidDataException($"{what}: expected '0xNNNN' (16-bit hex), got '{text}'.");
    }
}
