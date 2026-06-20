using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>Maps a portable <see cref="KeyCode"/> to the ZX Spectrum's 8×5 key matrix: a half-row
/// index 0..7 (selected by address lines A8..A15 — row 0 = A8 = port FEFE, row 7 = A15 = port 7FFE)
/// and a bit 0..4 within that half-row (0 = the first key in the row). On the real hardware a pressed
/// key pulls its data bit LOW, so the ULA returns 0 for pressed. Half-rows / bit 0..4:
/// 0 FEFE: CAPS,Z,X,C,V ; 1 FDFE: A,S,D,F,G ; 2 FBFE: Q,W,E,R,T ; 3 F7FE: 1,2,3,4,5 ;
/// 4 EFFE: 0,9,8,7,6 ; 5 DFFE: P,O,I,U,Y ; 6 BFFE: ENTER,L,K,J,H ; 7 7FFE: SPACE,SYMSHIFT,M,N,B.</summary>
public static class SpectrumKeyMatrix
{
    public static bool TryMap(KeyCode key, out int halfRow, out int bit)
    {
        (halfRow, bit) = key switch
        {
            // Row 0 FEFE
            KeyCode.CapsShift => (0, 0), KeyCode.Z => (0, 1), KeyCode.X => (0, 2),
            KeyCode.C => (0, 3), KeyCode.V => (0, 4),
            // Row 1 FDFE
            KeyCode.A => (1, 0), KeyCode.S => (1, 1), KeyCode.D => (1, 2),
            KeyCode.F => (1, 3), KeyCode.G => (1, 4),
            // Row 2 FBFE
            KeyCode.Q => (2, 0), KeyCode.W => (2, 1), KeyCode.E => (2, 2),
            KeyCode.R => (2, 3), KeyCode.T => (2, 4),
            // Row 3 F7FE
            KeyCode.Digit1 => (3, 0), KeyCode.Digit2 => (3, 1), KeyCode.Digit3 => (3, 2),
            KeyCode.Digit4 => (3, 3), KeyCode.Digit5 => (3, 4),
            // Row 4 EFFE
            KeyCode.Digit0 => (4, 0), KeyCode.Digit9 => (4, 1), KeyCode.Digit8 => (4, 2),
            KeyCode.Digit7 => (4, 3), KeyCode.Digit6 => (4, 4),
            // Row 5 DFFE
            KeyCode.P => (5, 0), KeyCode.O => (5, 1), KeyCode.I => (5, 2),
            KeyCode.U => (5, 3), KeyCode.Y => (5, 4),
            // Row 6 BFFE
            KeyCode.Enter => (6, 0), KeyCode.L => (6, 1), KeyCode.K => (6, 2),
            KeyCode.J => (6, 3), KeyCode.H => (6, 4),
            // Row 7 7FFE
            KeyCode.Space => (7, 0), KeyCode.SymbolShift => (7, 1), KeyCode.M => (7, 2),
            KeyCode.N => (7, 3), KeyCode.B => (7, 4),
            _ => (-1, -1),
        };
        return halfRow >= 0;
    }
}
