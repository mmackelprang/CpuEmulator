using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 8088 TomHarte DATA-axis runner — M5.5a (the scaffold flipped to a real Step + diff). It installs the
/// case's initial bus + the full 14-register initial state, calls <see cref="M8086Cpu.Step"/> exactly once,
/// then diffs the resulting 14 registers + the changed RAM cells against the case's MERGED final state
/// (<see cref="M8088TomHarteCase.MergedFinalRegs"/>). The FLAGS compare is mask-aware (both sides ANDed with the
/// opcode's defined-flag mask before comparing) so an undefined flag bit never spuriously fails — though for the
/// MOV family this is moot (MOV sets no flags). The <c>queue</c> + <c>cycles</c> stay carried-not-asserted (the
/// data axis does not need them; the timing axis is M5.5e).
/// </summary>
internal static class M8088TomHarteRunner
{
    /// <summary>
    /// Build a fresh 20-bit little-endian bus, install <c>initial.ram</c>, construct an <see cref="M8086Cpu"/>,
    /// set the full 14-register initial state, Step once, then diff registers + RAM against the merged final.
    /// Returns null on a full data-axis pass, or a human-readable diff string on the FIRST mismatch.
    /// </summary>
    /// <param name="c">The vector case.</param>
    /// <param name="metadata">The per-opcode flags-mask table (<see cref="M8088Metadata.Empty"/> when absent).</param>
    /// <param name="opcodeHex">The file's opcode (e.g. "88") — keys the flags-mask lookup. The reg-field lookup
    /// is null here: the only MOV group opcodes are C6/C7 (reg=0) and MOV writes no flags, so the mask path is
    /// exercised but the result is moot (MOV asserts no flag changes — any reasonable mask passes).</param>
    /// <param name="regField">For an opcode-GROUP form (M5.5b ALU 0x80/0x81/0x83), the caller passes the ModR/M
    /// reg subfield (e.g. (c.Bytes[1] >> 3) &amp; 7) so the per-subgroup flags-mask is selected; null for plain
    /// opcodes and the MOV family.</param>
    public static string? RunCase(M8088TomHarteCase c, M8088Metadata metadata, string opcodeHex, int? regField = null)
    {
        // 20-bit physical address space, little-endian (the 8086/8088 default — NO BigEndian), per ADR 0005 D2.
        // Map the WHOLE 1 MB as writable RAM up front: AddressSpace silently drops a Write8 to an UNMAPPED page
        // and returns open-bus (0xFF) on a read there, so without the backing the initial-RAM install would be a
        // no-op and the instruction fetch would read garbage. (The M5.4 scaffold never Stepped, so it never
        // needed the backing; M5.5a does.)
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        var cpu = new M8086Cpu(bus);
        var ir = c.Initial.Regs;
        cpu.SetRegister("AX", ir.Ax);
        cpu.SetRegister("BX", ir.Bx);
        cpu.SetRegister("CX", ir.Cx);
        cpu.SetRegister("DX", ir.Dx);
        cpu.SetRegister("CS", ir.Cs);
        cpu.SetRegister("SS", ir.Ss);
        cpu.SetRegister("DS", ir.Ds);
        cpu.SetRegister("ES", ir.Es);
        cpu.SetRegister("SP", ir.Sp);
        cpu.SetRegister("BP", ir.Bp);
        cpu.SetRegister("SI", ir.Si);
        cpu.SetRegister("DI", ir.Di);
        cpu.SetRegister("IP", ir.Ip);
        cpu.SetRegister("FLAGS", ir.Flags);

        // (a) Step ONCE. The generated Step decodes through the segmented (CS<<4)+IP fetch, advances IP, and
        //     executes the MOV body. The queue + cycles are ignored (data axis only).
        cpu.Step();

        // (b)/(c) read back the 14 registers; compute the expected merged-final regs.
        var expected = c.MergedFinalRegs();
        ushort flagsMask = metadata.FlagsMask(opcodeHex, regField);

        // (d) compare each register. FLAGS is mask-aware (both sides ANDed with the defined-flag mask).
        string? regMismatch =
            Diff("AX", (ushort)cpu.GetRegister("AX"), expected.Ax) ??
            Diff("BX", (ushort)cpu.GetRegister("BX"), expected.Bx) ??
            Diff("CX", (ushort)cpu.GetRegister("CX"), expected.Cx) ??
            Diff("DX", (ushort)cpu.GetRegister("DX"), expected.Dx) ??
            Diff("CS", (ushort)cpu.GetRegister("CS"), expected.Cs) ??
            Diff("SS", (ushort)cpu.GetRegister("SS"), expected.Ss) ??
            Diff("DS", (ushort)cpu.GetRegister("DS"), expected.Ds) ??
            Diff("ES", (ushort)cpu.GetRegister("ES"), expected.Es) ??
            Diff("SP", (ushort)cpu.GetRegister("SP"), expected.Sp) ??
            Diff("BP", (ushort)cpu.GetRegister("BP"), expected.Bp) ??
            Diff("SI", (ushort)cpu.GetRegister("SI"), expected.Si) ??
            Diff("DI", (ushort)cpu.GetRegister("DI"), expected.Di) ??
            Diff("IP", (ushort)cpu.GetRegister("IP"), expected.Ip) ??
            Diff("FLAGS",
                M8088Metadata.ApplyFlagsMask((ushort)cpu.GetRegister("FLAGS"), flagsMask),
                M8088Metadata.ApplyFlagsMask(expected.Flags, flagsMask));
        if (regMismatch is not null)
            return $"{c.Name}: {regMismatch}";

        // (e) compare final.ram cells against the bus read-back.
        foreach (var cell in c.Final.Ram)
        {
            byte actual = bus.Read8(cell.Address & mask);
            if (actual != cell.Value)
                return $"{c.Name}: ram[0x{cell.Address & mask:X5}] expected 0x{cell.Value:X2}, got 0x{actual:X2}";
        }

        // (f) full pass.
        return null;
    }

    private static string? Diff(string name, ushort actual, ushort expected) =>
        actual == expected ? null : $"{name} expected 0x{expected:X4}, got 0x{actual:X4}";
}
