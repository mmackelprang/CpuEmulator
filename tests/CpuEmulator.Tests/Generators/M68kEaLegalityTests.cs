using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kEaLegalityTests
{
    // The EA-category check: (mode, reg, category) → legal? The 68000 buckets:
    //  DataAlterable    = Dn, (An), (An)+, -(An), d16(An), d8(An,Xn), abs.w, abs.l         (NOT An, NOT PC, NOT #imm)
    //  Control          = (An), d16(An), d8(An,Xn), abs.w, abs.l, d16(PC), d8(PC,Xn)       (NOT Dn/An/(An)+/-(An)/#imm)
    //  All (data)       = every mode incl. #imm and PC-relative
    [Theory]
    [InlineData(0, 0, "DataAlterable", true)]    // Dn — legal data-alterable
    [InlineData(1, 0, "DataAlterable", false)]   // An — NOT data-alterable
    [InlineData(7, 4, "DataAlterable", false)]   // #imm — NOT alterable
    [InlineData(7, 2, "Control", true)]          // d16(PC) — legal control
    [InlineData(3, 0, "Control", false)]         // (An)+ — NOT control
    [InlineData(7, 4, "All", true)]              // #imm — legal as a plain data source
    public void Ea_category_accepts_and_rejects_modes(int mode, int reg, string category, bool legal)
    {
        Assert.Equal(legal, CpuEmulator.Generators.M68kEaLegality.IsLegal((uint)mode, (uint)reg, category));
    }
}
