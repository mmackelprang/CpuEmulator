using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRegistryTests
{
    [Fact]
    public void Machines_assembly_is_referable_from_host_test_context()
    {
        // A trivial proof the Host project graph can resolve BoardMachineFactory + ReferenceSbc:
        // build a 68000 spec and a machine from it. (Replaced by real registry tests in Task 6.)
        BoardSpec spec = ReferenceSbc.Build(
            CpuKind.M68000, new SimpleUart(), new IntervalTimer(), new byte[0x1_0000]);
        Machine machine = BoardMachineFactory.Build(spec, ExecutionTier.Interpreter);
        Assert.Equal("ReferenceSbc-M68000", machine.Name);
    }
}
