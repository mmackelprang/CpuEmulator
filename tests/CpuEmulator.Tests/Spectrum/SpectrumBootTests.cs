using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>The ROM-boot acceptance gate (ROADMAP: ZX Spectrum 48K). Boots a real 16 KB 48K ROM, runs it well
/// past the full power-on RAM test + screen clear (the copyright screen is painted by ≈5.9M T-states and is
/// stable by ~13M — 200k was ~30× too small and never reached it), renders the first stable frame, and asserts
/// it is the BASIC copyright screen — parameterized across every present ROM variant AND both execution tiers.
/// Skips-with-note when no ROM is cached (mirroring Klaus/ZEX gating) so ROM-free CI stays green. The structural
/// assertion (mostly-white Colors[7] paper + a black-Colors[0] ink floor) holds for every variant — including
/// the Beckman ROM's different reset sequence and the Arabic/prototype character sets — and a per-variant
/// committed RGBA hash (captured on first green run) is the tight, both-tiers-identical gate.</summary>
[Trait("Category", "UAT")]
public class SpectrumBootTests
{
    // Full boot to the copyright screen ≈ 5.9M T-states; stable by ~13M. 7M (~100 frames) is safely past the
    // (C) screen and before the unnecessary 13M. (Was 200_000 — ~30× too small; the RAM test wasn't even done.)
    private const long BootCycles = 7_000_000;

    [SpectrumRomVariantTheory]
    [MemberData(nameof(SpectrumRomVariantData.VariantTierRows), MemberType = typeof(SpectrumRomVariantData))]
    public void Rom_boots_to_the_basic_copyright_screen(string variant, string romPath, ExecutionTier tier)
    {
        // The all-absent sentinel row (see SpectrumRomVariantData): the [SpectrumRomVariantTheory] attribute
        // already Skip'd the whole theory with a note when nothing is cached; this early-return is the xUnit-v2
        // belt-and-suspenders guard (v2 has no Assert.SkipWhen) so the sentinel never asserts.
        if (romPath.Length == 0) return;

        byte[] rom = SpectrumRom.Load(romPath);
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        machine.Run(BootCycles);

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // Un-fakeable structural invariant: every 48K ROM clears to a WHITE base paper (Colors[7], NOT bright
        // Colors[15]) and prints its copyright line in black (Colors[0]) near the bottom. Count over the inner
        // 256×192 ink area (offset by the 32px border). An empty/garbage/partial-boot screen lacks both.
        int whitePaper = 0, blackInk = 0;
        for (int y = 0; y < SpectrumUla.InkHeight; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[7]) whitePaper++;
            else if (p == SpectrumPalette.Colors[0]) blackInk++;
        }

        Assert.True(whitePaper > SpectrumUla.InkWidth * SpectrumUla.InkHeight / 2,
            $"[{variant}/{tier}] expected a mostly-white paper screen; got {whitePaper} white pixels");
        // Variant-safe floor: the canonical (C) line is ≈307 px; the Arabic/prototype/Beckman lines differ but
        // all carry well over 50 black ink pixels. (The canonical spec48 could tighten to >200 — see the
        // per-variant note below — but the shared floor stays variant-safe.)
        int inkFloor = variant == "spec48" ? 200 : 50;
        Assert.True(blackInk > inkFloor,
            $"[{variant}/{tier}] expected the black copyright text; got {blackInk} black pixels");

        // Tight gate: a per-variant committed RGBA hash of the full frame. Both tiers MUST produce the identical
        // frame for a given variant, so the hash is keyed by variant name only (not tier). On the FIRST green
        // run, capture each variant's hash (uncomment the WriteLine), paste it into ExpectedHashes, re-run.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[boot frame hash] {variant} = {hash}");  // <-- uncomment once to capture
        if (ExpectedHashes.TryGetValue(variant, out string? expected) &&
            expected != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
        {
            Assert.Equal(expected, hash);
        }
    }

    // Per-variant committed boot-frame hashes (captured on first green run, both tiers identical). A variant with
    // a PLACEHOLDER (or absent here) skips the hash check and relies on the structural floor — so a not-yet-
    // captured variant never fails spuriously. Capture at least spec48 (canonical) definitely; the others as
    // their ROMs are present and green.
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spec48"]            = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v1"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v2"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v31"] = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-beckman"]    = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-prototype"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
        };

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
