namespace CpuEmulator.Core;

/// <summary>
/// The container device (QOM-style): owns the CPU, address spaces, scheduler, and
/// peripherals. Construction is two-phase — all bus mappings exist before any
/// peripheral's Realize runs — and deterministic (registration order).
/// </summary>
public sealed class Machine : IMachineContext
{
    private readonly Dictionary<AddressSpaceKind, AddressSpace> _spaces = [];
    private readonly CycleScheduler _scheduler;
    private readonly LateBoundLine _irqTarget = new();
    private readonly LateBoundLine _nmiTarget = new();

    public string Name { get; }
    public ICpuCore Cpu { get; }
    public IScheduler Scheduler => _scheduler;
    public IInterruptLine IrqLine { get; }
    public IInterruptLine NmiLine { get; }

    public static MachineBuilder Create(string name) => new(name);

    internal Machine(
        string name,
        List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> spaceDefs,
        List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> memoryDefs,
        List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> peripheralDefs,
        Func<IMachineContext, ICpuCore> cpuFactory)
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

        // Phase 2: create the CPU (it may capture spaces), then bind interrupt lines to it.
        Cpu = cpuFactory(this) ?? throw new MachineConfigurationException(
            $"Machine '{name}': CPU factory returned null.");
        _irqTarget.Bind(Cpu.SetIrqLine);
        _nmiTarget.Bind(Cpu.SetNmiLine);
        _scheduler.BindTimeSource(() => Cpu.CycleCount);

        // Phase 3: map peripherals, then Realize them in registration order.
        foreach (var (kind, start, length, peripheral) in peripheralDefs)
            GetSpace(kind).MapPeripheral(start, length, peripheral);
        foreach (var (_, _, _, peripheral) in peripheralDefs)
            peripheral.Realize(this);
    }

    public IAddressSpace Space(AddressSpaceKind kind) => GetSpace(kind);

    public void Reset() => Cpu.Reset();

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
        long start = Cpu.CycleCount;
        if (cycles <= 0)
            return 0;
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
}
