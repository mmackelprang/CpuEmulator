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
        Cpu = cpuFactory(this);
        _irqTarget.Bind(Cpu.SetIrqLine);
        _nmiTarget.Bind(Cpu.SetNmiLine);

        // Phase 3: map peripherals, then Realize them in registration order.
        foreach (var (kind, start, length, peripheral) in peripheralDefs)
            GetSpace(kind).MapPeripheral(start, length, peripheral);
        foreach (var (_, _, _, peripheral) in peripheralDefs)
            peripheral.Realize(this);
    }

    public IAddressSpace Space(AddressSpaceKind kind) => GetSpace(kind);

    public void Reset() => Cpu.Reset();

    /// <summary>
    /// Run the machine for a cycle budget. M1 semantics are coarse: the CPU runs a slice,
    /// then the scheduler catches up to the CPU's cycle count. The timer milestone will
    /// chunk CPU slices to the next pending event for tighter event timing.
    /// </summary>
    public void Run(long cycles)
    {
        if (cycles <= 0)
            return;
        long target = Cpu.CycleCount + cycles;
        while (Cpu.CycleCount < target)
        {
            long before = Cpu.CycleCount;
            long budget = target - Cpu.CycleCount;
            Cpu.Run(ref budget);
            if (Cpu.CycleCount == before)
                throw new EmulationException(
                    $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            _scheduler.AdvanceTo(Cpu.CycleCount);
        }
    }

    private AddressSpace GetSpace(AddressSpaceKind kind) =>
        _spaces.TryGetValue(kind, out var space)
            ? space
            : throw new MachineConfigurationException($"Machine '{Name}' has no {kind} address space.");

    /// <summary>Lets interrupt lines exist before the CPU does (the CPU factory may consult
    /// or even assert them). Binding replays the line's current state so an assert raised
    /// during CPU construction is not lost.</summary>
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
