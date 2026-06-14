using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Configuration entry for a single register in the semantics map.
/// </summary>
public sealed record RegisterConfig(
    string  Name,
    int     Bits,
    string  Role = "",
    string? HighHalf = null,   // M3.4a: for a 16-bit pair VIEW, the 8-bit high-half register name
    string? LowHalf  = null);  // M3.4a: the 8-bit low-half register name

/// <summary>M3.4a: one flag name → hardware bit position in the status-flag layout.</summary>
public sealed record FlagBitConfig(string Name, int Bit);

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
    /// <summary>M3.4a: the optional status-flag bit layout (the Z80's S=7..C=0). Empty ⇒ the
    /// FlagBit enum-fallback (the 6502 — no FlagLayout emitted).</summary>
    public FlagBitConfig[]             Flags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Mnemonics { get; init; } =
        new Dictionary<string, string>();

    // ─── vocabulary whitelist + arity table ──────────────────────────────
    // Mirrors the DSL factory names AND parameter counts in
    // CpuEmulator.Core.Specification.Spec.
    // SYNC HAZARD: if the DSL grows new factories (or changes signatures)
    // this table must change too.
    //
    // M3.1a: a register argument is now a register-NAME string literal ("A"), not a Reg enum
    // member. The arity table is UNCHANGED (register-ness is not encoded in arity — Load is still
    // arity 1); only the argument SPELLING changed, mirrored in AllowedArgPattern below. There is
    // no s_regMembers whitelist to mirror — the generator cross-checks names against the spec's
    // OWN Registers table (CPUGEN008), the real gate (it runs in the e2e test).
    //
    // See also the MIRROR TABLES block in SpecParser.cs (the generator's truth):
    //   s_microOpSignatures, s_addrModes, s_flagMembers, op-kind sets.
    // SpecFileEmitter.SupportedModes mirrors AddrMode enum members.
    private static readonly Dictionary<string, int> FactoryArity = new()
    {
        ["Load"]          = 1,  // Load("reg")
        ["Store"]         = 1,  // Store("reg")
        ["Transfer"]      = 2,  // Transfer("reg", "reg")
        ["Increment"]     = 1,  // Increment("reg")
        ["SetNZ"]         = 1,  // SetNZ("reg")
        ["Jump"]          = 0,  // Jump()
        ["BranchIf"]      = 2,  // BranchIf(Flag, bool)
        // ALU ops (Task 5)
        ["Adc"]           = 0,  // Adc()
        ["Sbc"]           = 0,  // Sbc()
        ["And"]           = 0,  // And()
        ["Ora"]           = 0,  // Ora()
        ["Eor"]           = 0,  // Eor()
        ["Compare"]       = 1,  // Compare("reg")
        ["Bit"]           = 0,  // Bit()
        // RMW ops (Task 6)
        ["ShiftLeft"]     = 0,  // ShiftLeft()
        ["ShiftRight"]    = 0,  // ShiftRight()
        ["RotateLeft"]    = 0,  // RotateLeft()
        ["RotateRight"]   = 0,  // RotateRight()
        ["IncrementMem"]  = 0,  // IncrementMem()
        ["DecrementMem"]  = 0,  // DecrementMem()
        ["Decrement"]     = 1,  // Decrement("reg")
        // Stack ops (Task 7)
        ["Push"]          = 1,  // Push("reg")
        ["Pull"]          = 1,  // Pull("reg")
        ["PushP"]         = 0,  // PushP()
        ["PullP"]         = 0,  // PullP()
        // Flag ops (Task 7)
        ["SetFlag"]       = 2,  // SetFlag(Flag, bool)
        // Flow ops (Task 7)
        ["Jsr"]           = 0,  // Jsr()
        ["Rts"]           = 0,  // Rts()
        // BRK/RTI flow ops (Task 8 / 3b-ii)
        ["Brk"]           = 0,  // Brk()
        ["Rti"]           = 0,  // Rti()
        // I/O-port + halt class (M3.2 — additive). Mirrors Spec.PortIn/PortOut/Halt + the parser's
        // s_microOpSignatures. These already SHIPPED in M3.2 (Spec.cs:68-70); this mirror table was
        // not updated in concert (the SYNC HAZARD noted above). M3.3 syncs it so a Z80 covered
        // mnemonic (HALT -> Halt, IN/OUT -> PortIn/PortOut, Ground truth E.2) validates against the
        // EXISTING factory set. NO M3.4 vocabulary is added here — these are pre-existing factories.
        ["PortIn"]        = 1,  // PortIn("reg")
        ["PortOut"]       = 1,  // PortOut("reg")
        ["Halt"]          = 0,  // Halt()
        // Composable flag micro-ops (M3.4a — general, 8086-reusable).
        ["SetSZ"]         = 1,  // SetSZ("reg")
        ["SetParity"]     = 1,  // SetParity("reg")
        ["SetXY"]         = 1,  // SetXY("reg")
        ["SetAddSub"]     = 1,  // SetAddSub(true|false)
        // M3.4b: rotate-accumulators + CB plane.
        ["Rlca"]          = 0,
        ["Rrca"]          = 0,
        ["Rla"]           = 0,
        ["Rra"]           = 0,
        ["CbRotate"]      = 2,  // CbRotate("RLC", "B")
        ["CbBit"]         = 3,  // CbBit("BIT", 7, "(HL)")
        // M3.4c: the ED-core ops.
        ["EdIn"]        = 1,
        ["EdOut"]       = 1,
        ["EdAdcSbc16"]  = 2,
        ["EdLdNnRp"]    = 2,
        ["EdNeg"]       = 0,
        ["EdRetn"]      = 1,
        ["EdIm"]        = 1,
        ["EdLdIaRa"]    = 1,
        ["EdRrdRld"]    = 1,
        ["EdNop"]       = 0,
        // M3.4d: the ED block ops.
        ["EdBlock"]     = 1,
    };

    // ─── ops-text argument acceptance pattern ───────────────────────────
    // Accepts: "<regname>" (a quoted register-name string), Flag.<word>, true, false
    // (no full parser — the generator is the real gate and runs in the e2e test).
    // M3.1a: the register-arg form moved from Reg.<word> to a double-quoted identifier; a bare
    // unquoted register token (e.g. A) is now rejected here, mirroring the parser's string-literal
    // requirement (CPUGEN011).
    // M3.4b: also accept a bare integer (the CB bit index 0..7) and a quoted "(HL)" target (the CB
    // EA shorthand — the only non-\w quoted arg). The generator is the real gate (runs in the e2e test).
    private static readonly Regex AllowedArgPattern =
        new(@"^(""\w+""|""\(HL\)""|Flag\.\w+|true|false|\d+)$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ─── serialization DTOs ──────────────────────────────────────────────
    // Disallow unknown members: this is a curated, hand-edited file — a
    // typo'd key (e.g. "mnemonic" for "mnemonics") must fail loudly rather
    // than be silently skipped leaving the real field at its default.

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class SemanticsMapDto
    {
        public string Architecture  { get; set; } = "";
        public string Namespace     { get; set; } = "";
        public string SpecClassName { get; set; } = "";
        public RegisterConfigDto[] Registers { get; set; } = [];
        public FlagBitConfigDto[]  Flags { get; set; } = [];   // M3.4a (optional)
        public Dictionary<string, string> Mnemonics { get; set; } = [];
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FlagBitConfigDto
    {
        public string Name { get; set; } = "";
        public int    Bit  { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class RegisterConfigDto
    {
        public string  Name { get; set; } = "";
        public int     Bits { get; set; }
        public string  Role { get; set; } = "";
        public string? HighHalf { get; set; }   // M3.4a: pair-view high half (optional)
        public string? LowHalf  { get; set; }   // M3.4a: pair-view low half (optional)
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

        // Non-empty config — an empty/missing field would make Task 4 emit
        // garbage (e.g. "namespace ;") far from the cause. Fail at load.
        if (string.IsNullOrWhiteSpace(dto.Architecture))
            throw new InvalidDataException("Semantics map: 'architecture' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.Namespace))
            throw new InvalidDataException("Semantics map: 'namespace' must be non-empty.");
        if (string.IsNullOrWhiteSpace(dto.SpecClassName))
            throw new InvalidDataException("Semantics map: 'specClassName' must be non-empty.");
        if (dto.Mnemonics.Count == 0)
            throw new InvalidDataException("Semantics map: 'mnemonics' must be non-empty.");

        // Validate each mnemonic's ops text
        foreach (var (mnemonic, opsText) in dto.Mnemonics)
            ValidateOpsText(mnemonic, opsText);

        return new SemanticsMap
        {
            Architecture  = dto.Architecture,
            Namespace     = dto.Namespace,
            SpecClassName = dto.SpecClassName,
            Registers     = dto.Registers.Select(r => new RegisterConfig(r.Name, r.Bits, r.Role, r.HighHalf, r.LowHalf)).ToArray(),
            Flags         = dto.Flags.Select(b => new FlagBitConfig(b.Name, b.Bit)).ToArray(),
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
        // No nested-paren support — the argument whitelist ("regname"/Flag.*/bool)
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

            if (!FactoryArity.TryGetValue(factoryName, out var arity))
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': unknown factory '{factoryName}'. " +
                    $"Allowed: {string.Join(", ", FactoryArity.Keys)}.");

            var closeParen = remaining.IndexOf(')', openParen);
            if (closeParen < 0)
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': unclosed '(' after factory '{factoryName}'.");

            var argsText = remaining[(openParen + 1)..closeParen].Trim();
            ValidateArgs(mnemonic, factoryName, arity, argsText);

            // Advance past this call. Anything that follows must be a ','
            // separator — a missing comma would otherwise emit as invalid C#.
            remaining = remaining[(closeParen + 1)..].TrimStart();
            if (remaining.Length > 0)
            {
                if (!remaining.StartsWith(','))
                    throw new InvalidDataException(
                        $"Ops text for '{mnemonic}': expected ',' between calls, got '{remaining}'.");
                remaining = remaining[1..].TrimStart();
            }
        }
    }

    private static void ValidateArgs(string mnemonic, string factory, int arity, string argsText)
    {
        string[] args = argsText.Length == 0
            ? []
            : argsText.Split(',');

        if (args.Length != arity)
            throw new InvalidDataException(
                $"Ops text for '{mnemonic}': '{factory}' expects {arity} argument(s), got {args.Length}.");

        foreach (var rawArg in args)
        {
            var arg = rawArg.Trim();
            if (!AllowedArgPattern.IsMatch(arg))
                throw new InvalidDataException(
                    $"Ops text for '{mnemonic}': invalid argument '{arg}' in call to '{factory}'. " +
                    "Arguments must be \"<regname>\", Flag.<name>, true, or false.");
        }
    }
}
