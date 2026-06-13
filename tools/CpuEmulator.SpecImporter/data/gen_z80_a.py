#!/usr/bin/env python3
"""Source A extraction generator — the Zilog Z80 CPU User Manual (UM0080).

This encodes the DOCUMENTED Z80 instruction set as tabulated in UM0080's per-instruction
descriptions (opcode bit-patterns + the M-cycle / T-state tables). It is run plane-by-plane
(base, CB, ED, DD, FD, DDCB, FDCB) and emits z80-opcodes-a.json.

cycles = T-states (total clock periods), the unambiguous scalar Zilog tabulates (Ground truth B).
Conditional rows (JR cc / CALL cc / RET cc / DJNZ / block-op repeat) record the NOT-TAKEN /
single-iteration base T-state count, with a source note (Ground truth B). pageCrossPenalty is
always false for the Z80.

Provenance: every row cites "Zilog Z80 CPU User Manual (UM0080)" + the group it appears in.
This is a faithful transcription of the documented set; behaviorally UNVERIFIED-PENDING-M3.4-TomHarte.
"""
import json

SRC = "Zilog Z80 CPU User Manual (UM0080)"

rows = []

def row(opcode, mnem, mode, nbytes, cycles, src, prefix=None, note=None):
    r = {"mnemonic": mnem, "mode": mode, "bytes": nbytes, "cycles": cycles,
         "pageCrossPenalty": False}
    if prefix is not None:
        r = {"prefix": prefix, "opcode": f"0x{opcode:02X}", **r}
    else:
        r = {"opcode": f"0x{opcode:02X}", **r}
    r["source"] = src + (f"; {note}" if note else "")
    return r

# 8-bit registers in opcode order (the 8080 r-field order). idx 6 = (HL).
R8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"]
RP_SP = ["BC", "DE", "HL", "SP"]   # bits 4-5 register pair (SP form)
RP_AF = ["BC", "DE", "HL", "AF"]   # the PUSH/POP form
CC = ["NZ", "Z", "NC", "C", "PO", "PE", "P", "M"]  # condition codes (bits 3-5)

# ───────────────────────── BASE PLANE ─────────────────────────
# Group 0x00-0x3F: the misc / 16-bit-load / inc-dec / rotate column.
base_misc = {
    0x00: ("NOP", "Implied", 1, 4),
    0x07: ("RLCA", "Implied", 1, 4),
    0x08: ("EX", "Implied", 1, 4),       # EX AF,AF'
    0x0F: ("RRCA", "Implied", 1, 4),
    0x10: ("DJNZ", "RelativeJump", 2, 8, "DJNZ d taken=13 not-taken=8; dataset records not-taken base"),
    0x17: ("RLA", "Implied", 1, 4),
    0x18: ("JR", "RelativeJump", 2, 12),  # JR d (unconditional, always taken)
    0x1F: ("RRA", "Implied", 1, 4),
    0x27: ("DAA", "Implied", 1, 4),
    0x2F: ("CPL", "Implied", 1, 4),
    0x37: ("SCF", "Implied", 1, 4),
    0x3F: ("CCF", "Implied", 1, 4),
}
# 16-bit immediate loads LD rr,nn : 0x01,0x11,0x21,0x31
for i, rp in enumerate(RP_SP):
    base_misc[0x01 + i * 0x10] = ("LD", "ImmediateExtended", 3, 10)
# ADD HL,rr : 0x09,0x19,0x29,0x39
for i, rp in enumerate(RP_SP):
    base_misc[0x09 + i * 0x10] = ("ADD", "Register", 1, 11)
# INC rr : 0x03,0x13,0x23,0x33 ; DEC rr : 0x0B,0x1B,0x2B,0x3B
for i in range(4):
    base_misc[0x03 + i * 0x10] = ("INC", "Register", 1, 6)
    base_misc[0x0B + i * 0x10] = ("DEC", "Register", 1, 6)
# INC r / DEC r (8-bit) : column 4 and 5 of each row. (HL) variants cost more.
for col, mnem in ((0x04, "INC"), (0x05, "DEC")):
    for rowi in range(8):
        opc = col + rowi * 0x08
        if opc > 0x3F:
            break
        target = R8[rowi]
        if target == "(HL)":
            base_misc[opc] = (mnem, "RegisterIndirect", 1, 11)
        else:
            base_misc[opc] = (mnem, "Register", 1, 4)
# LD r,n (8-bit immediate) : column 6 : 0x06,0x0E,...,0x3E
for rowi in range(8):
    opc = 0x06 + rowi * 0x08
    target = R8[rowi]
    if target == "(HL)":
        base_misc[opc] = ("LD", "Immediate", 2, 10)   # LD (HL),n = 10
    else:
        base_misc[opc] = ("LD", "Immediate", 2, 7)
# the indirect / extended loads in 0x02..0x3A column 2 and A
base_misc[0x02] = ("LD", "RegisterIndirect", 1, 7)   # LD (BC),A
base_misc[0x12] = ("LD", "RegisterIndirect", 1, 7)   # LD (DE),A
base_misc[0x22] = ("LD", "ExtendedAddress", 3, 16)   # LD (nn),HL
base_misc[0x32] = ("LD", "ExtendedAddress", 3, 13)   # LD (nn),A
base_misc[0x0A] = ("LD", "RegisterIndirect", 1, 7)   # LD A,(BC)
base_misc[0x1A] = ("LD", "RegisterIndirect", 1, 7)   # LD A,(DE)
base_misc[0x2A] = ("LD", "ExtendedAddress", 3, 16)   # LD HL,(nn)
base_misc[0x3A] = ("LD", "ExtendedAddress", 3, 13)   # LD A,(nn)
# conditional relative jumps JR cc,d : 0x20(NZ),0x28(Z),0x30(NC),0x38(C)
for opc in (0x20, 0x28, 0x30, 0x38):
    base_misc[opc] = ("JR", "RelativeJump", 2, 7,
                      "JR cc taken=12 not-taken=7; dataset records not-taken base")

for opc, spec in base_misc.items():
    mnem, mode, nbytes, cycles = spec[0], spec[1], spec[2], spec[3]
    note = spec[4] if len(spec) > 4 else None
    rows.append(row(opc, mnem, mode, nbytes, cycles, SRC + ", base plane", note=note))

# Group 0x40-0x7F: LD r,r' (and HALT at 0x76).
for dst in range(8):
    for srci in range(8):
        opc = 0x40 + dst * 8 + srci
        if opc == 0x76:
            rows.append(row(0x76, "HALT", "Implied", 1, 4, SRC + ", base plane"))
            continue
        d, s = R8[dst], R8[srci]
        if d == "(HL)" or s == "(HL)":
            rows.append(row(opc, "LD", "RegisterIndirect", 1, 7, SRC + ", LD r,(HL)/LD (HL),r"))
        else:
            rows.append(row(opc, "LD", "Register", 1, 4, SRC + ", LD r,r'"))

# Group 0x80-0xBF: 8-bit ALU on A. (HL) form costs 7.
ALU = ["ADD", "ADC", "SUB", "SBC", "AND", "XOR", "OR", "CP"]
for ai, mnem in enumerate(ALU):
    for srci in range(8):
        opc = 0x80 + ai * 8 + srci
        s = R8[srci]
        if s == "(HL)":
            rows.append(row(opc, mnem, "RegisterIndirect", 1, 7, SRC + ", 8-bit ALU A,(HL)"))
        else:
            rows.append(row(opc, mnem, "Register", 1, 4, SRC + ", 8-bit ALU A,r"))

# Group 0xC0-0xFF: RET cc / POP / JP cc / JP / CALL cc / PUSH / ALU n / RST / misc.
hi = {
    0xC1: ("POP", "Register", 1, 10), 0xD1: ("POP", "Register", 1, 10),
    0xE1: ("POP", "Register", 1, 10), 0xF1: ("POP", "Register", 1, 10),
    0xC5: ("PUSH", "Register", 1, 11), 0xD5: ("PUSH", "Register", 1, 11),
    0xE5: ("PUSH", "Register", 1, 11), 0xF5: ("PUSH", "Register", 1, 11),
    0xC3: ("JP", "ExtendedAddress", 3, 10),    # JP nn (always)
    0xC9: ("RET", "Implied", 1, 10),           # RET (always)
    0xCD: ("CALL", "ExtendedAddress", 3, 17),  # CALL nn (always)
    0xC6: ("ADD", "Immediate", 2, 7), 0xCE: ("ADC", "Immediate", 2, 7),
    0xD6: ("SUB", "Immediate", 2, 7), 0xDE: ("SBC", "Immediate", 2, 7),
    0xE6: ("AND", "Immediate", 2, 7), 0xEE: ("XOR", "Immediate", 2, 7),
    0xF6: ("OR", "Immediate", 2, 7),  0xFE: ("CP", "Immediate", 2, 7),
    0xD3: ("OUT", "IoPortImmediate", 2, 11),   # OUT (n),A — immediate port operand
    0xDB: ("IN", "IoPortImmediate", 2, 11),    # IN A,(n)  — immediate port operand
    0xD9: ("EXX", "Implied", 1, 4),
    0xE3: ("EX", "RegisterIndirect", 1, 19),   # EX (SP),HL
    0xE9: ("JP", "RegisterIndirect", 1, 4),    # JP (HL)
    0xEB: ("EX", "Register", 1, 4),            # EX DE,HL
    0xF3: ("DI", "Implied", 1, 4), 0xFB: ("EI", "Implied", 1, 4),
    0xF9: ("LD", "Register", 1, 6),            # LD SP,HL
}
# RET cc : 0xC0,0xC8,...,0xF8 (col 0 and 8)
for i in range(8):
    opc = 0xC0 + i * 8
    hi[opc] = ("RET", "Implied", 1, 5, "RET cc taken=11 not-taken=5; dataset records not-taken base")
# JP cc,nn : 0xC2,0xCA,...,0xFA (col 2 and A)
for i in range(8):
    opc = 0xC2 + i * 8
    hi[opc] = ("JP", "ExtendedAddress", 3, 10, "JP cc nn = 10 (taken or not)")
# CALL cc,nn : 0xC4,0xCC,...,0xFC (col 4 and C)
for i in range(8):
    opc = 0xC4 + i * 8
    hi[opc] = ("CALL", "ExtendedAddress", 3, 10,
              "CALL cc nn taken=17 not-taken=10; dataset records not-taken base")
# RST p : 0xC7,0xCF,...,0xFF (col 7 and F)
for i in range(8):
    opc = 0xC7 + i * 8
    hi[opc] = ("RST", "Implied", 1, 11)

for opc, spec in hi.items():
    mnem, mode, nbytes, cycles = spec[0], spec[1], spec[2], spec[3]
    note = spec[4] if len(spec) > 4 else None
    rows.append(row(opc, mnem, mode, nbytes, cycles, SRC + ", base plane", note=note))

# ───────────────────────── CB PLANE ─────────────────────────
# 0x00-0x3F: RLC/RRC/RL/RR/SLA/SRA/(SLL undoc — excluded)/SRL.
CB_ROT = {0x00: "RLC", 0x08: "RRC", 0x10: "RL", 0x18: "RR",
          0x20: "SLA", 0x28: "SRA", 0x38: "SRL"}  # 0x30 = SLL undocumented, excluded
for grp, mnem in CB_ROT.items():
    for srci in range(8):
        opc = grp + srci
        s = R8[srci]
        if s == "(HL)":
            rows.append(row(opc, mnem, "Bit", 2, 15, SRC + ", CB rotate/shift (HL)", prefix="0xCB"))
        else:
            rows.append(row(opc, mnem, "Bit", 2, 8, SRC + ", CB rotate/shift r", prefix="0xCB"))
# 0x40-0x7F BIT b,r ; 0x80-0xBF RES b,r ; 0xC0-0xFF SET b,r
for base, mnem, hl_cyc in ((0x40, "BIT", 12), (0x80, "RES", 15), (0xC0, "SET", 15)):
    for b in range(8):
        for srci in range(8):
            opc = base + b * 8 + srci
            s = R8[srci]
            cyc = hl_cyc if s == "(HL)" else 8
            rows.append(row(opc, mnem, "Bit", 2, cyc, SRC + f", CB {mnem} b,r", prefix="0xCB"))

# ───────────────────────── ED PLANE (documented Z80 only) ─────────────────────────
    # IN r,(C) / OUT (C),r — the port number is in register C; NO immediate operand byte, so the
    # encoding is ED + op = 2 bytes. Modeled as "Register" (register-shaped EA, base 1 + ED prefix),
    # NOT "IoPort" (which carries an immediate (n) port byte). The op semantics are TODO(vocab).
ed = {
    0x40: ("IN", "Register", 2, 12), 0x48: ("IN", "Register", 2, 12), 0x50: ("IN", "Register", 2, 12),
    0x58: ("IN", "Register", 2, 12), 0x60: ("IN", "Register", 2, 12), 0x68: ("IN", "Register", 2, 12),
    0x78: ("IN", "Register", 2, 12),  # IN r,(C) (0x70 = IN (C)/IN F,(C) undocumented, excluded)
    0x41: ("OUT", "Register", 2, 12), 0x49: ("OUT", "Register", 2, 12), 0x51: ("OUT", "Register", 2, 12),
    0x59: ("OUT", "Register", 2, 12), 0x61: ("OUT", "Register", 2, 12), 0x69: ("OUT", "Register", 2, 12),
    0x79: ("OUT", "Register", 2, 12),  # OUT (C),r (0x71 undocumented, excluded)
    0x42: ("SBC", "Register", 2, 15), 0x52: ("SBC", "Register", 2, 15),
    0x62: ("SBC", "Register", 2, 15), 0x72: ("SBC", "Register", 2, 15),  # SBC HL,rr
    0x4A: ("ADC", "Register", 2, 15), 0x5A: ("ADC", "Register", 2, 15),
    0x6A: ("ADC", "Register", 2, 15), 0x7A: ("ADC", "Register", 2, 15),  # ADC HL,rr
    0x43: ("LD", "ExtendedAddress", 4, 20), 0x53: ("LD", "ExtendedAddress", 4, 20),
    0x63: ("LD", "ExtendedAddress", 4, 20), 0x73: ("LD", "ExtendedAddress", 4, 20),  # LD (nn),rr
    0x4B: ("LD", "ExtendedAddress", 4, 20), 0x5B: ("LD", "ExtendedAddress", 4, 20),
    0x6B: ("LD", "ExtendedAddress", 4, 20), 0x7B: ("LD", "ExtendedAddress", 4, 20),  # LD rr,(nn)
    0x44: ("NEG", "Implied", 2, 8),
    0x45: ("RETN", "Implied", 2, 14), 0x4D: ("RETI", "Implied", 2, 14),
    0x46: ("IM", "Implied", 2, 8), 0x56: ("IM", "Implied", 2, 8), 0x5E: ("IM", "Implied", 2, 8),
    0x47: ("LD", "Implied", 2, 9), 0x4F: ("LD", "Implied", 2, 9),    # LD I,A / LD R,A
    0x57: ("LD", "Implied", 2, 9), 0x5F: ("LD", "Implied", 2, 9),    # LD A,I / LD A,R
    0x67: ("RRD", "RegisterIndirect", 2, 18), 0x6F: ("RLD", "RegisterIndirect", 2, 18),
}
# block ops 0xA0-0xA3, 0xA8-0xAB, 0xB0-0xB3, 0xB8-0xBB
block = {
    0xA0: ("LDI", 16), 0xA1: ("CPI", 16), 0xA2: ("INI", 16), 0xA3: ("OUTI", 16),
    0xA8: ("LDD", 16), 0xA9: ("CPD", 16), 0xAA: ("IND", 16), 0xAB: ("OUTD", 16),
    0xB0: ("LDIR", 16), 0xB1: ("CPIR", 16), 0xB2: ("INIR", 16), 0xB3: ("OTIR", 16),
    0xB8: ("LDDR", 16), 0xB9: ("CPDR", 16), 0xBA: ("INDR", 16), 0xBB: ("OTDR", 16),
}
for opc, spec in ed.items():
    mnem, mode, nbytes, cycles = spec
    rows.append(row(opc, mnem, mode, nbytes, cycles, SRC + ", ED extended plane", prefix="0xED"))
for opc, (mnem, cyc) in block.items():
    repeating = mnem.endswith("R")
    note = (f"{mnem} repeat=21 final=16; dataset records single-iteration base"
            if repeating else None)
    rows.append(row(opc, mnem, "Implied", 2, cyc, SRC + ", ED block op", prefix="0xED", note=note))

# ───────────────────────── DD / FD PLANES (documented subset) ─────────────────────────
def index_plane(prefix, reg):
    pl = []
    note_src = SRC + f", {reg} index plane"
    # ADD IX,pp : DD 09/19/29/39  (pp = BC,DE,IX,SP)
    for i in range(4):
        pl.append((0x09 + i * 0x10, "ADD", "Register", 2, 15))
    pl += [
        (0x21, "LD", "ImmediateExtended", 4, 14),   # LD IX,nn
        (0x22, "LD", "ExtendedAddress", 4, 20),      # LD (nn),IX
        (0x23, "INC", "Register", 2, 10),            # INC IX
        (0x2A, "LD", "ExtendedAddress", 4, 20),      # LD IX,(nn)
        (0x2B, "DEC", "Register", 2, 10),            # DEC IX
        (0x34, "INC", "Indexed", 3, 23),             # INC (IX+d)
        (0x35, "DEC", "Indexed", 3, 23),             # DEC (IX+d)
        (0x36, "LD", "Indexed", 4, 19),              # LD (IX+d),n
        (0xE1, "POP", "Register", 2, 14),            # POP IX
        (0xE3, "EX", "RegisterIndirect", 2, 23),     # EX (SP),IX
        (0xE5, "PUSH", "Register", 2, 15),           # PUSH IX
        (0xE9, "JP", "RegisterIndirect", 2, 8),      # JP (IX)
        (0xF9, "LD", "Register", 2, 10),             # LD SP,IX
    ]
    # LD r,(IX+d) : 0x46,0x4E,0x56,0x5E,0x66,0x6E,0x7E
    for opc in (0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E, 0x7E):
        pl.append((opc, "LD", "Indexed", 3, 19))
    # LD (IX+d),r : 0x70-0x77 except 0x76
    for opc in range(0x70, 0x78):
        if opc == 0x76:
            continue
        pl.append((opc, "LD", "Indexed", 3, 19))
    # ALU A,(IX+d) : 0x86,0x8E,0x96,0x9E,0xA6,0xAE,0xB6,0xBE
    aluops = {0x86: "ADD", 0x8E: "ADC", 0x96: "SUB", 0x9E: "SBC",
              0xA6: "AND", 0xAE: "XOR", 0xB6: "OR", 0xBE: "CP"}
    for opc, mnem in aluops.items():
        pl.append((opc, mnem, "Indexed", 3, 19))
    for opc, mnem, mode, nbytes, cyc in pl:
        rows.append(row(opc, mnem, mode, nbytes, cyc, note_src, prefix=prefix))

index_plane("0xDD", "IX")
index_plane("0xFD", "IY")

# ───────────────────────── DDCB / FDCB PLANES ─────────────────────────
def ddcb_plane(prefix):
    note_src = SRC + f", {'IX' if prefix == '0xDDCB' else 'IY'} bit/rotate plane"
    rotmap = {0x06: "RLC", 0x0E: "RRC", 0x16: "RL", 0x1E: "RR",
              0x26: "SLA", 0x2E: "SRA", 0x3E: "SRL"}  # 0x36 SLL undoc excluded
    for opc, mnem in rotmap.items():
        rows.append(row(opc, mnem, "Indexed", 4, 23, note_src, prefix=prefix))
    # BIT/RES/SET n,(IX+d) — only the (HL)-column opcode is the documented form (06+8b etc.)
    for base, mnem, cyc in ((0x46, "BIT", 20), (0x86, "RES", 23), (0xC6, "SET", 23)):
        for b in range(8):
            opc = base + b * 8
            rows.append(row(opc, mnem, "Indexed", 4, cyc, note_src, prefix=prefix))

ddcb_plane("0xDDCB")
ddcb_plane("0xFDCB")

print(json.dumps(rows, indent=2))
