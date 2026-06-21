namespace CpuEmulator.Core;

/// <summary>Fluent composition of a machine: declare spaces, memory, CPU, peripherals; Build() wires it.</summary>
public sealed class MachineBuilder
{
    private readonly string _name;
    private readonly List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> _spaceDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> _memoryDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> _peripheralDefs = [];
    private Func<IMachineContext, ICpuCore>? _cpuFactory;
    private Func<IMachineContext, ICpuCore>? _coprocessorFactory;
    private IAddressTranslation? _coprocessorTranslation;
    private double _coprocessorClockRatio;
    private bool _built;

    internal MachineBuilder(string name) => _name = name;

    public MachineBuilder WithAddressSpace(AddressSpaceKind kind, int addressBits, AddressSpaceOptions? options = null)
    {
        if (_spaceDefs.Any(d => d.Kind == kind))
            throw new MachineConfigurationException($"Address space {kind} is declared twice.");
        _spaceDefs.Add((kind, addressBits, options));
        return this;
    }

    public MachineBuilder WithCpu(Func<IMachineContext, ICpuCore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _cpuFactory = factory;
        return this;
    }

    /// <summary>Declare an optional bus-arbitrated coprocessor that shares the primary's program space
    /// through <paramref name="translation"/> (ADR 0015 Decision 2). The coprocessor is dormant at reset
    /// and activated via ICoprocessorControl (a control-port peripheral flips it). Calling this puts the
    /// Machine on the dual-CPU construction + run path; NOT calling it leaves the single-CPU path
    /// byte-for-byte unchanged. <paramref name="clockRatioToPrimary"/> (e.g. ~2.0 for the SoftCard Z80)
    /// converts coprocessor run time into primary-domain scheduler cycles (ADR 0015 Decision 5).</summary>
    public MachineBuilder WithCoprocessor(
        Func<IMachineContext, ICpuCore> coprocessorFactory,
        IAddressTranslation translation,
        double clockRatioToPrimary)
    {
        ArgumentNullException.ThrowIfNull(coprocessorFactory);
        ArgumentNullException.ThrowIfNull(translation);
        if (clockRatioToPrimary <= 0)
            throw new MachineConfigurationException(
                $"Coprocessor clock ratio must be positive; got {clockRatioToPrimary}.");
        _coprocessorFactory = coprocessorFactory;
        _coprocessorTranslation = translation;
        _coprocessorClockRatio = clockRatioToPrimary;
        return this;
    }

    public MachineBuilder WithRam(AddressSpaceKind kind, uint start, uint length)
    {
        _memoryDefs.Add((kind, start, new byte[length], true));
        return this;
    }

    public MachineBuilder WithRom(AddressSpaceKind kind, uint start, byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _memoryDefs.Add((kind, start, image, false));
        return this;
    }

    public MachineBuilder WithPeripheral(AddressSpaceKind kind, uint start, uint length, IPeripheral peripheral)
    {
        ArgumentNullException.ThrowIfNull(peripheral);
        _peripheralDefs.Add((kind, start, length, peripheral));
        return this;
    }

    /// <summary>Construct the machine. May only be called once; a Build() that throws still consumes the builder.</summary>
    public Machine Build()
    {
        if (_built)
            throw new MachineConfigurationException("Build() may only be called once per builder.");
        _built = true;

        if (_cpuFactory is null)
            throw new MachineConfigurationException($"Machine '{_name}' has no CPU. Call WithCpu().");
        if (_spaceDefs.All(d => d.Kind != AddressSpaceKind.Program))
            throw new MachineConfigurationException($"Machine '{_name}' has no Program address space.");

        CoprocessorBuild? coprocessor = _coprocessorFactory is null
            ? null
            : new CoprocessorBuild(_coprocessorFactory, _coprocessorTranslation!, _coprocessorClockRatio);

        return new Machine(_name, _spaceDefs, _memoryDefs, _peripheralDefs, _cpuFactory, coprocessor);
    }
}

/// <summary>The resolved coprocessor declaration the MachineBuilder hands to the Machine ctor: the core
/// factory, the logical->physical translation, and the clock ratio. Null on every single-CPU board.</summary>
internal sealed record CoprocessorBuild(
    Func<IMachineContext, ICpuCore> Factory,
    IAddressTranslation Translation,
    double ClockRatioToPrimary);
