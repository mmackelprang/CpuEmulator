namespace CpuEmulator.Core.Specification;

/// <summary>Marks a class holding CPU spec tables (Registers, Instructions) for the source
/// generator. The generated CPU class is named by stripping a trailing "Spec" from the class
/// name and appending "Cpu", unless <see cref="CpuName"/> overrides it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CpuSpecificationAttribute(string architecture) : Attribute
{
    /// <summary>Architecture identifier surfaced as <c>ICpuCore.Architecture</c>, e.g. "mos6502".</summary>
    public string Architecture { get; } = architecture;

    /// <summary>Optional explicit name for the generated CPU class.</summary>
    public string? CpuName { get; set; }
}
