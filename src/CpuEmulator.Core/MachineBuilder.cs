namespace CpuEmulator.Core;

/// <summary>Fluent composition of a machine: declare spaces, memory, CPU, peripherals; Build() wires it.</summary>
public sealed class MachineBuilder
{
    private readonly string _name;
    private readonly List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> _spaceDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> _memoryDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> _peripheralDefs = [];
    private Func<IMachineContext, ICpuCore>? _cpuFactory;
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

        return new Machine(_name, _spaceDefs, _memoryDefs, _peripheralDefs, _cpuFactory);
    }
}
