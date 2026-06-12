using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Configuration entry for a single register in the semantics map.
/// </summary>
public sealed record RegisterConfig(
    string Name,
    int    Bits,
    string Role = "");

/// <summary>
/// The fully loaded semantics map: config (architecture, namespace, class, registers)
/// plus the mnemonic → ops-text dictionary.
/// </summary>
public sealed class SemanticsMap
{
    public string Architecture  { get; init; } = "";
    public string Namespace     { get; init; } = "";
    public string SpecClassName { get; init; } = "";
    public RegisterConfig[]            Registers { get; init; } = [];
    public IReadOnlyDictionary<string, string> Mnemonics { get; init; } =
        new Dictionary<string, string>();

    // ─── vocabulary whitelist ────────────────────────────────────────────
    // Mirrors the DSL factory names in CpuEmulator.Core.Specification.Spec.
    // SYNC HAZARD: if the DSL grows new factories this list must grow too.
    // The same hazard exists for the AddrMode / Reg / Flag enum mirrors in
    // the generator (recorded in the 2b review, carried to 3b).
    private static readonly HashSet<string> AllowedFactories =
    [
        "Load", "Store", "Transfer", "Increment", "SetNZ", "Jump", "BranchIf"
    ];

    // ─── ops-text argument acceptance pattern ───────────────────────────
    // Accepts: Reg.<word>, Flag.<word>, true, false  (no full parser — the
    // generator is the real gate and runs in the e2e test).
    private static readonly Regex AllowedArgPattern =
        new(@"^(Reg\.\w+|Flag\.\w+|true|false)$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ─── serialization DTO ───────────────────────────────────────────────

    private sealed class SemanticsMapDto
    {
        public string Architecture  { get; set; } = "";
        public string Namespace     { get; set; } = "";
        public string SpecClassName { get; set; } = "";
        public RegisterConfigDto[] Registers { get; set; } = [];
        public Dictionary<string, string> Mnemonics { get; set; } = [];
    }

    private sealed class RegisterConfigDto
    {
        public string Name { get; set; } = "";
        public int    Bits { get; set; }
        public string Role { get; set; } = "";
    }

    // ─── public API ──────────────────────────────────────────────────────

    /// <summary>Loads the semantics map from a file path.</summary>
    public static SemanticsMap Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parses and validates the semantics map from a JSON string.
    /// Throws <see cref="InvalidDataException"/> on any violation.
    /// </summary>
    public static SemanticsMap Parse(string json)
    {
        SemanticsMapDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SemanticsMapDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Semantics map JSON is malformed: {ex.Message}", ex);
        }

        if (dto is null)
            throw new InvalidDataException("Semantics map is null.");

        // Validate each mnemonic's ops text
        foreach (var (mnemonic, opsText) in dto.Mnemonics)
            ValidateOpsText(mnemonic, opsText);

        return new SemanticsMap
        {
            Architecture  = dto.Architecture,
            Namespace     = dto.Namespace,
            SpecClassName = dto.SpecClassName,
            Registers     = dto.Registers.Select(r => new RegisterConfig(r.Name, r.Bits, r.Role)).ToArray(),
            Mnemonics     = dto.Mnemonics,
        };
    }

    // ─── validation ──────────────────────────────────────────────────────

    private static void ValidateOpsText(string mnemonic, string text)
    {
        var trimmed = text.Trim();

        // Must be a bracketed list
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            throw new InvalidDataException(
                $"Ops text for '{mnemonic}' must be a bracketed list (got: '{text}').");

        // Empty list is valid (e.g. NOP "[]")
        var inner = trimmed[1..^1].Trim();
        if (inner.Length == 0)
            return;

        // Parse calls: each call is FactoryName(args...)
        // We use a simple split-and-scan rather than a full parser.
        ParseCalls(mnemonic, inner);
    }

    private static void ParseCalls(string mnemonic, string inner)
    {
        // Scan call-by-call: factory name up to '(', args up to the FIRST ')'.
        // No nested-paren support — the argument whitelist (Reg.*/Flag.*/bool)
        // forbids parens anyway, so nesting can never appear in valid input.
        var remaining = inner;
        while (remaining.Length > 0)
        {
            // Find the factory call boundaries (first '(' and first ')')
            var openParen = remaining.IndexOf('(');
            if (openParen < 0)
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': expected factory call, got '{remaining}'.");

            var factoryName = remaining[..openParen].Trim();

            if (!AllowedFactories.Contains(factoryName))
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': unknown factory '{factoryName}'. " +
                    $"Allowed: {string.Join(", ", AllowedFactories)}.");

            var closeParen = remaining.IndexOf(')', openParen);
            if (closeParen < 0)
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': unclosed '(' after factory '{factoryName}'.");

            var argsText = remaining[(openParen + 1)..closeParen].Trim();
            ValidateArgs(mnemonic, factoryName, argsText);

            // Advance past this call (and any trailing comma + whitespace)
            remaining = remaining[(closeParen + 1)..].TrimStart();
            if (remaining.StartsWith(','))
                remaining = remaining[1..].TrimStart();
        }
    }

    private static void ValidateArgs(string mnemonic, string factory, string argsText)
    {
        if (argsText.Length == 0)
            return; // Jump() takes no args

        var args = argsText.Split(',');
        foreach (var rawArg in args)
        {
            var arg = rawArg.Trim();
            if (!AllowedArgPattern.IsMatch(arg))
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': invalid argument '{arg}' in call to '{factory}'. " +
                    "Arguments must be Reg.<name>, Flag.<name>, true, or false.");
        }
    }
}
