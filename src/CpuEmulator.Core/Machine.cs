namespace CpuEmulator.Core;

/// <summary>
/// The container device (QOM-style): owns the CPU, address spaces, scheduler, and
/// peripherals. Construction is two-phase — all bus mappings exist before any
/// peripheral's Realize runs — and deterministic (registration order).
/// </summary>
public sealed class Machine : IMachineContext, ICoprocessorControl
{
    private readonly Dictionary<AddressSpaceKind, AddressSpace> _spaces = [];
    private readonly CycleScheduler _scheduler;
    private readonly LateBoundLine _irqTarget = new();
    private readonly LateBoundLine _nmiTarget = new();
    private readonly ICpuCore? _coprocessor;
    private readonly double _coprocessorRatio;
    private bool _z80Active;                       // false at reset: the primary runs (ADR 0015 Decision 1)
    private long _coprocessorCyclesContributed;    // coprocessor cycles run so far (for the virtual clock)
    private bool _sliceEndRequested;               // set by SetCoprocessorActive to end the running slice

    public string Name { get; }
    public ICpuCore Cpu { get; }
    public IScheduler Scheduler => _scheduler;
    public IInterruptLine IrqLine { get; }
    public IInterruptLine NmiLine { get; }

    /// <summary>True while the coprocessor is the bus master (the primary is DMA-suspended). False on a
    /// single-CPU machine and at reset. Test/host introspection.</summary>
    public bool CoprocessorActive => _z80Active;

    /// <summary>The optional coprocessor core (null on every single-CPU machine). Test/host introspection.</summary>
    public ICpuCore? Coprocessor => _coprocessor;

    public static MachineBuilder Create(string name) => new(name);

    internal Machine(
        string name,
        List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> spaceDefs,
        List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> memoryDefs,
        List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> peripheralDefs,
        Func<IMachineContext, ICpuCore> cpuFactory,
        CoprocessorBuild? coprocessor = null)
    {
        Name = name;
        _scheduler = new CycleScheduler();
        IrqLine = new InterruptLine(_irqTarget.Set);
        NmiLine = new InterruptLine(_nmiTarget.Set);

        // Phase 1: construct spaces and map memory.
        foreach (var (kind, addressBits, options) in spaceDefs)
            _spaces[kind] = new AddressSpace(kind, addressBits, options);
        foreach (var (kind, start, backing, writable) in memoryDefs)
            GetSpace(kind).MapMemory(start, backing, writable);

        // Phase 2: create the primary CPU, then bind interrupt lines + the scheduler clock to it.
        Cpu = cpuFactory(this) ?? throw new MachineConfigurationException(
            $"Machine '{name}': CPU factory returned null.");
        _irqTarget.Bind(Cpu.SetIrqLine);
        _nmiTarget.Bind(Cpu.SetNmiLine);

        if (coprocessor is null)
        {
            // Single-CPU path: byte-for-byte the pre-PR-I behavior.
            _scheduler.BindTimeSource(() => Cpu.CycleCount);
        }
        else
        {
            // Dual-CPU path (ADR 0015). The coprocessor is built over a TranslatingAddressSpace wrapping
            // the primary program space, so the coprocessor core is unchanged. Interrupts stay on the
            // PRIMARY only (Decision 5). The scheduler runs in the primary cycle domain plus the
            // coprocessor's run time converted by the clock ratio (the virtual 6502-domain clock).
            _coprocessorRatio = coprocessor.ClockRatioToPrimary;
            var programSpace = GetSpace(AddressSpaceKind.Program);
            var translatingBus = new TranslatingAddressSpace(programSpace, coprocessor.Translation);
            _coprocessor = coprocessor.Factory(new CoprocessorContext(this, translatingBus))
                ?? throw new MachineConfigurationException(
                    $"Machine '{name}': coprocessor factory returned null.");
            // The coprocessor's interrupt inputs are intentionally left unbound (Decision 5).
            _scheduler.BindTimeSource(() =>
                Cpu.CycleCount + (long)Math.Round(_coprocessorCyclesContributed / _coprocessorRatio));
        }

        // Phase 3: map peripherals, then Realize them in registration order.
        foreach (var (kind, start, length, peripheral) in peripheralDefs)
            GetSpace(kind).MapPeripheral(start, length, peripheral);
        foreach (var (_, _, _, peripheral) in peripheralDefs)
            peripheral.Realize(this);
    }

    public IAddressSpace Space(AddressSpaceKind kind) => GetSpace(kind);

    public void Reset() => Cpu.Reset();

    /// <summary>ICoprocessorControl (ADR 0015 Decisions 1 + 3): a control-port peripheral flips which CPU
    /// runs. Sets _z80Active and requests the current run slice end so the switch takes effect on the next
    /// dispatch (the writing instruction completes first). Inert on a single-CPU machine — but a control
    /// port is only Realized with this Machine when a coprocessor exists, so this is never reached there.</summary>
    public void SetCoprocessorActive(bool active)
    {
        _z80Active = active;
        _sliceEndRequested = true;
    }

    /// <summary>
    /// Run for a cycle budget; returns cycles actually executed (may overshoot by up to
    /// one instruction). Slices chunk to the next live event, so callbacks fire at their
    /// exact cycle and their IRQs land at the very next instruction boundary. An event
    /// scheduled MID-slice still fires at its exact cycle in scheduler time, but its IRQ
    /// reaches the CPU at the end of the running slice — latency bounded by the slice
    /// (one instruction under monitor budget-1 stepping). Empty queue = one full-budget
    /// slice (the pre-PR-#11 behavior, byte-identical).
    /// </summary>
    public long Run(long cycles)
    {
        if (cycles <= 0)
            return 0;
        if (_coprocessor is null)
            return RunSingleCpu(cycles);
        return RunDualCpu(cycles);
    }

    /// <summary>The pre-PR-I single-CPU run loop, unchanged (ADR 0015: single-CPU path byte-for-byte
    /// identical). Drives the one Cpu, slicing to the next scheduled event.</summary>
    private long RunSingleCpu(long cycles)
    {
        long start = Cpu.CycleCount;
        long target = start + cycles;
        while (Cpu.CycleCount < target)
        {
            long before = Cpu.CycleCount;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, before + 1) // events at/behind the CPU: 1-cycle floor
                : target;
            long budget = sliceEnd - before;
            Cpu.Run(ref budget);
            if (Cpu.CycleCount <= before)
                throw new EmulationException(
                    $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            _scheduler.AdvanceTo(Cpu.CycleCount);
        }
        return Cpu.CycleCount - start;
    }

    /// <summary>The dual-CPU run loop (ADR 0015 Decision 1): drive ONLY the active core (run-one-then-the-
    /// other; the dormant core is never scheduled). The budget is in the PRIMARY (virtual 6502) cycle
    /// domain (Decision 5). When the primary runs, the virtual clock = Cpu.CycleCount advances 1:1; when
    /// the coprocessor runs, its cycles convert into the virtual clock via the ratio (the bound time
    /// source reads _coprocessorCyclesContributed). A control-port write (SetCoprocessorActive) ends the
    /// running slice; a pending interrupt forces a switch back to the primary.</summary>
    private long RunDualCpu(long cycles)
    {
        long virtualStart = _scheduler.CurrentCycle;
        long target = virtualStart + cycles;
        while (_scheduler.CurrentCycle < target)
        {
            _sliceEndRequested = false;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, _scheduler.CurrentCycle + 1)
                : target;

            // Drive the ACTIVE core ONE INSTRUCTION AT A TIME, yielding the instant a $CnXX write flips it
            // (SetCoprocessorActive sets _sliceEndRequested INSIDE Step). ADR 0017 Decision 3.
            while (_scheduler.CurrentCycle < sliceEnd && !_sliceEndRequested)
            {
                if (!_z80Active)
                {
                    long before = Cpu.CycleCount;
                    Cpu.Step();                                   // exactly one 6502 instruction
                    if (Cpu.CycleCount <= before)
                        throw new EmulationException(
                            $"CPU '{Cpu.Architecture}' made no progress during Step; aborting to avoid a hang.");
                }
                else
                {
                    ICpuCore copro = _coprocessor!;
                    long coproBefore = copro.CycleCount;
                    copro.Step();                                 // exactly one Z80 instruction
                    long coproRan = copro.CycleCount - coproBefore;
                    if (coproRan <= 0)
                        throw new EmulationException(
                            $"Coprocessor '{copro.Architecture}' made no progress during Step; aborting to avoid a hang.");
                    _coprocessorCyclesContributed += coproRan;    // convert to the virtual clock via the ratio
                }

                // Fire any events due at the new (derived) virtual time.
                _scheduler.AdvanceTo(_scheduler.CurrentCycle);

                // A pending interrupt forces a switch to the primary (ADR 0015 Decision 5: all interrupts to
                // the 6502; while the coprocessor runs the primary is DMA-suspended, so an IRQ resumes it).
                if (_z80Active && (IrqLine.IsAsserted || NmiLine.IsAsserted))
                    _z80Active = false;
                // _sliceEndRequested (set by a $CnXX write this instruction) breaks the inner loop; _z80Active
                // already selects the other core for the next instruction (the writing instruction completed
                // first -- ADR 0015 OQ5: the switch takes effect on the next dispatch).
            }
        }
        return _scheduler.CurrentCycle - virtualStart;
    }

    private AddressSpace GetSpace(AddressSpaceKind kind) =>
        _spaces.TryGetValue(kind, out var space)
            ? space
            : throw new MachineConfigurationException($"Machine '{Name}' has no {kind} address space.");

    /// <summary>Lets interrupt lines exist before the CPU does (the CPU factory may consult
    /// or even assert them). Binding replays the line's current state so an assert raised
    /// during CPU construction is not lost. Replays level, not edges — a pulse (assert then
    /// release) during the construction window is intentionally invisible; only the final
    /// level is forwarded at bind.</summary>
    private sealed class LateBoundLine
    {
        private Action<bool>? _target;
        private bool _lastValue;

        public void Set(bool value)
        {
            _lastValue = value;
            _target?.Invoke(value);
        }

        public void Bind(Action<bool> target)
        {
            _target = target;
            if (_lastValue)
                target(true);
        }
    }

    /// <summary>The IMachineContext the coprocessor core is constructed with: identical to the Machine
    /// except Space(Program) returns the TranslatingAddressSpace wrapper (so CpuCoreFactory builds the
    /// coprocessor core over the translated bus). All other members forward to the Machine — the
    /// coprocessor shares the one scheduler + interrupt domain. The Io space (if any) is shared
    /// untranslated; the SoftCard Z80 reaches I/O through the translation's $E000->$C000 branch on the
    /// Program bus, so a separate Io space is not declared for the SoftCard board.</summary>
    private sealed class CoprocessorContext(Machine machine, IAddressSpace translatedProgram) : IMachineContext
    {
        public IScheduler Scheduler => machine.Scheduler;
        public IInterruptLine IrqLine => machine.IrqLine;
        public IInterruptLine NmiLine => machine.NmiLine;
        public IAddressSpace Space(AddressSpaceKind kind) =>
            kind == AddressSpaceKind.Program ? translatedProgram : machine.Space(kind);
    }
}
