namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5d — the 8086 PORT-I/O op bodies (hand-written): IN (E4/E5 imm8 port, EC/ED DX port) and OUT (E6/E7 imm8,
/// EE/EF DX). The 8086 has a SEPARATE 64 KB I/O address space (the one place it has a second bus). Dispatched by
/// the generated <c>ExecuteX86</c> to <see cref="IoExecute"/>.
///
/// <para><b>The data-axis port model (reconciled against the 8088 corpus).</b> The SingleStepTests/8088 v2
/// corpus has NO top-level <c>ports</c> field and NO peripheral attached: every IN reads OPEN-BUS — 0xFF for a
/// byte IN, 0xFFFF for a word IN (verified across the E4/E5/EC/ED files); every OUT has NO observable data-axis
/// effect (it only advances IP — the written byte/word goes to a port no register/RAM cell reflects). So on the
/// data axis IN loads the open-bus constant into AL/AX and OUT is a no-op (beyond the IP advance Step already
/// did). The port NUMBER (the imm8 or DX) is irrelevant to the data axis — there is no device to address. The
/// real port bus + the value-on-the-data-bus is the M5.5e TIMING axis (the IOR/IOW cycles trace).</para>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>The 8086 open-bus value an unconnected I/O port returns on a READ (no peripheral attached in the
    /// 8088 corpus): all ones. Byte ⇒ 0xFF, word ⇒ 0xFFFF.</summary>
    private const ushort IoOpenBus = 0xFFFF;

    /// <summary>M5.5d: execute one port-I/O instruction.</summary>
    partial void IoExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        switch (key)
        {
            // ── IN — read OPEN-BUS into the accumulator (no peripheral on the data axis). The port number (imm8
            //    for E4/E5, DX for EC/ED) does not matter — there is no device to address. ─────────────────────
            case 0xE4u:   // IN AL, imm8
            case 0xECu:   // IN AL, DX
                AL = unchecked((byte)IoOpenBus);
                break;
            case 0xE5u:   // IN AX, imm8
            case 0xEDu:   // IN AX, DX
                AX = IoOpenBus;
                break;

            // ── OUT — no observable data-axis effect (the value goes to a port; nothing reads it back). IP was
            //    already advanced by Step; nothing else to do on the data axis. ───────────────────────────────
            case 0xE6u:   // OUT imm8, AL
            case 0xE7u:   // OUT imm8, AX
            case 0xEEu:   // OUT DX, AL
            case 0xEFu:   // OUT DX, AX
                break;
        }
    }
}
