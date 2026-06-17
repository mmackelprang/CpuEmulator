using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// The spec-config half of the x86 importer arm (M5.4): architecture/namespace/specClassName + the
/// register file + the flag layout. Unlike <see cref="FieldGrammarConfig"/>, the x86 config carries
/// register PAIR-VIEWS (the AX/BX/CX/DX over the AH/AL… 8-bit halves — the same HighHalf/LowHalf machinery
/// the Z80 BC/DE/HL use), reusing <see cref="RegisterConfig"/>/<see cref="FlagBitConfig"/> from
/// <see cref="SemanticsMap"/> so the emitted Registers/FlagLayout shape matches the other arms byte-for-byte.
/// The 8086 has no per-mnemonic semantics map here — op bodies are M5.5 — so this is state-model-only.
/// </summary>
public sealed class X86Config
{
    public string Architecture  { get; init; } = "";
    public string Namespace     { get; init; } = "";
    public string SpecClassName { get; init; } = "";
    public RegisterConfig[] Registers { get; init; } = [];
    public FlagBitConfig[]  Flags     { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class Dto
    {
        public string Architecture  { get; set; } = "";
        public string Namespace     { get; set; } = "";
        public string SpecClassName { get; set; } = "";
        public RegisterDto[] Registers { get; set; } = [];
        public FlagDto[]     Flags     { get; set; } = [];
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class RegisterDto
    {
        public string  Name { get; set; } = "";
        public int     Bits { get; set; }
        public string  Role { get; set; } = "";
        public string? HighHalf { get; set; }
        public string? LowHalf  { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FlagDto
    {
        public string Name { get; set; } = "";
        public int    Bit  { get; set; }
    }

    public static X86Config Load(string path) => Parse(File.ReadAllText(path));

    public static X86Config Parse(string json)
    {
        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"x86 config JSON is malformed: {ex.Message}", ex);
        }
        if (dto is null) throw new InvalidDataException("x86 config is null.");
        if (string.IsNullOrWhiteSpace(dto.Architecture))
            throw new InvalidDataException("x86 config: 'architecture' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.Namespace))
            throw new InvalidDataException("x86 config: 'namespace' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.SpecClassName))
            throw new InvalidDataException("x86 config: 'specClassName' must be non-empty.");
        if (dto.Registers.Length == 0)
            throw new InvalidDataException("x86 config: 'registers' must be non-empty.");

        // Flag bit positions must be in-range [0,15] (the 16-bit FLAGS register) and unique — a duplicate or
        // out-of-range bit would silently emit a malformed FlagLayout the generator's analyzer then rejects.
        var seenBits = new HashSet<int>();
        foreach (var f in dto.Flags)
        {
            if (f.Bit is < 0 or > 15)
                throw new InvalidDataException(
                    $"x86 config: flag '{f.Name}' bit {f.Bit} out of range [0,15] (the 16-bit FLAGS register).");
            if (!seenBits.Add(f.Bit))
                throw new InvalidDataException($"x86 config: duplicate flag bit {f.Bit} (flag '{f.Name}').");
        }

        return new X86Config
        {
            Architecture  = dto.Architecture,
            Namespace     = dto.Namespace,
            SpecClassName = dto.SpecClassName,
            Registers     = [.. System.Linq.Enumerable.Select(dto.Registers,
                                r => new RegisterConfig(r.Name, r.Bits, r.Role, r.HighHalf, r.LowHalf))],
            Flags         = [.. System.Linq.Enumerable.Select(dto.Flags,
                                f => new FlagBitConfig(f.Name, f.Bit))],
        };
    }
}
