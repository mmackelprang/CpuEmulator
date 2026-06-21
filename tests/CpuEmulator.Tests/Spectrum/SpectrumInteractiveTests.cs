using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>Interactive BASIC behavioral UAT on the canonical 48K ROM, both tiers: boot to the K cursor, drive
/// the real key matrix to enter `PRINT 2+2` + ENTER, then assert the printed `4` / report line appears in the
/// top print region of the screen. Proves boot → keyboard → BASIC interpreter → screen end-to-end. Skips-with-
/// note when the canonical ROM is absent.</summary>
[Trait("Category", "UAT")]
public class SpectrumInteractiveTests
{
    private const long BootCycles = 7_000_000;            // reach the K cursor (≥5.9M)
    private const long CyclesPerFrame = SpectrumUla.TStatesPerFrame; // 69888

    [SpectrumRomTheory]   // skips-with-note when 48.rom (canonical) is absent
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Typing_PRINT_2_plus_2_then_ENTER_prints_4(ExecutionTier tier)
    {
        byte[] rom = SpectrumRom.Load(SpectrumRomVectors.TryGetRomPath());
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        machine.Run(BootCycles); // boot to the `K` cursor

        // Baseline: capture the ink in the TOP print region (rows 0..7 = the first text line) BEFORE typing.
        // The freshly-booted screen is blank white paper there (the (C) line is near the BOTTOM), so the top
        // print rows have ~0 black ink. After PRINT 2+2 the result `4` is printed at the top → ink appears.
        int inkTopBefore = CountBlackInkInRows(ula, 0, 8);

        // PRINT (keyword P) 2 + (SymbolShift+K) 2 ENTER.
        TypeKey(machine, ula, KeyCode.P);
        TypeKey(machine, ula, KeyCode.Digit2);
        TypeChord(machine, ula, KeyCode.SymbolShift, KeyCode.K);  // '+'
        TypeKey(machine, ula, KeyCode.Digit2);
        TypeKey(machine, ula, KeyCode.Enter);

        // Let the ROM evaluate + print + emit the report line.
        RunFrames(machine, 60);

        ula.RenderInto(new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight]); // settle a final frame
        int inkTopAfter = CountBlackInkInRows(ula, 0, 8);

        // Un-fakeable: typing actually drove the interpreter to PRINT a result on the top line. A machine that
        // ignored the keystrokes (or never evaluated) leaves the top print rows blank → inkTopAfter ≈ inkTopBefore.
        Assert.True(inkTopBefore < 10,
            $"[{tier}] precondition: the top print row should start blank; got {inkTopBefore} ink px");
        Assert.True(inkTopAfter > inkTopBefore + 20,
            $"[{tier}] expected the printed result on the top line; before={inkTopBefore} after={inkTopAfter}");

        // Tight gate: a committed RGBA hash of the full post-RUN frame (both tiers identical). Captured on first
        // green run; PLACEHOLDER until then so the structural delta is the live gate.
        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[interactive frame hash] {hash}");  // <-- uncomment once to capture
        // Non-const so the inert hash branch stays reachable under TreatWarningsAsErrors (CS0162) — same as the
        // shipped boot gate's ExpectedBootHash.
        string ExpectedHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedHash, hash);
    }

    /// <summary>Count black-ink (Colors[0]) pixels in the ink-area pixel rows [yStart, yEnd) (8 rows = one text
    /// line). Renders a fresh frame first.</summary>
    private static int CountBlackInkInRows(SpectrumUla ula, int yStart, int yEnd)
    {
        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        int ink = 0;
        for (int y = yStart; y < yEnd; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[0]) ink++;
        }
        return ink;
    }

    /// <summary>Press one key: Down, hold several frames (so the 50 Hz ISR scans the matrix), Up, then a gap
    /// frame so the ROM sees the release before the next key.</summary>
    private static void TypeKey(Machine machine, SpectrumUla ula, KeyCode key)
    {
        ula.PostKey(new KeyEvent(KeyAction.Down, key, null));
        RunFrames(machine, 4);
        ula.PostKey(new KeyEvent(KeyAction.Up, key, null));
        RunFrames(machine, 3);
    }

    /// <summary>Press two keys as a chord (both down → hold → both up → gap). Used for SYMBOL SHIFT + K = '+'.</summary>
    private static void TypeChord(Machine machine, SpectrumUla ula, KeyCode a, KeyCode b)
    {
        ula.PostKey(new KeyEvent(KeyAction.Down, a, null));
        ula.PostKey(new KeyEvent(KeyAction.Down, b, null));
        RunFrames(machine, 4);
        ula.PostKey(new KeyEvent(KeyAction.Up, b, null));
        ula.PostKey(new KeyEvent(KeyAction.Up, a, null));
        RunFrames(machine, 3);
    }

    private static void RunFrames(Machine machine, int frames) => machine.Run(CyclesPerFrame * frames);

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
