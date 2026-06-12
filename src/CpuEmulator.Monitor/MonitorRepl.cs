using CpuEmulator.Core;

namespace CpuEmulator.Monitor;

/// <summary>
/// Line-oriented command parser over MonitorEngine. Console-free: driven by TextReader/TextWriter.
/// The host (§9 item 7) wires this over stdio; tests use StringReader/StringWriter.
/// Assembly cursor: set by 'a $ADDR INSTR', advanced per instruction; required before
/// cursor-form 'a INSTR'.
/// </summary>
public sealed class MonitorRepl
{
    private readonly MonitorEngine _engine;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _prompt;

    private uint _assembleCursor;
    private bool _cursorValid;

    /// <summary>
    /// Construct a REPL over the given engine and I/O pair.
    /// </summary>
    /// <param name="engine">The monitor engine to dispatch commands to.</param>
    /// <param name="input">Command source (StringReader for tests, Console.In for host).</param>
    /// <param name="output">Output sink (StringWriter for tests, Console.Out for host).</param>
    /// <param name="prompt">When true, print "* " before each line (disabled in tests).</param>
    public MonitorRepl(MonitorEngine engine, TextReader input, TextWriter output, bool prompt = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _engine = engine;
        _input = input;
        _output = output;
        _prompt = prompt;
    }

    /// <summary>Run the REPL until 'q' or EOF.</summary>
    public void Run()
    {
        while (true)
        {
            if (_prompt) _output.Write("* ");
            string? line = _input.ReadLine();
            if (line is null) break; // EOF
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue; // blank line ignored
            if (!Dispatch(trimmed)) break; // 'q' returns false
        }
    }

    /// <summary>Dispatch one command line. Returns false when the REPL should quit.</summary>
    private bool Dispatch(string line)
    {
        // Split into command token and the rest of the line (args)
        int sep = line.IndexOf(' ');
        string cmd = sep < 0 ? line : line.Substring(0, sep);
        string args = sep < 0 ? string.Empty : line.Substring(sep + 1);

        switch (cmd.ToLowerInvariant())
        {
            case "m": HandleMemory(args); break;
            case "d": HandleDisassemble(args); break;
            case "a": HandleAssemble(args); break;
            case "r": HandleRegisters(args); break;
            case "s": HandleStep(args); break;
            case "g": HandleGo(args); break;
            case "l": HandleLoad(args); break;
            case "w": HandleSave(args); break;
            case "?": HandleHelp(); break;
            case "q": return false;
            default:
                _output.WriteLine($"? unknown command '{cmd}' — type ? for help");
                break;
        }
        return true;
    }

    // ── m: memory dump / write ────────────────────────────────────────────────

    private void HandleMemory(string args)
    {
        int colon = args.IndexOf(':');
        if (colon >= 0) // write form: m ADDR: BB BB ...
        {
            if (!TryParseAddress(args.Substring(0, colon), out uint addr))
            {
                _output.WriteLine($"? bad address '{args.Substring(0, colon).Trim()}'");
                return;
            }
            string[] tokens = args.Substring(colon + 1)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var bytes = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!TryParseHexByte(tokens[i], out bytes[i]))
                {
                    _output.WriteLine($"? bad byte '{tokens[i]}'");
                    return;
                }
            }
            if (bytes.Length == 0)
            {
                _output.WriteLine("? no bytes to write");
                return;
            }
            _engine.WriteMemory(addr, bytes);
            _output.WriteLine(_engine.ReadMemory(addr, bytes.Length)); // echo what landed
            return;
        }

        // dump form: m ADDR [COUNT]
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = 0x40;
        if (parts.Length is < 1 or > 2
            || !TryParseAddress(parts[0], out uint start)
            || (parts.Length == 2 && !TryParseCount(parts[1], out count)))
        {
            _output.WriteLine("? usage: m ADDR [COUNT]  or  m ADDR: BB BB ...");
            return;
        }
        _output.WriteLine(_engine.ReadMemory(start, count));
    }

    // ── d: disassemble ────────────────────────────────────────────────────────

    private void HandleDisassemble(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = 8;
        if (parts.Length is < 1 or > 2
            || !TryParseAddress(parts[0], out uint addr)
            || (parts.Length == 2 && !TryParseCount(parts[1], out count)))
        {
            _output.WriteLine("? usage: d ADDR [COUNT]");
            return;
        }
        _output.WriteLine(_engine.Disassemble(addr, count));
    }

    // ── a: assemble ───────────────────────────────────────────────────────────

    private void HandleAssemble(string args)
    {
        string text = args.Trim();
        uint addr;
        if (text.StartsWith("$", StringComparison.Ordinal))
        {
            // Address form: a $ADDR INSTRUCTION
            int space = text.IndexOf(' ');
            if (space < 0 || !TryParseAddress(text.Substring(0, space), out addr))
            {
                _output.WriteLine("? usage: a $ADDR INSTRUCTION ($ required — mnemonics are valid hex)");
                return;
            }
            text = text.Substring(space + 1).Trim();
        }
        else if (_cursorValid)
        {
            addr = _assembleCursor;
        }
        else
        {
            _output.WriteLine("? no assembly address — start with a $ADDR INSTRUCTION");
            return;
        }

        if (!_engine.TryAssembleAt(addr, text, out byte[] bytes, out string? error))
        {
            _output.WriteLine($"? {error}");
            return;
        }
        _output.WriteLine(_engine.Disassemble(addr, 1)); // echo the encoded instruction
        _assembleCursor = addr + (uint)bytes.Length;
        _cursorValid = true;
    }

    // ── r: registers ──────────────────────────────────────────────────────────

    private void HandleRegisters(string args)
    {
        string trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            _output.WriteLine(_engine.Registers());
            return;
        }

        // r NAME=VALUE
        int eq = trimmed.IndexOf('=');
        if (eq <= 0)
        {
            _output.WriteLine($"? usage: r  or  r NAME=VALUE");
            return;
        }
        string name = trimmed.Substring(0, eq).Trim().ToUpperInvariant();
        string valueStr = trimmed.Substring(eq + 1).Trim();

        // Value may have optional '$' prefix
        if (valueStr.StartsWith("$", StringComparison.Ordinal))
            valueStr = valueStr.Substring(1);

        if (!ulong.TryParse(valueStr, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong value))
        {
            _output.WriteLine($"? bad value '{trimmed.Substring(eq + 1).Trim()}'");
            return;
        }

        try
        {
            _engine.SetRegister(name, value);
        }
        catch (ArgumentException)
        {
            _output.WriteLine($"? unknown register '{name}'");
            return;
        }
        _output.WriteLine(_engine.Registers());
    }

    // ── s: step ───────────────────────────────────────────────────────────────

    private void HandleStep(string args)
    {
        string trimmed = args.Trim();
        int n = 1;
        if (trimmed.Length > 0 && !TryParseCount(trimmed, out n))
        {
            _output.WriteLine($"? bad step count '{trimmed}'");
            return;
        }
        for (int i = 0; i < n; i++)
        {
            MonitorStepReport report = _engine.Step();
            // Two-line step report: "{pc:X4}: {disassembly}" then registers line
            _output.WriteLine($"{report.PcBefore:X4}: {report.Disassembly}");
            _output.WriteLine(report.Registers);
        }
    }

    // ── g: go / run ───────────────────────────────────────────────────────────

    /// <summary>
    /// g [$ADDR] [until $TARGET] [BUDGET]
    /// Optional leading $ADDR sets PC; optional "until $TARGET" pair; optional decimal BUDGET
    /// (default 1,000,000). With target → RunUntil; without → Run then budget-exhausted stop line.
    /// </summary>
    private void HandleGo(string args)
    {
        string[] tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int idx = 0;
        bool hasTarget = false;
        uint targetPc = 0;
        long budget = 1_000_000;

        // Optional leading $ADDR
        if (idx < tokens.Length && tokens[idx].StartsWith("$", StringComparison.Ordinal))
        {
            if (!TryParseAddress(tokens[idx], out uint startPc))
            {
                _output.WriteLine($"? bad address '{tokens[idx]}'");
                return;
            }
            _engine.SetRegister("PC", startPc);
            idx++;
        }

        // Optional "until $TARGET"
        if (idx < tokens.Length
            && tokens[idx].Equals("until", StringComparison.OrdinalIgnoreCase))
        {
            idx++;
            if (idx >= tokens.Length || !TryParseAddress(tokens[idx], out targetPc))
            {
                _output.WriteLine("? usage: g [$ADDR] [until $TARGET] [BUDGET]");
                return;
            }
            hasTarget = true;
            idx++;
        }

        // Optional decimal BUDGET
        if (idx < tokens.Length)
        {
            if (!long.TryParse(tokens[idx], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out budget)
                || budget <= 0)
            {
                _output.WriteLine($"? bad budget '{tokens[idx]}' (expected decimal cycle count)");
                return;
            }
            idx++;
        }

        if (idx < tokens.Length)
        {
            _output.WriteLine("? usage: g [$ADDR] [until $TARGET] [BUDGET]");
            return;
        }

        if (hasTarget)
        {
            RunReport report = _engine.RunUntil(targetPc, budget);
            string stopLine = report.Reason switch
            {
                RunStopReason.TargetReached =>
                    $"target ${report.Pc:X4} reached after {report.CyclesRun} cycles",
                RunStopReason.Trapped =>
                    $"trapped at ${report.Pc:X4} after {report.CyclesRun} cycles",
                RunStopReason.BudgetExhausted =>
                    $"budget exhausted at ${report.Pc:X4} after {report.CyclesRun} cycles",
                _ => $"stopped at ${report.Pc:X4} after {report.CyclesRun} cycles",
            };
            _output.WriteLine(stopLine);
        }
        else
        {
            // Plain Run — no target
            long before = _engine.Run(budget); // returns cycles consumed
            // We need the current PC for the stop line — read from the engine
            // (MonitorEngine exposes Registers() which includes PC, but we need the raw uint)
            // Parse it out from the registers string — or just report via the registers line.
            // The plan says: "budget exhausted at $PC after N cycles"
            // We access the PC via Registers string. Let's format inline.
            string regsLine = _engine.Registers();
            // Extract PC value from "... PC=XXXX ..."
            uint currentPc = ExtractPcFromRegsLine(regsLine);
            _output.WriteLine($"budget exhausted at ${currentPc:X4} after {before} cycles");
        }
    }

    /// <summary>Parse the PC value out of a registers line like "A=00 ... PC=0202 CYC=42".</summary>
    private static uint ExtractPcFromRegsLine(string regsLine)
    {
        int idx = regsLine.IndexOf("PC=", StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + 3;
        int end = start;
        while (end < regsLine.Length && regsLine[end] != ' ') end++;
        if (uint.TryParse(regsLine.Substring(start, end - start),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint pc))
            return pc;
        return 0;
    }

    // ── l: load file ──────────────────────────────────────────────────────────

    private void HandleLoad(string args)
    {
        // l ADDR PATH (PATH = rest of line; may contain spaces)
        string trimmed = args.Trim();
        int space = trimmed.IndexOf(' ');
        if (space < 0 || !TryParseAddress(trimmed.Substring(0, space), out uint addr))
        {
            _output.WriteLine("? usage: l ADDR PATH");
            return;
        }
        string path = trimmed.Substring(space + 1).Trim();
        try
        {
            int count = _engine.LoadFile(addr, path);
            _output.WriteLine($"loaded ${count:X} bytes at ${addr:X4}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"? {ex.Message}");
        }
    }

    // ── w: save file ──────────────────────────────────────────────────────────

    private void HandleSave(string args)
    {
        // w ADDR LEN PATH
        string trimmed = args.Trim();
        string[] parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !TryParseAddress(parts[0], out uint addr)
            || !TryParseCount(parts[1], out int len))
        {
            _output.WriteLine("? usage: w ADDR LEN PATH");
            return;
        }
        string path = parts[2].Trim();
        try
        {
            _engine.SaveFile(addr, len, path);
            _output.WriteLine($"wrote ${len:X} bytes from ${addr:X4}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"? {ex.Message}");
        }
    }

    // ── ?: help ───────────────────────────────────────────────────────────────

    private void HandleHelp()
    {
        _output.WriteLine(
            """
            Commands (ADDR/COUNT/LEN/VALUE/bytes = hex, $ optional except 'a' address and 'g' BUDGET):
              m ADDR [COUNT]         hex-dump COUNT bytes (default $40)
              m ADDR: BB BB ...      write bytes at ADDR, echo the dump of what landed
              d ADDR [COUNT]         disassemble COUNT instructions (default 8)
              a $ADDR INSTR          assemble INSTR at $ADDR ($ required); echo; advance cursor
              a INSTR                assemble at cursor (error if no prior a $ADDR ...)
              r                      print registers
              r NAME=VALUE           set register, print registers
              s [N]                  step N instructions (default 1), print each step report
              g [$ADDR] [until $TARGET] [BUDGET]
                                     optionally set PC; run until TARGET/trap/BUDGET cycles
                                     (BUDGET is decimal, default 1000000)
              l ADDR PATH            load raw binary file at ADDR
              w ADDR LEN PATH        save LEN bytes from ADDR to raw binary file
              ?                      print this help
              q                      quit (EOF also quits); blank lines are ignored
            """);
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    /// <summary>Parse an address (hex, optional '$' prefix).</summary>
    private static bool TryParseAddress(string s, out uint address)
    {
        address = 0;
        string t = s.Trim();
        if (t.StartsWith("$", StringComparison.Ordinal))
            t = t.Substring(1);
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out address);
    }

    /// <summary>Parse a count/length (hex, optional '$' prefix).</summary>
    private static bool TryParseCount(string s, out int count)
    {
        count = 0;
        string t = s.Trim();
        if (t.StartsWith("$", StringComparison.Ordinal))
            t = t.Substring(1);
        if (int.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out count))
            return count > 0;
        return false;
    }

    /// <summary>Parse a single hex byte (2 hex digits, no prefix).</summary>
    private static bool TryParseHexByte(string s, out byte value)
    {
        value = 0;
        return s.Length <= 2 && byte.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
