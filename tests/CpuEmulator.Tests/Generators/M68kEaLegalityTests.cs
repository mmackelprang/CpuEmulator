using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kEaLegalityTests
{
    // The EA-category check: (mode, reg, category) → legal? The 68000 buckets (M68000 PRM Appendix C):
    //  DataAddressing   = every mode EXCEPT An-direct (mode 1)
    //  All              = the SUPERSET of DataAddressing that adds An-direct back in (every legal mode)
    //  DataAlterable    = Dn, (An), (An)+, -(An), d16(An), d8(An,Xn), abs.w, abs.l         (NOT An, NOT PC, NOT #imm)
    //  Control          = (An), d16(An), d8(An,Xn), abs.w, abs.l, d16(PC), d8(PC,Xn)       (NOT Dn/An/(An)+/-(An)/#imm)
    [Theory]
    [InlineData(0, 0, "DataAlterable", true)]    // Dn — legal data-alterable
    [InlineData(1, 0, "DataAlterable", false)]   // An — NOT data-alterable
    [InlineData(7, 4, "DataAlterable", false)]   // #imm — NOT alterable
    [InlineData(7, 2, "Control", true)]          // d16(PC) — legal control
    [InlineData(3, 0, "Control", false)]         // (An)+ — NOT control
    [InlineData(7, 4, "All", true)]              // #imm — legal as a plain data source
    [InlineData(1, 0, "All", true)]              // An-direct — legal for "All" (the superset adds An back)
    [InlineData(1, 0, "DataAddressing", false)]  // An-direct — NOT a data-addressing mode (the distinction)
    [InlineData(7, 5, "All", false)]             // mode-7 reg 5+ — no such EA (illegal); rejected by every bucket
    public void Ea_category_accepts_and_rejects_modes(int mode, int reg, string category, bool legal)
    {
        Assert.Equal(legal, CpuEmulator.Generators.M68kEaLegality.IsLegal((uint)mode, (uint)reg, category));
    }
}
