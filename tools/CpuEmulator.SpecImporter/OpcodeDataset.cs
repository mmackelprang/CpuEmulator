using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// A single row from the vendored 6502 opcode dataset.
/// Field names match the JSON camelCase keys exactly.
/// </summary>
public sealed record OpcodeEntry(
    string Opcode,
    string Mnemonic,
    string Mode,
    int    Bytes,
    int    Cycles,
    bool   PageCrossPenalty);

/// <summary>
/// Loads and validates the vendored 6502 opcode dataset.
///
/// Valid mode vocabulary (13 modes — full 6502 documented set):
///   Implied, Accumulator, Immediate,
///   ZeroPage, ZeroPageX, ZeroPageY,
///   Absolute, AbsoluteX, AbsoluteY,
///   IndirectX, IndirectY, Indirect, Relative
///
/// Byte-count rules (derived from 6502 encoding):
///   Implied/Accumulator           → 1 byte
///   Immediate/ZeroPage/ZeroPageX/ZeroPageY/IndirectX/IndirectY/Relative → 2 bytes
///   Absolute/AbsoluteX/AbsoluteY/Indirect → 3 bytes
/// </summary>
public static class OpcodeDataset
{
    private static readonly HashSet<string> ValidModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads the dataset from a file path.</summary>
    public static OpcodeEntry[] Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parses and validates the dataset from a JSON string.
    /// Throws <see cref="InvalidDataException"/> on any violation.
    /// </summary>
    public static OpcodeEntry[] Parse(string json)
    {
        OpcodeEntry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<OpcodeEntry[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Opcode dataset JSON is malformed: {ex.Message}", ex);
        }

        if (entries is null || entries.Length == 0)
            throw new InvalidDataException("Opcode dataset is empty.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var ctx = $"row {i} ({e.Opcode} {e.Mnemonic} {e.Mode})";

            // Unique opcode
            if (!seen.Add(e.Opcode))
                throw new InvalidDataException($"Duplicate opcode {e.Opcode} at {ctx}.");

            // Mode vocabulary
            if (!ValidModes.Contains(e.Mode))
                throw new InvalidDataException($"Unknown mode '{e.Mode}' at {ctx}.");

            // Byte-count consistency
            var expectedBytes = ExpectedBytes(e.Mode);
            if (e.Bytes != expectedBytes)
                throw new InvalidDataException(
                    $"Byte count mismatch at {ctx}: mode {e.Mode} requires {expectedBytes} bytes, got {e.Bytes}.");
        }

        return entries;
    }

    private static int ExpectedBytes(string mode) => mode switch
    {
        "Implied" or "Accumulator" => 1,
        "Immediate" or "ZeroPage" or "ZeroPageX" or "ZeroPageY"
            or "IndirectX" or "IndirectY" or "Relative" => 2,
        "Absolute" or "AbsoluteX" or "AbsoluteY" or "Indirect" => 3,
        _ => throw new InvalidOperationException($"Unhandled mode: {mode}") // unreachable after vocabulary check
    };
}
