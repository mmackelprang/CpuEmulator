namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

/// <summary>The Z80 tier driver: constructs a 16-bit program <c>AddressSpace</c> (the whole 64 KiB
/// workload image mapped at 0x0000) + a 16-bit I/O space, a <c>Z80Cpu</c>, and (for Tier-1) a
/// <c>JittedCpu&lt;Z80Cpu&gt;</c> — the same construction the proven <c>CpmBdosHost</c> test fixture
/// uses. Seeds <c>PC=StartPc</c> + <c>SP=0xFFFE</c>.
/// <para>The Z80 stop condition is workload-dependent. Z80-W2 (the arithmetic kernel) terminates on
/// the fixed cycle cap (the shared <see cref="TierRunner"/> loop), with NO host service. Z80-W1 (the
/// ZEXDOC prefix) runs real CP/M code: it services the BDOS CALL (PC==0x0005, fn-2/fn-9 + host RET)
/// and honors the warm-boot sentinel (PC==0x0000) as an EARLY-STOP guard — both folded into
/// <see cref="Z80Instance.AdvanceSlice"/>. W1 itself is a capped WINDOW (a deterministic ZEXDOC
/// prefix, NOT run-to-banner), so the window cap is the normal terminator; the warm-boot guard only
/// matters if ZEX warm-boots before the window closes.</para></summary>
public sealed class Z80TierDriver : ITierDriver
{
    public string Architecture => "z80";

    public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

    private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        // The Z80 workloads carry a full 64 KiB image (the .com already placed at 0x0100 + page-zero
        // seeded for W1; the kernel placed at 0x0100 for W2), so the driver just maps the whole image.
        mem.MapMemory(0x0000, (byte[])w.Image.Clone(), writable: true);
        var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        var cpu = new Z80Cpu(mem, io);
        cpu.SetRegister("PC", w.StartPc);
        cpu.SetRegister("SP", 0xFFFE);     // a sane CP/M-ish stack; W1's ZEX prefix sets up its own early
        JittedCpu<Z80Cpu>? j = jit ? new JittedCpu<Z80Cpu>(cpu, Z80Cpu.JitTarget, mem, io, options) : null;
        return new Z80Instance(cpu, mem, j, w);
    }

    /// <summary>A live Z80 tier instance. <see cref="AdvanceSlice"/> advances toward the slice
    /// budget; for the BDOS workload (W1) it steps one instruction at a time (the budget-1 JIT idiom
    /// mirrors <c>CpmBdosHost.Run</c> so PC surfaces at the BDOS / warm-boot boundary EXACTLY), and
    /// for the kernel (W2) it advances a single budgeted Run (Tier-1) or a Step loop (Tier-0) — the
    /// large window being the fair throughput measurement since W2 has no host boundary.</summary>
    private sealed class Z80Instance : ITierInstance
    {
        private const ushort WarmBoot = 0x0000;  // CP/M warm-boot sentinel — the early-stop guard (W1)
        private const ushort BdosEntry = 0x0005; // the CALL target the BDOS intercept fires on (W1)

        private readonly Z80Cpu _cpu;
        private readonly AddressSpace _mem;
        private readonly JittedCpu<Z80Cpu>? _jit;
        private readonly bool _usesBdos;
        private bool _parked;

        public Z80Instance(Z80Cpu cpu, AddressSpace mem, JittedCpu<Z80Cpu>? jit, BenchWorkload w)
        {
            _cpu = cpu;
            _mem = mem;
            _jit = jit;
            _usesBdos = w.UsesCpmBdos;
        }

        public long CycleCount => _cpu.CycleCount;
        public ushort CurrentPc => (ushort)_cpu.GetRegister("PC");
        public bool ParkedThisSlice => _parked;

        public void AdvanceSlice(long maxCycles)
        {
            _parked = false;
            long localTarget = _cpu.CycleCount + maxCycles;

            if (_usesBdos)
            {
                // W1 — the ZEXDOC prefix: step instruction-by-instruction so PC surfaces at the BDOS
                // CALL + warm-boot boundaries exactly, servicing BDOS host-side as we go.
                while (_cpu.CycleCount < localTarget)
                {
                    ushort pc = (ushort)_cpu.GetRegister("PC");
                    if (pc == WarmBoot) { _parked = true; return; }    // early-stop (rare for a capped prefix)
                    if (pc == BdosEntry) { ServiceBdos(_cpu, _mem); continue; }   // service + RET (not parked)
                    long prevCycles = _cpu.CycleCount;                            // 0-T-state infinite-loop guard
                    if (_jit is not null) { long b = 1; _jit.Run(ref b); }        // budget-1: exact PC surfacing
                    else _cpu.Step();
                    if (_cpu.CycleCount == prevCycles)
                        throw new InvalidOperationException("Z80: instruction advanced 0 T-states — infinite-loop guard (subject diverged)");
                }
                return;
            }

            // W2 — the arithmetic kernel: no host boundary, so a large window is the fair throughput
            // measurement. Tier-1 runs one budgeted Run (block-cached + chained); Tier-0 steps.
            if (_jit is not null)
            {
                long budget = maxCycles;
                _jit.Run(ref budget);
            }
            else
            {
                while (_cpu.CycleCount < localTarget)
                {
                    long prevCycles = _cpu.CycleCount;                            // 0-T-state infinite-loop guard
                    _cpu.Step();
                    if (_cpu.CycleCount == prevCycles)
                        throw new InvalidOperationException("Z80: instruction advanced 0 T-states — infinite-loop guard (subject diverged)");
                }
            }
        }

        // ── A port of the PROVEN CpmBdosHost.ServiceBdos / ReturnFromBdos convention (M3.5-2) ─────────
        // fn-2 = console out (char in E); fn-9 = print the $-terminated string at DE; any other fn is a
        // silent RET (ZEX uses only 2 + 9). Console output is DISCARDED here — this is a THROUGHPUT run,
        // not a correctness transcript (that is the ZEX test's job); we consume + RET only to keep ZEX
        // advancing. Then host-RET by popping the return address the CALL pushed. The source of truth for
        // this convention remains tests/CpuEmulator.Tests/Zex/CpmBdosHost.cs.
        private static void ServiceBdos(Z80Cpu cpu, AddressSpace mem)
        {
            byte fn = (byte)cpu.GetRegister("C");
            switch (fn)
            {
                case 2: // console out: char in E — discarded (throughput run)
                    _ = (byte)cpu.GetRegister("E");
                    break;
                case 9: // print $-terminated string at DE — discarded (throughput run)
                {
                    ushort addr = (ushort)cpu.GetRegister("DE");
                    for (int guard = 0; guard < 0x10000; guard++)
                    {
                        byte b = mem.Read8(addr);
                        if (b == (byte)'$') break;
                        addr = (ushort)(addr + 1);
                    }
                    break;
                }
                default:
                    break; // unimplemented BDOS function — silent RET (ZEX never hits this)
            }
            ReturnFromBdos(cpu, mem);
        }

        /// <summary>Host-side RET: pop the 16-bit return address off the Z80 stack and set PC (a port
        /// of <c>CpmBdosHost.ReturnFromBdos</c>).</summary>
        private static void ReturnFromBdos(Z80Cpu cpu, AddressSpace mem)
        {
            ushort sp = (ushort)cpu.GetRegister("SP");
            byte lo = mem.Read8(sp);
            byte hi = mem.Read8((ushort)(sp + 1));
            cpu.SetRegister("SP", (ushort)(sp + 2));
            cpu.SetRegister("PC", (ushort)((hi << 8) | lo));
        }
    }
}
