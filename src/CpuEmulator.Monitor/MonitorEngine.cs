using CpuEmulator.Core;

namespace CpuEmulator.Monitor;

public enum RunStopReason { TargetReached, Trapped, BudgetExhausted }
public sealed record RunReport(RunStopReason Reason, uint Pc, long CyclesRun);
public sealed record MonitorStepReport(
    uint PcBefore, bool InterruptServiced, string Disassembly, long Cycles, string Registers);

/// <summary>
/// CPU-agnostic machine-language monitor engine. Provides programmatic access to load/save
/// memory, dump/modify memory, disassemble, read/set registers, step, run, and assemble.
/// Built over (ICpuCore, IAddressSpace, IMonitorSupport) — the 6502 satisfies all three as
/// (cpu, space, cpu).
/// </summary>
public sealed class MonitorEngine
{
    private readonly ICpuCore _cpu;
    private readonly IAddressSpace _memory;
    private readonly IMonitorSupport _support;
    private readonly uint _addressMask;
    private readonly int _addressDigits;
    private readonly Func<long, long> _run;

    /// <param name="run">Optional slice driver: cycle budget in, cycles consumed out. The
    /// host passes Machine.Run so monitor g/s tick scheduled peripherals; null = the bare
    /// CPU (pre-host behavior, byte-identical). CONTRACT: given a budget of 1 the driver
    /// executes exactly one instruction — Machine.Run and ICpuCore.Run both qualify — which
    /// keeps Step reports and RunUntil's per-instruction trap/target checks exact.</param>
    public MonitorEngine(ICpuCore cpu, IAddressSpace memory, IMonitorSupport support,
                         Func<long, long>? run = null)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(support);
        _cpu = cpu;
        _memory = memory;
        _support = support;
        int bits = memory.AddressBits;
        _addressMask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1;
        _addressDigits = (bits + 3) / 4;
        _run = run ?? RunBare;
    }

    /// <summary>Default slice driver: the bare ICpuCore.Run (no scheduler in play).</summary>
    private long RunBare(long cycles)
    {
        long budget = cycles;
        _cpu.Run(ref budget);
        return cycles - budget;
    }

    private uint Pc => (uint)_cpu.GetRegister(_support.ProgramCounterName) & _addressMask;

    /// <summary>Display read: peek where the device is honest; fall back to a live bus read
    /// only when TryPeek returns false (no honest peek). Used by ReadMemory, Disassemble,
    /// and Step report prefetch — execution reads, LoadBytes/SaveBytes, and TryAssembleAt
    /// stay live-bus (recorded scope).</summary>
    private byte PeekOrRead(uint address) =>
        _memory.TryPeek8(address, out byte value) ? value : _memory.Read8(address);

    /// <summary>Current program counter, masked to the address space. Reads and writes
    /// route through <see cref="IMonitorSupport.ProgramCounterName"/> so callers (the REPL,
    /// hosts) never hardcode a register name — the engine stays CPU-agnostic.</summary>
    public uint ProgramCounter
    {
        get => Pc;
        set => _cpu.SetRegister(_support.ProgramCounterName, value & _addressMask);
    }

    /// <summary>Hex digits needed to print an address ((AddressBits + 3) / 4 — 4 for the
    /// 6502). Display formatting for callers, so address widths never hardcode 16 bits.</summary>
    public int AddressDigits => _addressDigits;

    // ── Memory load/save ─────────────────────────────────────────────────────

    /// <summary>Write bytes into the address space, wrapping at the address mask.</summary>
    public void LoadBytes(uint address, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            _memory.Write8((address + (uint)i) & _addressMask, bytes[i]);
    }

    /// <summary>Load a raw binary file at address. Returns the number of bytes loaded.</summary>
    public int LoadFile(uint address, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        LoadBytes(address, bytes);
        return bytes.Length;
    }

    /// <summary>Read bytes from the address space, wrapping at the address mask.</summary>
    public byte[] SaveBytes(uint address, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = _memory.Read8((address + (uint)i) & _addressMask);
        return result;
    }

    /// <summary>Save bytes from the address space to a raw binary file.</summary>
    public void SaveFile(uint address, int length, string path)
    {
        byte[] bytes = SaveBytes(address, length);
        File.WriteAllBytes(path, bytes);
    }

    // ── Memory display/modify ────────────────────────────────────────────────

    /// <summary>
    /// Hex-dump count bytes starting at address.
    /// Format per Ground truth D: {addr:X4}: {hex,-47} |{ascii}|, 16 bytes/line.
    /// Display reads peek where the device is honest; devices without TryPeek fall back to
    /// live reads. SaveBytes (w) remains a bus sweep — saving MMIO is a bus read sweep.
    /// </summary>
    public string ReadMemory(uint address, int count)
    {
        var sb = new System.Text.StringBuilder();
        int remaining = count;
        uint addr = address & _addressMask;
        while (remaining > 0)
        {
            int lineCount = Math.Min(remaining, 16);
            var hexParts = new System.Text.StringBuilder();
            var asciiParts = new System.Text.StringBuilder();
            for (int i = 0; i < lineCount; i++)
            {
                byte b = PeekOrRead((addr + (uint)i) & _addressMask);
                if (i > 0) hexParts.Append(' ');
                hexParts.Append(b.ToString("X2"));
                asciiParts.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            string hexStr = hexParts.ToString();
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(addr.ToString($"X{_addressDigits}"));
            sb.Append(": ");
            sb.Append(hexStr.PadRight(47));
            sb.Append(" |");
            sb.Append(asciiParts);
            sb.Append('|');
            addr = (addr + (uint)lineCount) & _addressMask;
            remaining -= lineCount;
        }
        return sb.ToString();
    }

    /// <summary>Write bytes into the address space.</summary>
    public void WriteMemory(uint address, byte[] bytes) => LoadBytes(address, bytes);

    // ── Disassembly ──────────────────────────────────────────────────────────

    /// <summary>
    /// Disassemble count instructions starting at address, walking via InstructionLength.
    /// Format per Ground truth D: {addr:X4}: {bytes,-8}  {text}
    /// Display reads peek where the device is honest; devices without TryPeek fall back to
    /// live reads.
    /// </summary>
    public string Disassemble(uint address, int count)
    {
        var sb = new System.Text.StringBuilder();
        uint addr = address & _addressMask;
        for (int i = 0; i < count; i++)
        {
            byte opcode = PeekOrRead(addr);
            byte lo = PeekOrRead((addr + 1) & _addressMask);
            byte hi = PeekOrRead((addr + 2) & _addressMask);
            string text = _support.Disassemble(opcode, lo, hi);
            int len = _support.InstructionLength(opcode);

            // Build bytes string (space-separated)
            var bytesStr = new System.Text.StringBuilder();
            for (int b = 0; b < len; b++)
            {
                if (b > 0) bytesStr.Append(' ');
                bytesStr.Append(PeekOrRead((addr + (uint)b) & _addressMask).ToString("X2"));
            }

            if (sb.Length > 0) sb.AppendLine();
            sb.Append(addr.ToString($"X{_addressDigits}"));
            sb.Append(": ");
            sb.Append(bytesStr.ToString().PadRight(8));
            sb.Append("  ");
            sb.Append(text);

            addr = (addr + (uint)len) & _addressMask;
        }
        return sb.ToString();
    }

    // ── Registers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Format registers line per Ground truth D:
    /// A=00 X=05 Y=00 S=FD P=B0 PC=0202 CYC=42
    /// Each register at RegisterBits/4 hex digits, CYC decimal.
    /// </summary>
    public string Registers()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string name in _cpu.RegisterNames)
        {
            if (sb.Length > 0) sb.Append(' ');
            ulong value = _cpu.GetRegister(name);
            int bits = _support.RegisterBits(name);
            int digits = bits / 4;
            sb.Append(name);
            sb.Append('=');
            sb.Append(value.ToString($"X{digits}"));
        }
        sb.Append(" CYC=");
        sb.Append(_cpu.CycleCount);
        return sb.ToString();
    }

    /// <summary>Set a named register by value.</summary>
    public void SetRegister(string name, ulong value) => _cpu.SetRegister(name, value);

    // ── Execution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Execute one instruction — or one interrupt service — and report which.
    /// IMonitorSupport.InterruptPending is sampled BEFORE stepping: when true, the CPU's
    /// Step will service the interrupt instead of the instruction at PC (the contract),
    /// so the report says so rather than naming an instruction that did not run.
    /// </summary>
    public MonitorStepReport Step()
    {
        uint pcBefore = Pc;
        bool interrupt = _support.InterruptPending;
        string disassembly;
        if (interrupt)
        {
            disassembly = "(interrupt serviced)";
        }
        else
        {
            byte opcode = PeekOrRead(pcBefore);
            disassembly = _support.Disassemble(
                opcode,
                PeekOrRead((pcBefore + 1) & _addressMask),
                PeekOrRead((pcBefore + 2) & _addressMask));
        }
        long cyclesBefore = _cpu.CycleCount;
        _run(1); // exactly one instruction — see the ctor contract
        return new MonitorStepReport(
            pcBefore, interrupt, disassembly, _cpu.CycleCount - cyclesBefore, Registers());
    }

    /// <summary>
    /// Run for approximately the given cycle budget, returning cycles actually consumed.
    /// May overshoot by at most one instruction — both slice drivers share this contract:
    /// the bare ICpuCore.Run and a host-wired Machine.Run each stop at the first
    /// instruction boundary at or past the budget.
    /// </summary>
    public long Run(long cycles) => _run(cycles);

    /// <summary>
    /// Run until targetPc is reached, PC traps (parks), or maxCycles exhausted.
    /// Per instruction: check Pc == targetPc BEFORE stepping (TargetReached; returns 0
    /// cycles if already at target); Step(); then Pc == before detects trap (Trapped).
    /// </summary>
    public RunReport RunUntil(uint targetPc, long maxCycles)
    {
        targetPc &= _addressMask;
        long start = _cpu.CycleCount;
        while (true)
        {
            uint before = Pc;
            if (before == targetPc)
                return new RunReport(RunStopReason.TargetReached, before, _cpu.CycleCount - start);
            _run(1); // exactly one instruction — see the ctor contract
            uint after = Pc;
            if (after == before)
                return new RunReport(RunStopReason.Trapped, after, _cpu.CycleCount - start);
            if (_cpu.CycleCount - start >= maxCycles)
                return new RunReport(RunStopReason.BudgetExhausted, after, _cpu.CycleCount - start);
        }
    }

    // ── Assembly ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Assemble one instruction at an address and write its bytes to memory.
    /// One address-aware convenience over IMonitorSupport.TryAssemble: a '$hhhh' operand
    /// the table rejects (e.g. 'BNE $0205' — branches take relative offsets) is retried
    /// as offset = target - (address + L) per candidate length L; the first L whose
    /// assembled instruction is exactly L bytes is the fixed point. Offsets that wrap
    /// the address space are not resolved (out-of-range error).
    /// </summary>
    public bool TryAssembleAt(uint address, string instruction, out byte[] bytes, out string? error)
    {
        address &= _addressMask;
        string text = instruction.Trim();
        int space = text.IndexOf(' ');
        string mnemonic = space < 0 ? text : text.Substring(0, space);
        string operand = space < 0 ? string.Empty : text.Substring(space + 1);

        if (!_support.TryAssemble(mnemonic, operand, out bytes, out error)
            && TryParseAbsoluteTarget(operand, out uint target))
        {
            for (int length = 2; length <= 3; length++)
            {
                int offset = (int)target - (int)((address + (uint)length) & _addressMask);
                if (offset < -128 || offset > 127)
                    continue;
                string relative = "*" + (offset >= 0 ? "+" : string.Empty)
                    + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_support.TryAssemble(mnemonic, relative, out byte[] candidate, out _)
                    && candidate.Length == length)
                {
                    bytes = candidate;
                    error = null;
                    break;
                }
            }
        }
        if (error != null)
            return false;
        for (int i = 0; i < bytes.Length; i++)
            _memory.Write8((address + (uint)i) & _addressMask, bytes[i]);
        return true;
    }

    /// <summary>Parse a '$'+4-hex-digit absolute address, returning false otherwise.</summary>
    private static bool TryParseAbsoluteTarget(string operand, out uint target)
    {
        target = 0;
        string t = operand.Trim();
        if (t.Length == 5 && t[0] == '$'
            && ushort.TryParse(t.Substring(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort v))
        {
            target = v;
            return true;
        }
        return false;
    }
}
