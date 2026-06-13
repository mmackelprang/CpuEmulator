using CpuEmulator.Core.Specification;

namespace CpuEmulator.Tests.Generators;

/// <summary>Pins the M3.1a DSL form: a micro-op register argument is a bare register-NAME
/// string literal (the retired <c>Reg</c> enum is gone). The <see cref="Spec"/> factories and
/// <see cref="Op"/> records carry the name as a <see cref="string"/>.</summary>
public class DslFormTests
{
    [Fact]
    public void Load_factory_stores_the_register_name_string()
    {
        var op = Assert.IsType<LoadRegOp>(Spec.Load("A"));
        Assert.Equal("A", op.Target);
    }

    [Fact]
    public void Transfer_factory_stores_both_register_name_strings()
    {
        var op = Assert.IsType<TransferOp>(Spec.Transfer("A", "X"));
        Assert.Equal("A", op.Source);
        Assert.Equal("X", op.Target);
    }

    [Fact]
    public void Register_op_factories_round_trip_arbitrary_names()
    {
        // The factories take ANY string — validation against the Registers table is the
        // generator's job (CPUGEN008), not the factory's. This is the genericity win: a name
        // the 6502 never had (BC) is a legal authoring value.
        Assert.Equal("BC", Assert.IsType<StoreRegOp>(Spec.Store("BC")).Source);
        Assert.Equal("HL", Assert.IsType<IncrementOp>(Spec.Increment("HL")).Target);
        Assert.Equal("BC", Assert.IsType<SetNZOp>(Spec.SetNZ("BC")).Source);
        Assert.Equal("HL", Assert.IsType<CompareOp>(Spec.Compare("HL")).Source);
        Assert.Equal("BC", Assert.IsType<DecrementOp>(Spec.Decrement("BC")).Target);
        Assert.Equal("HL", Assert.IsType<PushOp>(Spec.Push("HL")).Source);
        Assert.Equal("BC", Assert.IsType<PullOp>(Spec.Pull("BC")).Target);
    }

    [Fact]
    public void Spec_authored_with_string_form_compiles_through_the_generator()
    {
        // The shared happy-path fixture now uses Load("A")/SetNZ("A") (the migrated form).
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);
        Assert.Empty(result.AllErrors);
    }
}
