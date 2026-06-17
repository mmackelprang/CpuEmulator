using System.Text.Json;

namespace CpuEmulator.Benchmarks;

/// <summary>A single CITED published-context row from the hand-curated registry
/// (bench/results/reference-numbers.json). It is NOT measured by this suite — it reserves the
/// "best existing" column in the comparison table with a sourced placeholder until a head-to-head
/// number lands. <see cref="GuestMips"/>/<see cref="CyclesPerSecond"/> may be null when we ship no
/// fabricated number; <see cref="Source"/> is REQUIRED (provenance is enforced in code) so a cited
/// figure always carries its citation.</summary>
/// <param name="Cpu">The architecture key this row attaches to ("mos6502" / "z80" / "m68000").</param>
/// <param name="Subject">The cited subject name (e.g. "Musashi (C)").</param>
/// <param name="GuestMips">Cited guest-MIPS, or null when no comparable published figure exists.</param>
/// <param name="CyclesPerSecond">Cited cycles/sec in the CPU's own unit, or null.</param>
/// <param name="Note">Human context — what the figure is + why it's cited, not measured.</param>
/// <param name="Source">The citation URL — REQUIRED + non-empty (provenance enforced in code).</param>
/// <param name="MeasuredOn">Where/how the cited figure was obtained (or "n/a — …" for a project ref).</param>
/// <param name="CitedDate">When this row was added to the registry.</param>
public record ReferenceNumber(
    string Cpu,
    string Subject,
    double? GuestMips,
    double? CyclesPerSecond,
    string Note,
    string Source,
    string MeasuredOn,
    string CitedDate);

/// <summary>Loads + validates the committed published-numbers registry
/// (bench/results/reference-numbers.json). The registry's value is twofold: (a) it proves the
/// cited-row mechanism + provenance enforcement end-to-end, and (b) it reserves the 68000
/// "best existing" column as a <c>[cited]</c> placeholder until the head-to-head Musashi number
/// lands (plan Task M4). An ABSENT file is a skip-with-note (returns empty — never throws); a
/// PRESENT-but-invalid file throws <see cref="InvalidDataException"/> naming the offending subject.</summary>
public static class ReferenceNumbers
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The nullable-tolerant DTO the JSON deserializes into; mapped to the public
    /// <see cref="ReferenceNumber"/> after validation. Every numeric field is nullable (we ship no
    /// fabricated number); the string fields are nullable so a missing one is caught + reported
    /// rather than silently defaulted.</summary>
    private sealed class Dto
    {
        public string? Cpu { get; set; }
        public string? Subject { get; set; }
        public double? GuestMips { get; set; }
        public double? CyclesPerSecond { get; set; }
        public string? Note { get; set; }
        public string? Source { get; set; }
        public string? MeasuredOn { get; set; }
        public string? CitedDate { get; set; }
    }

    /// <summary>The pure parse + validate (the seam tests drive). Deserializes the JSON array,
    /// validates every row (a row missing a non-empty <c>source</c>, <c>cpu</c>, or <c>subject</c>
    /// is REJECTED), and maps to the public record. Throws <see cref="InvalidDataException"/> with a
    /// clear message naming the offending subject on any violation.</summary>
    public static IReadOnlyList<ReferenceNumber> Parse(string json)
    {
        List<Dto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<Dto>>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"reference-numbers registry is not valid JSON: {ex.Message}", ex);
        }

        if (dtos is null) return [];

        var result = new List<ReferenceNumber>(dtos.Count);
        foreach (var d in dtos)
        {
            // Name the offending row as helpfully as we can — prefer the subject, fall back to the
            // cpu, then to the row index so the message is always actionable.
            string who = !string.IsNullOrWhiteSpace(d.Subject) ? d.Subject!
                       : !string.IsNullOrWhiteSpace(d.Cpu) ? $"cpu={d.Cpu}"
                       : $"row #{result.Count + 1}";

            if (string.IsNullOrWhiteSpace(d.Cpu))
                throw new InvalidDataException($"reference-numbers row '{who}' is missing a non-empty 'cpu'.");
            if (string.IsNullOrWhiteSpace(d.Subject))
                throw new InvalidDataException($"reference-numbers row '{who}' is missing a non-empty 'subject'.");
            if (string.IsNullOrWhiteSpace(d.Source))
                throw new InvalidDataException(
                    $"reference-numbers row '{d.Subject}' is missing a non-empty 'source' — a cited figure MUST carry its citation (provenance is enforced).");

            result.Add(new ReferenceNumber(
                Cpu: d.Cpu!,
                Subject: d.Subject!,
                GuestMips: d.GuestMips,
                CyclesPerSecond: d.CyclesPerSecond,
                Note: d.Note ?? string.Empty,
                Source: d.Source!,
                MeasuredOn: d.MeasuredOn ?? string.Empty,
                CitedDate: d.CitedDate ?? string.Empty));
        }
        return result;
    }

    /// <summary>Locate + load the committed registry. <paramref name="path"/> overrides the
    /// location; otherwise the default walks up from the working dir / assembly base for a
    /// <c>bench/results/reference-numbers.json</c> (mirroring <c>ReportWriter.LocateBenchDir</c>).
    /// An ABSENT file returns an empty list (skip-with-note discipline — the comparison table simply
    /// shows no cited rows); a PRESENT-but-invalid file throws via <see cref="Parse"/>.</summary>
    public static IReadOnlyList<ReferenceNumber> Load(string? path = null)
    {
        string resolved = path ?? LocateRegistry();
        if (!File.Exists(resolved)) return [];
        return Parse(File.ReadAllText(resolved));
    }

    /// <summary>Walk up from the working dir + assembly base for a
    /// <c>bench/results/reference-numbers.json</c> (mirrors <c>ReportWriter.LocateBenchDir</c>); falls
    /// back to a path beside the cwd (which File.Exists then reports absent → empty list).</summary>
    private static string LocateRegistry()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "bench", "results", "reference-numbers.json");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "bench", "results", "reference-numbers.json");
    }
}
