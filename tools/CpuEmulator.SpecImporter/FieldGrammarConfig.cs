using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// The spec-config half of the FieldGrammar importer arm: architecture/namespace/specClassName +
/// the register file + flag layout. The 68000 has NO per-mnemonic semantics map (op bodies are M4.5),
/// so this carries ONLY the state model the emitter writes verbatim. Reuses RegisterConfig/FlagBitConfig
/// from SemanticsMap so the emitted Registers/FlagLayout shape matches the opcode-row arm byte-for-byte.
/// </summary>
public sealed class FieldGrammarConfig
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
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FlagDto
    {
        public string Name { get; set; } = "";
        public int    Bit  { get; set; }
    }

    public static FieldGrammarConfig Load(string path) => Parse(File.ReadAllText(path));

    public static FieldGrammarConfig Parse(string json)
    {
        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"FieldGrammar config JSON is malformed: {ex.Message}", ex);
        }
        if (dto is null) throw new InvalidDataException("FieldGrammar config is null.");
        if (string.IsNullOrWhiteSpace(dto.Architecture))
            throw new InvalidDataException("FieldGrammar config: 'architecture' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.Namespace))
            throw new InvalidDataException("FieldGrammar config: 'namespace' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.SpecClassName))
            throw new InvalidDataException("FieldGrammar config: 'specClassName' must be non-empty.");
        if (dto.Registers.Length == 0)
            throw new InvalidDataException("FieldGrammar config: 'registers' must be non-empty.");

        return new FieldGrammarConfig
        {
            Architecture  = dto.Architecture,
            Namespace     = dto.Namespace,
            SpecClassName = dto.SpecClassName,
            Registers     = [.. System.Linq.Enumerable.Select(dto.Registers,
                                r => new RegisterConfig(r.Name, r.Bits, r.Role))],
            Flags         = [.. System.Linq.Enumerable.Select(dto.Flags,
                                f => new FlagBitConfig(f.Name, f.Bit))],
        };
    }
}
