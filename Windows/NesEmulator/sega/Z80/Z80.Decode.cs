// Decode tables: opcode -> micro-program builder. Transcribed from BizHawk's Z80A Execute.cs (FetchInstruction; MIT, Copyright (c) BizHawk team) with the
// prefix flags replaced by one state value and the register names renamed (rA, rB, ... so they cannot clash with the public registers). The opcode-to-builder
// mapping (including the undocumented IXH/IXL/IYH/IYL forms, SLL, the DD CB / FD CB register copies and the ED duplicates) is BizHawk's, checked by the test vectors.

namespace NesEmulator.Sega;

public sealed partial class Z80<TBus>
{
    /// <summary>Decode table: unprefixed opcodes.</summary>
    private void DecodeBase()
    {
        switch (opcode)
        {
            case 0x00: NOP_(); break; // NOP
            case 0x01: LD_IND_16(rC, rB, rPCl, rPCh); break; // LD BC, nn
            case 0x02: LD_8_IND(rC, rB, rA); break; // LD (BC), A
            case 0x03: INC_16(rC, rB); break; // INC BC
            case 0x04: INT_OP(INC8, rB); break; // INC B
            case 0x05: INT_OP(DEC8, rB); break; // DEC B
            case 0x06: LD_IND_8_INC(rB, rPCl, rPCh); break; // LD B, n
            case 0x07: INT_OP(RLC, rAim); break; // RLCA
            case 0x08: EXCH_(); break; // EXCH AF, AF'
            case 0x09: ADD_16(rL, rH, rC, rB); break; // ADD HL, BC
            case 0x0A: REG_OP_IND(TR, rA, rC, rB); break; // LD A, (BC)
            case 0x0B: DEC_16(rC, rB); break; // DEC BC
            case 0x0C: INT_OP(INC8, rC); break; // INC C
            case 0x0D: INT_OP(DEC8, rC); break; // DEC C
            case 0x0E: LD_IND_8_INC(rC, rPCl, rPCh); break; // LD C, n
            case 0x0F: INT_OP(RRC, rAim); break; // RRCA
            case 0x10: DJNZ_(); break; // DJNZ B
            case 0x11: LD_IND_16(rE, rD, rPCl, rPCh); break; // LD DE, nn
            case 0x12: LD_8_IND(rE, rD, rA); break; // LD (DE), A
            case 0x13: INC_16(rE, rD); break; // INC DE
            case 0x14: INT_OP(INC8, rD); break; // INC D
            case 0x15: INT_OP(DEC8, rD); break; // DEC D
            case 0x16: LD_IND_8_INC(rD, rPCl, rPCh); break; // LD D, n
            case 0x17: INT_OP(RL, rAim); break; // RLA
            case 0x18: JR_COND(true); break; // JR, r8
            case 0x19: ADD_16(rL, rH, rE, rD); break; // ADD HL, DE
            case 0x1A: REG_OP_IND(TR, rA, rE, rD); break; // LD A, (DE)
            case 0x1B: DEC_16(rE, rD); break; // DEC DE
            case 0x1C: INT_OP(INC8, rE); break; // INC E
            case 0x1D: INT_OP(DEC8, rE); break; // DEC E
            case 0x1E: LD_IND_8_INC(rE, rPCl, rPCh); break; // LD E, n
            case 0x1F: INT_OP(RR, rAim); break; // RRA
            case 0x20: JR_COND(!FlagZ); break; // JR NZ, r8
            case 0x21: LD_IND_16(rL, rH, rPCl, rPCh); break; // LD HL, nn
            case 0x22: LD_16_IND_nn(rL, rH); break; // LD (nn), HL
            case 0x23: INC_16(rL, rH); break; // INC HL
            case 0x24: INT_OP(INC8, rH); break; // INC H
            case 0x25: INT_OP(DEC8, rH); break; // DEC H
            case 0x26: LD_IND_8_INC(rH, rPCl, rPCh); break; // LD H, n
            case 0x27: INT_OP(DA, rA); break; // DAA
            case 0x28: JR_COND(FlagZ); break; // JR Z, r8
            case 0x29: ADD_16(rL, rH, rL, rH); break; // ADD HL, HL
            case 0x2A: LD_IND_16_nn(rL, rH); break; // LD HL, (nn)
            case 0x2B: DEC_16(rL, rH); break; // DEC HL
            case 0x2C: INT_OP(INC8, rL); break; // INC L
            case 0x2D: INT_OP(DEC8, rL); break; // DEC L
            case 0x2E: LD_IND_8_INC(rL, rPCl, rPCh); break; // LD L, n
            case 0x2F: INT_OP(CPL, rA); break; // CPL
            case 0x30: JR_COND(!FlagC); break; // JR NC, r8
            case 0x31: LD_IND_16(rSPl, rSPh, rPCl, rPCh); break; // LD SP, nn
            case 0x32: LD_8_IND_nn(rA); break; // LD (nn), A
            case 0x33: INC_16(rSPl, rSPh); break; // INC SP
            case 0x34: INC_8_IND(rL, rH); break; // INC (HL)
            case 0x35: DEC_8_IND(rL, rH); break; // DEC (HL)
            case 0x36: LD_8_IND_IND(rL, rH, rPCl, rPCh); break; // LD (HL), n
            case 0x37: INT_OP(SCF, rA); break; // SCF
            case 0x38: JR_COND(FlagC); break; // JR C, r8
            case 0x39: ADD_16(rL, rH, rSPl, rSPh); break; // ADD HL, SP
            case 0x3A: LD_IND_8_nn(rA); break; // LD A, (nn)
            case 0x3B: DEC_16(rSPl, rSPh); break; // DEC SP
            case 0x3C: INT_OP(INC8, rA); break; // INC A
            case 0x3D: INT_OP(DEC8, rA); break; // DEC A
            case 0x3E: LD_IND_8_INC(rA, rPCl, rPCh); break; // LD A, n
            case 0x3F: INT_OP(CCF, rA); break; // CCF
            case 0x40: REG_OP(TR, rB, rB); break; // LD B, B
            case 0x41: REG_OP(TR, rB, rC); break; // LD B, C
            case 0x42: REG_OP(TR, rB, rD); break; // LD B, D
            case 0x43: REG_OP(TR, rB, rE); break; // LD B, E
            case 0x44: REG_OP(TR, rB, rH); break; // LD B, H
            case 0x45: REG_OP(TR, rB, rL); break; // LD B, L
            case 0x46: REG_OP_IND_HL(TR, rB); break; // LD B, (HL)
            case 0x47: REG_OP(TR, rB, rA); break; // LD B, A
            case 0x48: REG_OP(TR, rC, rB); break; // LD C, B
            case 0x49: REG_OP(TR, rC, rC); break; // LD C, C
            case 0x4A: REG_OP(TR, rC, rD); break; // LD C, D
            case 0x4B: REG_OP(TR, rC, rE); break; // LD C, E
            case 0x4C: REG_OP(TR, rC, rH); break; // LD C, H
            case 0x4D: REG_OP(TR, rC, rL); break; // LD C, L
            case 0x4E: REG_OP_IND_HL(TR, rC); break; // LD C, (HL)
            case 0x4F: REG_OP(TR, rC, rA); break; // LD C, A
            case 0x50: REG_OP(TR, rD, rB); break; // LD D, B
            case 0x51: REG_OP(TR, rD, rC); break; // LD D, C
            case 0x52: REG_OP(TR, rD, rD); break; // LD D, D
            case 0x53: REG_OP(TR, rD, rE); break; // LD D, E
            case 0x54: REG_OP(TR, rD, rH); break; // LD D, H
            case 0x55: REG_OP(TR, rD, rL); break; // LD D, L
            case 0x56: REG_OP_IND_HL(TR, rD); break; // LD D, (HL)
            case 0x57: REG_OP(TR, rD, rA); break; // LD D, A
            case 0x58: REG_OP(TR, rE, rB); break; // LD E, B
            case 0x59: REG_OP(TR, rE, rC); break; // LD E, C
            case 0x5A: REG_OP(TR, rE, rD); break; // LD E, D
            case 0x5B: REG_OP(TR, rE, rE); break; // LD E, E
            case 0x5C: REG_OP(TR, rE, rH); break; // LD E, H
            case 0x5D: REG_OP(TR, rE, rL); break; // LD E, L
            case 0x5E: REG_OP_IND_HL(TR, rE); break; // LD E, (HL)
            case 0x5F: REG_OP(TR, rE, rA); break; // LD E, A
            case 0x60: REG_OP(TR, rH, rB); break; // LD H, B
            case 0x61: REG_OP(TR, rH, rC); break; // LD H, C
            case 0x62: REG_OP(TR, rH, rD); break; // LD H, D
            case 0x63: REG_OP(TR, rH, rE); break; // LD H, E
            case 0x64: REG_OP(TR, rH, rH); break; // LD H, H
            case 0x65: REG_OP(TR, rH, rL); break; // LD H, L
            case 0x66: REG_OP_IND_HL(TR, rH); break; // LD H, (HL)
            case 0x67: REG_OP(TR, rH, rA); break; // LD H, A
            case 0x68: REG_OP(TR, rL, rB); break; // LD L, B
            case 0x69: REG_OP(TR, rL, rC); break; // LD L, C
            case 0x6A: REG_OP(TR, rL, rD); break; // LD L, D
            case 0x6B: REG_OP(TR, rL, rE); break; // LD L, E
            case 0x6C: REG_OP(TR, rL, rH); break; // LD L, H
            case 0x6D: REG_OP(TR, rL, rL); break; // LD L, L
            case 0x6E: REG_OP_IND_HL(TR, rL); break; // LD L, (HL)
            case 0x6F: REG_OP(TR, rL, rA); break; // LD L, A
            case 0x70: LD_8_IND_HL(rB); break; // LD (HL), B
            case 0x71: LD_8_IND_HL(rC); break; // LD (HL), C
            case 0x72: LD_8_IND_HL(rD); break; // LD (HL), D
            case 0x73: LD_8_IND_HL(rE); break; // LD (HL), E
            case 0x74: LD_8_IND_HL(rH); break; // LD (HL), H
            case 0x75: LD_8_IND_HL(rL); break; // LD (HL), L
            case 0x76: HALT_(); break; // HALT
            case 0x77: LD_8_IND_HL( rA); break; // LD (HL), A
            case 0x78: REG_OP(TR, rA, rB); break; // LD A, B
            case 0x79: REG_OP(TR, rA, rC); break; // LD A, C
            case 0x7A: REG_OP(TR, rA, rD); break; // LD A, D
            case 0x7B: REG_OP(TR, rA, rE); break; // LD A, E
            case 0x7C: REG_OP(TR, rA, rH); break; // LD A, H
            case 0x7D: REG_OP(TR, rA, rL); break; // LD A, L
            case 0x7E: REG_OP_IND_HL(TR, rA); break; // LD A, (HL)
            case 0x7F: REG_OP(TR, rA, rA); break; // LD A, A
            case 0x80: REG_OP(ADD8, rA, rB); break; // ADD A, B
            case 0x81: REG_OP(ADD8, rA, rC); break; // ADD A, C
            case 0x82: REG_OP(ADD8, rA, rD); break; // ADD A, D
            case 0x83: REG_OP(ADD8, rA, rE); break; // ADD A, E
            case 0x84: REG_OP(ADD8, rA, rH); break; // ADD A, H
            case 0x85: REG_OP(ADD8, rA, rL); break; // ADD A, L
            case 0x86: REG_OP_IND_HL(ADD8, rA); break; // ADD A, (HL)
            case 0x87: REG_OP(ADD8, rA, rA); break; // ADD A, A
            case 0x88: REG_OP(ADC8, rA, rB); break; // ADC A, B
            case 0x89: REG_OP(ADC8, rA, rC); break; // ADC A, C
            case 0x8A: REG_OP(ADC8, rA, rD); break; // ADC A, D
            case 0x8B: REG_OP(ADC8, rA, rE); break; // ADC A, E
            case 0x8C: REG_OP(ADC8, rA, rH); break; // ADC A, H
            case 0x8D: REG_OP(ADC8, rA, rL); break; // ADC A, L
            case 0x8E: REG_OP_IND_HL(ADC8, rA); break; // ADC A, (HL)
            case 0x8F: REG_OP(ADC8, rA, rA); break; // ADC A, A
            case 0x90: REG_OP(SUB8, rA, rB); break; // SUB A, B
            case 0x91: REG_OP(SUB8, rA, rC); break; // SUB A, C
            case 0x92: REG_OP(SUB8, rA, rD); break; // SUB A, D
            case 0x93: REG_OP(SUB8, rA, rE); break; // SUB A, E
            case 0x94: REG_OP(SUB8, rA, rH); break; // SUB A, H
            case 0x95: REG_OP(SUB8, rA, rL); break; // SUB A, L
            case 0x96: REG_OP_IND_HL(SUB8, rA); break; // SUB A, (HL)
            case 0x97: REG_OP(SUB8, rA, rA); break; // SUB A, A
            case 0x98: REG_OP(SBC8, rA, rB); break; // SBC A, B
            case 0x99: REG_OP(SBC8, rA, rC); break; // SBC A, C
            case 0x9A: REG_OP(SBC8, rA, rD); break; // SBC A, D
            case 0x9B: REG_OP(SBC8, rA, rE); break; // SBC A, E
            case 0x9C: REG_OP(SBC8, rA, rH); break; // SBC A, H
            case 0x9D: REG_OP(SBC8, rA, rL); break; // SBC A, L
            case 0x9E: REG_OP_IND_HL(SBC8, rA); break; // SBC A, (HL)
            case 0x9F: REG_OP(SBC8, rA, rA); break; // SBC A, A
            case 0xA0: REG_OP(AND8, rA, rB); break; // AND A, B
            case 0xA1: REG_OP(AND8, rA, rC); break; // AND A, C
            case 0xA2: REG_OP(AND8, rA, rD); break; // AND A, D
            case 0xA3: REG_OP(AND8, rA, rE); break; // AND A, E
            case 0xA4: REG_OP(AND8, rA, rH); break; // AND A, H
            case 0xA5: REG_OP(AND8, rA, rL); break; // AND A, L
            case 0xA6: REG_OP_IND_HL(AND8, rA); break; // AND A, (HL)
            case 0xA7: REG_OP(AND8, rA, rA); break; // AND A, A
            case 0xA8: REG_OP(XOR8, rA, rB); break; // XOR A, B
            case 0xA9: REG_OP(XOR8, rA, rC); break; // XOR A, C
            case 0xAA: REG_OP(XOR8, rA, rD); break; // XOR A, D
            case 0xAB: REG_OP(XOR8, rA, rE); break; // XOR A, E
            case 0xAC: REG_OP(XOR8, rA, rH); break; // XOR A, H
            case 0xAD: REG_OP(XOR8, rA, rL); break; // XOR A, L
            case 0xAE: REG_OP_IND_HL(XOR8, rA); break; // XOR A, (HL)
            case 0xAF: REG_OP(XOR8, rA, rA); break; // XOR A, A
            case 0xB0: REG_OP(OR8, rA, rB); break; // OR A, B
            case 0xB1: REG_OP(OR8, rA, rC); break; // OR A, C
            case 0xB2: REG_OP(OR8, rA, rD); break; // OR A, D
            case 0xB3: REG_OP(OR8, rA, rE); break; // OR A, E
            case 0xB4: REG_OP(OR8, rA, rH); break; // OR A, H
            case 0xB5: REG_OP(OR8, rA, rL); break; // OR A, L
            case 0xB6: REG_OP_IND_HL(OR8, rA); break; // OR A, (HL)
            case 0xB7: REG_OP(OR8, rA, rA); break; // OR A, A
            case 0xB8: REG_OP(CP8, rA, rB); break; // CP A, B
            case 0xB9: REG_OP(CP8, rA, rC); break; // CP A, C
            case 0xBA: REG_OP(CP8, rA, rD); break; // CP A, D
            case 0xBB: REG_OP(CP8, rA, rE); break; // CP A, E
            case 0xBC: REG_OP(CP8, rA, rH); break; // CP A, H
            case 0xBD: REG_OP(CP8, rA, rL); break; // CP A, L
            case 0xBE: REG_OP_IND_HL(CP8, rA); break; // CP A, (HL)
            case 0xBF: REG_OP(CP8, rA, rA); break; // CP A, A
            case 0xC0: RET_COND(!FlagZ); break; // Ret NZ
            case 0xC1: POP_(rC, rB); break; // POP BC
            case 0xC2: JP_COND(!FlagZ); break; // JP NZ
            case 0xC3: JP_COND(true); break; // JP
            case 0xC4: CALL_COND(!FlagZ); break; // CALL NZ
            case 0xC5: PUSH_(rC, rB); break; // PUSH BC
            case 0xC6: REG_OP_IND_INC(ADD8, rA, rPCl, rPCh); break; // ADD A, n
            case 0xC7: RST_(0); break; // RST 0
            case 0xC8: RET_COND(FlagZ); break; // RET Z
            case 0xC9: RET_(); break; // RET
            case 0xCA: JP_COND(FlagZ); break; // JP Z
            case 0xCB: PREFIX_(PRE_CB); break; // PREFIX CB
            case 0xCC: CALL_COND(FlagZ); break; // CALL Z
            case 0xCD: CALL_COND(true); break; // CALL
            case 0xCE: REG_OP_IND_INC(ADC8, rA, rPCl, rPCh); break; // ADC A, n
            case 0xCF: RST_(0x08); break; // RST 0x08
            case 0xD0: RET_COND(!FlagC); break; // Ret NC
            case 0xD1: POP_(rE, rD); break; // POP DE
            case 0xD2: JP_COND(!FlagC); break; // JP NC
            case 0xD3: OUT_(); break; // OUT A
            case 0xD4: CALL_COND(!FlagC); break; // CALL NC
            case 0xD5: PUSH_(rE, rD); break; // PUSH DE
            case 0xD6: REG_OP_IND_INC(SUB8, rA, rPCl, rPCh); break; // SUB A, n
            case 0xD7: RST_(0x10); break; // RST 0x10
            case 0xD8: RET_COND(FlagC); break; // RET C
            case 0xD9: EXX_(); break; // EXX
            case 0xDA: JP_COND(FlagC); break; // JP C
            case 0xDB: IN_(); break; // IN A
            case 0xDC: CALL_COND(FlagC); break; // CALL C
            case 0xDD: PREFIX_(PRE_DD); break; // PREFIX IX
            case 0xDE: REG_OP_IND_INC(SBC8, rA, rPCl, rPCh); break; // SBC A, n
            case 0xDF: RST_(0x18); break; // RST 0x18
            case 0xE0: RET_COND(!FlagP); break; // RET Po
            case 0xE1: POP_(rL, rH); break; // POP HL
            case 0xE2: JP_COND(!FlagP); break; // JP Po
            case 0xE3: EXCH_16_IND_(rSPl, rSPh, rL, rH); break; // ex (SP), HL
            case 0xE4: CALL_COND(!FlagP); break; // CALL Po
            case 0xE5: PUSH_(rL, rH); break; // PUSH HL
            case 0xE6: REG_OP_IND_INC(AND8, rA, rPCl, rPCh); break; // AND A, n
            case 0xE7: RST_(0x20); break; // RST 0x20
            case 0xE8: RET_COND(FlagP); break; // RET Pe
            case 0xE9: JP_16(rL, rH); break; // JP (HL)
            case 0xEA: JP_COND(FlagP); break; // JP Pe
            case 0xEB: EXCH_16_(rE,rD, rL, rH); break; // ex DE, HL
            case 0xEC: CALL_COND(FlagP); break; // CALL Pe
            case 0xED: PREFIX_(PRE_ED); break; // PREFIX EXTD
            case 0xEE: REG_OP_IND_INC(XOR8, rA, rPCl, rPCh); break; // XOR A, n
            case 0xEF: RST_(0x28); break; // RST 0x28
            case 0xF0: RET_COND(!FlagS); break; // RET p
            case 0xF1: POP_(rF, rA); break; // POP AF
            case 0xF2: JP_COND(!FlagS); break; // JP p
            case 0xF3: DI_(); break; // DI
            case 0xF4: CALL_COND(!FlagS); break; // CALL p
            case 0xF5: PUSH_(rF, rA); break; // PUSH AF
            case 0xF6: REG_OP_IND_INC(OR8, rA, rPCl, rPCh); break; // OR A, n
            case 0xF7: RST_(0x30); break; // RST 0x30
            case 0xF8: RET_COND(FlagS); break; // RET M
            case 0xF9: LD_SP_16(rL, rH); break; // LD SP, HL
            case 0xFA: JP_COND(FlagS); break; // JP M
            case 0xFB: EI_(); break; // EI
            case 0xFC: CALL_COND(FlagS); break; // CALL M
            case 0xFD: PREFIX_(PRE_FD); break; // PREFIX IY
            case 0xFE: REG_OP_IND_INC(CP8, rA, rPCl, rPCh); break; // CP A, n
            case 0xFF: RST_(0x38); break; // RST 0x38
        }
    }

    /// <summary>Decode table: CB.</summary>
    private void DecodeCB()
    {
        switch (opcode)
        {
            case 0x00: INT_OP(RLC, rB); break; // RLC B
            case 0x01: INT_OP(RLC, rC); break; // RLC C
            case 0x02: INT_OP(RLC, rD); break; // RLC D
            case 0x03: INT_OP(RLC, rE); break; // RLC E
            case 0x04: INT_OP(RLC, rH); break; // RLC H
            case 0x05: INT_OP(RLC, rL); break; // RLC L
            case 0x06: INT_OP_IND(RLC, rL, rH); break; // RLC (HL)
            case 0x07: INT_OP(RLC, rA); break; // RLC A
            case 0x08: INT_OP(RRC, rB); break; // RRC B
            case 0x09: INT_OP(RRC, rC); break; // RRC C
            case 0x0A: INT_OP(RRC, rD); break; // RRC D
            case 0x0B: INT_OP(RRC, rE); break; // RRC E
            case 0x0C: INT_OP(RRC, rH); break; // RRC H
            case 0x0D: INT_OP(RRC, rL); break; // RRC L
            case 0x0E: INT_OP_IND(RRC, rL, rH); break; // RRC (HL)
            case 0x0F: INT_OP(RRC, rA); break; // RRC A
            case 0x10: INT_OP(RL, rB); break; // RL B
            case 0x11: INT_OP(RL, rC); break; // RL C
            case 0x12: INT_OP(RL, rD); break; // RL D
            case 0x13: INT_OP(RL, rE); break; // RL E
            case 0x14: INT_OP(RL, rH); break; // RL H
            case 0x15: INT_OP(RL, rL); break; // RL L
            case 0x16: INT_OP_IND(RL, rL, rH); break; // RL (HL)
            case 0x17: INT_OP(RL, rA); break; // RL A
            case 0x18: INT_OP(RR, rB); break; // RR B
            case 0x19: INT_OP(RR, rC); break; // RR C
            case 0x1A: INT_OP(RR, rD); break; // RR D
            case 0x1B: INT_OP(RR, rE); break; // RR E
            case 0x1C: INT_OP(RR, rH); break; // RR H
            case 0x1D: INT_OP(RR, rL); break; // RR L
            case 0x1E: INT_OP_IND(RR, rL, rH); break; // RR (HL)
            case 0x1F: INT_OP(RR, rA); break; // RR A
            case 0x20: INT_OP(SLA, rB); break; // SLA B
            case 0x21: INT_OP(SLA, rC); break; // SLA C
            case 0x22: INT_OP(SLA, rD); break; // SLA D
            case 0x23: INT_OP(SLA, rE); break; // SLA E
            case 0x24: INT_OP(SLA, rH); break; // SLA H
            case 0x25: INT_OP(SLA, rL); break; // SLA L
            case 0x26: INT_OP_IND(SLA, rL, rH); break; // SLA (HL)
            case 0x27: INT_OP(SLA, rA); break; // SLA A
            case 0x28: INT_OP(SRA, rB); break; // SRA B
            case 0x29: INT_OP(SRA, rC); break; // SRA C
            case 0x2A: INT_OP(SRA, rD); break; // SRA D
            case 0x2B: INT_OP(SRA, rE); break; // SRA E
            case 0x2C: INT_OP(SRA, rH); break; // SRA H
            case 0x2D: INT_OP(SRA, rL); break; // SRA L
            case 0x2E: INT_OP_IND(SRA, rL, rH); break; // SRA (HL)
            case 0x2F: INT_OP(SRA, rA); break; // SRA A
            case 0x30: INT_OP(SLL, rB); break; // SLL B
            case 0x31: INT_OP(SLL, rC); break; // SLL C
            case 0x32: INT_OP(SLL, rD); break; // SLL D
            case 0x33: INT_OP(SLL, rE); break; // SLL E
            case 0x34: INT_OP(SLL, rH); break; // SLL H
            case 0x35: INT_OP(SLL, rL); break; // SLL L
            case 0x36: INT_OP_IND(SLL, rL, rH); break; // SLL (HL)
            case 0x37: INT_OP(SLL, rA); break; // SLL A
            case 0x38: INT_OP(SRL, rB); break; // SRL B
            case 0x39: INT_OP(SRL, rC); break; // SRL C
            case 0x3A: INT_OP(SRL, rD); break; // SRL D
            case 0x3B: INT_OP(SRL, rE); break; // SRL E
            case 0x3C: INT_OP(SRL, rH); break; // SRL H
            case 0x3D: INT_OP(SRL, rL); break; // SRL L
            case 0x3E: INT_OP_IND(SRL, rL, rH); break; // SRL (HL)
            case 0x3F: INT_OP(SRL, rA); break; // SRL A
            case 0x40: BIT_OP(BIT, 0, rB); break; // BIT 0, B
            case 0x41: BIT_OP(BIT, 0, rC); break; // BIT 0, C
            case 0x42: BIT_OP(BIT, 0, rD); break; // BIT 0, D
            case 0x43: BIT_OP(BIT, 0, rE); break; // BIT 0, E
            case 0x44: BIT_OP(BIT, 0, rH); break; // BIT 0, H
            case 0x45: BIT_OP(BIT, 0, rL); break; // BIT 0, L
            case 0x46: BIT_TE_IND(BIT, 0, rL, rH); break; // BIT 0, (HL)
            case 0x47: BIT_OP(BIT, 0, rA); break; // BIT 0, A
            case 0x48: BIT_OP(BIT, 1, rB); break; // BIT 1, B
            case 0x49: BIT_OP(BIT, 1, rC); break; // BIT 1, C
            case 0x4A: BIT_OP(BIT, 1, rD); break; // BIT 1, D
            case 0x4B: BIT_OP(BIT, 1, rE); break; // BIT 1, E
            case 0x4C: BIT_OP(BIT, 1, rH); break; // BIT 1, H
            case 0x4D: BIT_OP(BIT, 1, rL); break; // BIT 1, L
            case 0x4E: BIT_TE_IND(BIT, 1, rL, rH); break; // BIT 1, (HL)
            case 0x4F: BIT_OP(BIT, 1, rA); break; // BIT 1, A
            case 0x50: BIT_OP(BIT, 2, rB); break; // BIT 2, B
            case 0x51: BIT_OP(BIT, 2, rC); break; // BIT 2, C
            case 0x52: BIT_OP(BIT, 2, rD); break; // BIT 2, D
            case 0x53: BIT_OP(BIT, 2, rE); break; // BIT 2, E
            case 0x54: BIT_OP(BIT, 2, rH); break; // BIT 2, H
            case 0x55: BIT_OP(BIT, 2, rL); break; // BIT 2, L
            case 0x56: BIT_TE_IND(BIT, 2, rL, rH); break; // BIT 2, (HL)
            case 0x57: BIT_OP(BIT, 2, rA); break; // BIT 2, A
            case 0x58: BIT_OP(BIT, 3, rB); break; // BIT 3, B
            case 0x59: BIT_OP(BIT, 3, rC); break; // BIT 3, C
            case 0x5A: BIT_OP(BIT, 3, rD); break; // BIT 3, D
            case 0x5B: BIT_OP(BIT, 3, rE); break; // BIT 3, E
            case 0x5C: BIT_OP(BIT, 3, rH); break; // BIT 3, H
            case 0x5D: BIT_OP(BIT, 3, rL); break; // BIT 3, L
            case 0x5E: BIT_TE_IND(BIT, 3, rL, rH); break; // BIT 3, (HL)
            case 0x5F: BIT_OP(BIT, 3, rA); break; // BIT 3, A
            case 0x60: BIT_OP(BIT, 4, rB); break; // BIT 4, B
            case 0x61: BIT_OP(BIT, 4, rC); break; // BIT 4, C
            case 0x62: BIT_OP(BIT, 4, rD); break; // BIT 4, D
            case 0x63: BIT_OP(BIT, 4, rE); break; // BIT 4, E
            case 0x64: BIT_OP(BIT, 4, rH); break; // BIT 4, H
            case 0x65: BIT_OP(BIT, 4, rL); break; // BIT 4, L
            case 0x66: BIT_TE_IND(BIT, 4, rL, rH); break; // BIT 4, (HL)
            case 0x67: BIT_OP(BIT, 4, rA); break; // BIT 4, A
            case 0x68: BIT_OP(BIT, 5, rB); break; // BIT 5, B
            case 0x69: BIT_OP(BIT, 5, rC); break; // BIT 5, C
            case 0x6A: BIT_OP(BIT, 5, rD); break; // BIT 5, D
            case 0x6B: BIT_OP(BIT, 5, rE); break; // BIT 5, E
            case 0x6C: BIT_OP(BIT, 5, rH); break; // BIT 5, H
            case 0x6D: BIT_OP(BIT, 5, rL); break; // BIT 5, L
            case 0x6E: BIT_TE_IND(BIT, 5, rL, rH); break; // BIT 5, (HL)
            case 0x6F: BIT_OP(BIT, 5, rA); break; // BIT 5, A
            case 0x70: BIT_OP(BIT, 6, rB); break; // BIT 6, B
            case 0x71: BIT_OP(BIT, 6, rC); break; // BIT 6, C
            case 0x72: BIT_OP(BIT, 6, rD); break; // BIT 6, D
            case 0x73: BIT_OP(BIT, 6, rE); break; // BIT 6, E
            case 0x74: BIT_OP(BIT, 6, rH); break; // BIT 6, H
            case 0x75: BIT_OP(BIT, 6, rL); break; // BIT 6, L
            case 0x76: BIT_TE_IND(BIT, 6, rL, rH); break; // BIT 6, (HL)
            case 0x77: BIT_OP(BIT, 6, rA); break; // BIT 6, A
            case 0x78: BIT_OP(BIT, 7, rB); break; // BIT 7, B
            case 0x79: BIT_OP(BIT, 7, rC); break; // BIT 7, C
            case 0x7A: BIT_OP(BIT, 7, rD); break; // BIT 7, D
            case 0x7B: BIT_OP(BIT, 7, rE); break; // BIT 7, E
            case 0x7C: BIT_OP(BIT, 7, rH); break; // BIT 7, H
            case 0x7D: BIT_OP(BIT, 7, rL); break; // BIT 7, L
            case 0x7E: BIT_TE_IND(BIT, 7, rL, rH); break; // BIT 7, (HL)
            case 0x7F: BIT_OP(BIT, 7, rA); break; // BIT 7, A
            case 0x80: BIT_OP(RES, 0, rB); break; // RES 0, B
            case 0x81: BIT_OP(RES, 0, rC); break; // RES 0, C
            case 0x82: BIT_OP(RES, 0, rD); break; // RES 0, D
            case 0x83: BIT_OP(RES, 0, rE); break; // RES 0, E
            case 0x84: BIT_OP(RES, 0, rH); break; // RES 0, H
            case 0x85: BIT_OP(RES, 0, rL); break; // RES 0, L
            case 0x86: BIT_OP_IND(RES, 0, rL, rH); break; // RES 0, (HL)
            case 0x87: BIT_OP(RES, 0, rA); break; // RES 0, A
            case 0x88: BIT_OP(RES, 1, rB); break; // RES 1, B
            case 0x89: BIT_OP(RES, 1, rC); break; // RES 1, C
            case 0x8A: BIT_OP(RES, 1, rD); break; // RES 1, D
            case 0x8B: BIT_OP(RES, 1, rE); break; // RES 1, E
            case 0x8C: BIT_OP(RES, 1, rH); break; // RES 1, H
            case 0x8D: BIT_OP(RES, 1, rL); break; // RES 1, L
            case 0x8E: BIT_OP_IND(RES, 1, rL, rH); break; // RES 1, (HL)
            case 0x8F: BIT_OP(RES, 1, rA); break; // RES 1, A
            case 0x90: BIT_OP(RES, 2, rB); break; // RES 2, B
            case 0x91: BIT_OP(RES, 2, rC); break; // RES 2, C
            case 0x92: BIT_OP(RES, 2, rD); break; // RES 2, D
            case 0x93: BIT_OP(RES, 2, rE); break; // RES 2, E
            case 0x94: BIT_OP(RES, 2, rH); break; // RES 2, H
            case 0x95: BIT_OP(RES, 2, rL); break; // RES 2, L
            case 0x96: BIT_OP_IND(RES, 2, rL, rH); break; // RES 2, (HL)
            case 0x97: BIT_OP(RES, 2, rA); break; // RES 2, A
            case 0x98: BIT_OP(RES, 3, rB); break; // RES 3, B
            case 0x99: BIT_OP(RES, 3, rC); break; // RES 3, C
            case 0x9A: BIT_OP(RES, 3, rD); break; // RES 3, D
            case 0x9B: BIT_OP(RES, 3, rE); break; // RES 3, E
            case 0x9C: BIT_OP(RES, 3, rH); break; // RES 3, H
            case 0x9D: BIT_OP(RES, 3, rL); break; // RES 3, L
            case 0x9E: BIT_OP_IND(RES, 3, rL, rH); break; // RES 3, (HL)
            case 0x9F: BIT_OP(RES, 3, rA); break; // RES 3, A
            case 0xA0: BIT_OP(RES, 4, rB); break; // RES 4, B
            case 0xA1: BIT_OP(RES, 4, rC); break; // RES 4, C
            case 0xA2: BIT_OP(RES, 4, rD); break; // RES 4, D
            case 0xA3: BIT_OP(RES, 4, rE); break; // RES 4, E
            case 0xA4: BIT_OP(RES, 4, rH); break; // RES 4, H
            case 0xA5: BIT_OP(RES, 4, rL); break; // RES 4, L
            case 0xA6: BIT_OP_IND(RES, 4, rL, rH); break; // RES 4, (HL)
            case 0xA7: BIT_OP(RES, 4, rA); break; // RES 4, A
            case 0xA8: BIT_OP(RES, 5, rB); break; // RES 5, B
            case 0xA9: BIT_OP(RES, 5, rC); break; // RES 5, C
            case 0xAA: BIT_OP(RES, 5, rD); break; // RES 5, D
            case 0xAB: BIT_OP(RES, 5, rE); break; // RES 5, E
            case 0xAC: BIT_OP(RES, 5, rH); break; // RES 5, H
            case 0xAD: BIT_OP(RES, 5, rL); break; // RES 5, L
            case 0xAE: BIT_OP_IND(RES, 5, rL, rH); break; // RES 5, (HL)
            case 0xAF: BIT_OP(RES, 5, rA); break; // RES 5, A
            case 0xB0: BIT_OP(RES, 6, rB); break; // RES 6, B
            case 0xB1: BIT_OP(RES, 6, rC); break; // RES 6, C
            case 0xB2: BIT_OP(RES, 6, rD); break; // RES 6, D
            case 0xB3: BIT_OP(RES, 6, rE); break; // RES 6, E
            case 0xB4: BIT_OP(RES, 6, rH); break; // RES 6, H
            case 0xB5: BIT_OP(RES, 6, rL); break; // RES 6, L
            case 0xB6: BIT_OP_IND(RES, 6, rL, rH); break; // RES 6, (HL)
            case 0xB7: BIT_OP(RES, 6, rA); break; // RES 6, A
            case 0xB8: BIT_OP(RES, 7, rB); break; // RES 7, B
            case 0xB9: BIT_OP(RES, 7, rC); break; // RES 7, C
            case 0xBA: BIT_OP(RES, 7, rD); break; // RES 7, D
            case 0xBB: BIT_OP(RES, 7, rE); break; // RES 7, E
            case 0xBC: BIT_OP(RES, 7, rH); break; // RES 7, H
            case 0xBD: BIT_OP(RES, 7, rL); break; // RES 7, L
            case 0xBE: BIT_OP_IND(RES, 7, rL, rH); break; // RES 7, (HL)
            case 0xBF: BIT_OP(RES, 7, rA); break; // RES 7, A
            case 0xC0: BIT_OP(SET, 0, rB); break; // SET 0, B
            case 0xC1: BIT_OP(SET, 0, rC); break; // SET 0, C
            case 0xC2: BIT_OP(SET, 0, rD); break; // SET 0, D
            case 0xC3: BIT_OP(SET, 0, rE); break; // SET 0, E
            case 0xC4: BIT_OP(SET, 0, rH); break; // SET 0, H
            case 0xC5: BIT_OP(SET, 0, rL); break; // SET 0, L
            case 0xC6: BIT_OP_IND(SET, 0, rL, rH); break; // SET 0, (HL)
            case 0xC7: BIT_OP(SET, 0, rA); break; // SET 0, A
            case 0xC8: BIT_OP(SET, 1, rB); break; // SET 1, B
            case 0xC9: BIT_OP(SET, 1, rC); break; // SET 1, C
            case 0xCA: BIT_OP(SET, 1, rD); break; // SET 1, D
            case 0xCB: BIT_OP(SET, 1, rE); break; // SET 1, E
            case 0xCC: BIT_OP(SET, 1, rH); break; // SET 1, H
            case 0xCD: BIT_OP(SET, 1, rL); break; // SET 1, L
            case 0xCE: BIT_OP_IND(SET, 1, rL, rH); break; // SET 1, (HL)
            case 0xCF: BIT_OP(SET, 1, rA); break; // SET 1, A
            case 0xD0: BIT_OP(SET, 2, rB); break; // SET 2, B
            case 0xD1: BIT_OP(SET, 2, rC); break; // SET 2, C
            case 0xD2: BIT_OP(SET, 2, rD); break; // SET 2, D
            case 0xD3: BIT_OP(SET, 2, rE); break; // SET 2, E
            case 0xD4: BIT_OP(SET, 2, rH); break; // SET 2, H
            case 0xD5: BIT_OP(SET, 2, rL); break; // SET 2, L
            case 0xD6: BIT_OP_IND(SET, 2, rL, rH); break; // SET 2, (HL)
            case 0xD7: BIT_OP(SET, 2, rA); break; // SET 2, A
            case 0xD8: BIT_OP(SET, 3, rB); break; // SET 3, B
            case 0xD9: BIT_OP(SET, 3, rC); break; // SET 3, C
            case 0xDA: BIT_OP(SET, 3, rD); break; // SET 3, D
            case 0xDB: BIT_OP(SET, 3, rE); break; // SET 3, E
            case 0xDC: BIT_OP(SET, 3, rH); break; // SET 3, H
            case 0xDD: BIT_OP(SET, 3, rL); break; // SET 3, L
            case 0xDE: BIT_OP_IND(SET, 3, rL, rH); break; // SET 3, (HL)
            case 0xDF: BIT_OP(SET, 3, rA); break; // SET 3, A
            case 0xE0: BIT_OP(SET, 4, rB); break; // SET 4, B
            case 0xE1: BIT_OP(SET, 4, rC); break; // SET 4, C
            case 0xE2: BIT_OP(SET, 4, rD); break; // SET 4, D
            case 0xE3: BIT_OP(SET, 4, rE); break; // SET 4, E
            case 0xE4: BIT_OP(SET, 4, rH); break; // SET 4, H
            case 0xE5: BIT_OP(SET, 4, rL); break; // SET 4, L
            case 0xE6: BIT_OP_IND(SET, 4, rL, rH); break; // SET 4, (HL)
            case 0xE7: BIT_OP(SET, 4, rA); break; // SET 4, A
            case 0xE8: BIT_OP(SET, 5, rB); break; // SET 5, B
            case 0xE9: BIT_OP(SET, 5, rC); break; // SET 5, C
            case 0xEA: BIT_OP(SET, 5, rD); break; // SET 5, D
            case 0xEB: BIT_OP(SET, 5, rE); break; // SET 5, E
            case 0xEC: BIT_OP(SET, 5, rH); break; // SET 5, H
            case 0xED: BIT_OP(SET, 5, rL); break; // SET 5, L
            case 0xEE: BIT_OP_IND(SET, 5, rL, rH); break; // SET 5, (HL)
            case 0xEF: BIT_OP(SET, 5, rA); break; // SET 5, A
            case 0xF0: BIT_OP(SET, 6, rB); break; // SET 6, B
            case 0xF1: BIT_OP(SET, 6, rC); break; // SET 6, C
            case 0xF2: BIT_OP(SET, 6, rD); break; // SET 6, D
            case 0xF3: BIT_OP(SET, 6, rE); break; // SET 6, E
            case 0xF4: BIT_OP(SET, 6, rH); break; // SET 6, H
            case 0xF5: BIT_OP(SET, 6, rL); break; // SET 6, L
            case 0xF6: BIT_OP_IND(SET, 6, rL, rH); break; // SET 6, (HL)
            case 0xF7: BIT_OP(SET, 6, rA); break; // SET 6, A
            case 0xF8: BIT_OP(SET, 7, rB); break; // SET 7, B
            case 0xF9: BIT_OP(SET, 7, rC); break; // SET 7, C
            case 0xFA: BIT_OP(SET, 7, rD); break; // SET 7, D
            case 0xFB: BIT_OP(SET, 7, rE); break; // SET 7, E
            case 0xFC: BIT_OP(SET, 7, rH); break; // SET 7, H
            case 0xFD: BIT_OP(SET, 7, rL); break; // SET 7, L
            case 0xFE: BIT_OP_IND(SET, 7, rL, rH); break; // SET 7, (HL)
            case 0xFF: BIT_OP(SET, 7, rA); break; // SET 7, A
        }
    }

    /// <summary>Decode table: ED.</summary>
    private void DecodeED()
    {
        switch (opcode)
        {
            case 0x40: IN_REG_(rB, rC); break; // IN B, (C)
            case 0x41: OUT_REG_(rC, rB); break; // OUT (C), B
            case 0x42: REG_OP_16_(SBC16, rL, rH, rC, rB); break; // SBC HL, BC
            case 0x43: LD_16_IND_nn(rC, rB); break; // LD (nn), BC
            case 0x44: INT_OP(NEG, rA); break; // NEG
            case 0x45: RETN_(); break; // RETN
            case 0x46: INT_MODE_(0); break; // IM $0
            case 0x47: REG_OP_IR(TR, rI, rA); break; // LD I, A
            case 0x48: IN_REG_(rC, rC); break; // IN C, (C)
            case 0x49: OUT_REG_(rC, rC); break; // OUT (C), C
            case 0x4A: REG_OP_16_(ADC16, rL, rH, rC, rB); break; // ADC HL, BC
            case 0x4B: LD_IND_16_nn(rC, rB); break; // LD BC, (nn)
            case 0x4C: INT_OP(NEG, rA); break; // NEG
            case 0x4D: RETI_(); break; // RETI
            case 0x4E: INT_MODE_(0); break; // IM $0
            case 0x4F: REG_OP_IR(TR, rR, rA); break; // LD R, A
            case 0x50: IN_REG_(rD, rC); break; // IN D, (C)
            case 0x51: OUT_REG_(rC, rD); break; // OUT (C), D
            case 0x52: REG_OP_16_(SBC16, rL, rH, rE, rD); break; // SBC HL, DE
            case 0x53: LD_16_IND_nn(rE, rD); break; // LD (nn), DE
            case 0x54: INT_OP(NEG, rA); break; // NEG
            case 0x55: RETN_(); break; // RETN
            case 0x56: INT_MODE_(1); break; // IM $1
            case 0x57: REG_OP_IR(TR, rA, rI); break; // LD A, I
            case 0x58: IN_REG_(rE, rC); break; // IN E, (C)
            case 0x59: OUT_REG_(rC, rE); break; // OUT (C), E
            case 0x5A: REG_OP_16_(ADC16, rL, rH, rE, rD); break; // ADC HL, DE
            case 0x5B: LD_IND_16_nn(rE, rD); break; // LD DE, (nn)
            case 0x5C: INT_OP(NEG, rA); break; // NEG
            case 0x5D: RETN_(); break; // RETI
            case 0x5E: INT_MODE_(2); break; // IM $0
            case 0x5F: REG_OP_IR(TR, rA, rR); break; // LD A, R
            case 0x60: IN_REG_(rH, rC); break; // IN H, (C)
            case 0x61: OUT_REG_(rC, rH); break; // OUT (C), H
            case 0x62: REG_OP_16_(SBC16, rL, rH, rL, rH); break; // SBC HL, HL
            case 0x63: LD_16_IND_nn(rL, rH); break; // LD (nn), HL
            case 0x64: INT_OP(NEG, rA); break; // NEG
            case 0x65: RETN_(); break; // RETN
            case 0x66: INT_MODE_(0); break; // IM $0
            case 0x67: RRD_(); break; // RRD
            case 0x68: IN_REG_(rL, rC); break; // IN L, (C)
            case 0x69: OUT_REG_(rC, rL); break; // OUT (C), L
            case 0x6A: REG_OP_16_(ADC16, rL, rH, rL, rH); break; // ADC HL, HL
            case 0x6B: LD_IND_16_nn(rL, rH); break; // LD HL, (nn)
            case 0x6C: INT_OP(NEG, rA); break; // NEG
            case 0x6D: RETN_(); break; // RETI
            case 0x6E: INT_MODE_(0); break; // IM $0
            case 0x6F: RLD_(); break; // LD R, A
            case 0x70: IN_REG_(rALU, rC); break; // IN 0, (C)
            case 0x71: OUT_REG_(rC, rZERO); break; // OUT (C), 0
            case 0x72: REG_OP_16_(SBC16, rL, rH, rSPl, rSPh); break; // SBC HL, SP
            case 0x73: LD_16_IND_nn(rSPl, rSPh); break; // LD (nn), SP
            case 0x74: INT_OP(NEG, rA); break; // NEG
            case 0x75: RETN_(); break; // RETN
            case 0x76: INT_MODE_(1); break; // IM $1
            case 0x77: NOP_(); break; // NOP
            case 0x78: IN_REG_(rA, rC); break; // IN A, (C)
            case 0x79: OUT_REG_(rC, rA); break; // OUT (C), A
            case 0x7A: REG_OP_16_(ADC16, rL, rH, rSPl, rSPh); break; // ADC HL, SP
            case 0x7B: LD_IND_16_nn(rSPl, rSPh); break; // LD SP, (nn)
            case 0x7C: INT_OP(NEG, rA); break; // NEG
            case 0x7D: RETN_(); break; // RETI
            case 0x7E: INT_MODE_(2); break; // IM $2
            case 0x7F: NOP_(); break; // NOP
            case 0xA0: LD_OP_R(INC16, 0); break; // LDI
            case 0xA1: CP_OP_R(INC16, 0); break; // CPI
            case 0xA2: IN_OP_R(INC16, 0); break; // INI
            case 0xA3: OUT_OP_R(INC16, 0); break; // OUTI
            case 0xA8: LD_OP_R(DEC16, 0); break; // LDD
            case 0xA9: CP_OP_R(DEC16, 0); break; // CPD
            case 0xAA: IN_OP_R(DEC16, 0); break; // IND
            case 0xAB: OUT_OP_R(DEC16, 0); break; // OUTD
            case 0xB0: LD_OP_R(INC16, 1); break; // LDIR
            case 0xB1: CP_OP_R(INC16, 1); break; // CPIR
            case 0xB2: IN_OP_R(INC16, 1); break; // INIR
            case 0xB3: OUT_OP_R(INC16, 1); break; // OTIR
            case 0xB8: LD_OP_R(DEC16, 1); break; // LDDR
            case 0xB9: CP_OP_R(DEC16, 1); break; // CPDR
            case 0xBA: IN_OP_R(DEC16, 1); break; // INDR
            case 0xBB: OUT_OP_R(DEC16, 1); break; // OTDR
            default: NOP_(); break; // NOP
        }
    }

    /// <summary>Decode table: DD.</summary>
    private void DecodeDD()
    {
        switch (opcode)
        {
            case 0x00: NOP_(); break; // NOP
            case 0x01: LD_IND_16(rC, rB, rPCl, rPCh); break; // LD BC, nn
            case 0x02: LD_8_IND(rC, rB, rA); break; // LD (BC), A
            case 0x03: INC_16(rC, rB); break; // INC BC
            case 0x04: INT_OP(INC8, rB); break; // INC B
            case 0x05: INT_OP(DEC8, rB); break; // DEC B
            case 0x06: LD_IND_8_INC(rB, rPCl, rPCh); break; // LD B, n
            case 0x07: INT_OP(RLC, rAim); break; // RLCA
            case 0x08: EXCH_(); break; // EXCH AF, AF'
            case 0x09: ADD_16(rIxl, rIxh, rC, rB); break; // ADD Ix, BC
            case 0x0A: REG_OP_IND(TR, rA, rC, rB); break; // LD A, (BC)
            case 0x0B: DEC_16(rC, rB); break; // DEC BC
            case 0x0C: INT_OP(INC8, rC); break; // INC C
            case 0x0D: INT_OP(DEC8, rC); break; // DEC C
            case 0x0E: LD_IND_8_INC(rC, rPCl, rPCh); break; // LD C, n
            case 0x0F: INT_OP(RRC, rAim); break; // RRCA
            case 0x10: DJNZ_(); break; // DJNZ B
            case 0x11: LD_IND_16(rE, rD, rPCl, rPCh); break; // LD DE, nn
            case 0x12: LD_8_IND(rE, rD, rA); break; // LD (DE), A
            case 0x13: INC_16(rE, rD); break; // INC DE
            case 0x14: INT_OP(INC8, rD); break; // INC D
            case 0x15: INT_OP(DEC8, rD); break; // DEC D
            case 0x16: LD_IND_8_INC(rD, rPCl, rPCh); break; // LD D, n
            case 0x17: INT_OP(RL, rAim); break; // RLA
            case 0x18: JR_COND(true); break; // JR, r8
            case 0x19: ADD_16(rIxl, rIxh, rE, rD); break; // ADD Ix, DE
            case 0x1A: REG_OP_IND(TR, rA, rE, rD); break; // LD A, (DE)
            case 0x1B: DEC_16(rE, rD); break; // DEC DE
            case 0x1C: INT_OP(INC8, rE); break; // INC E
            case 0x1D: INT_OP(DEC8, rE); break; // DEC E
            case 0x1E: LD_IND_8_INC(rE, rPCl, rPCh); break; // LD E, n
            case 0x1F: INT_OP(RR, rAim); break; // RRA
            case 0x20: JR_COND(!FlagZ); break; // JR NZ, r8
            case 0x21: LD_IND_16(rIxl, rIxh, rPCl, rPCh); break; // LD Ix, nn
            case 0x22: LD_16_IND_nn(rIxl, rIxh); break; // LD (nn), Ix
            case 0x23: INC_16(rIxl, rIxh); break; // INC Ix
            case 0x24: INT_OP(INC8, rIxh); break; // INC Ixh
            case 0x25: INT_OP(DEC8, rIxh); break; // DEC Ixh
            case 0x26: LD_IND_8_INC(rIxh, rPCl, rPCh); break; // LD Ixh, n
            case 0x27: INT_OP(DA, rA); break; // DAA
            case 0x28: JR_COND(FlagZ); break; // JR Z, r8
            case 0x29: ADD_16(rIxl, rIxh, rIxl, rIxh); break; // ADD Ix, Ix
            case 0x2A: LD_IND_16_nn(rIxl, rIxh); break; // LD Ix, (nn)
            case 0x2B: DEC_16(rIxl, rIxh); break; // DEC Ix
            case 0x2C: INT_OP(INC8, rIxl); break; // INC Ixl
            case 0x2D: INT_OP(DEC8, rIxl); break; // DEC Ixl
            case 0x2E: LD_IND_8_INC(rIxl, rPCl, rPCh); break; // LD Ixl, n
            case 0x2F: INT_OP(CPL, rA); break; // CPL
            case 0x30: JR_COND(!FlagC); break; // JR NC, r8
            case 0x31: LD_IND_16(rSPl, rSPh, rPCl, rPCh); break; // LD SP, nn
            case 0x32: LD_8_IND_nn(rA); break; // LD (nn), A
            case 0x33: INC_16(rSPl, rSPh); break; // INC SP
            case 0x34: I_OP_n(INC8, rIxl, rIxh); break; // INC (Ix + n)
            case 0x35: I_OP_n(DEC8, rIxl, rIxh); break; // DEC (Ix + n)
            case 0x36: I_OP_n_n(rIxl, rIxh); break; // LD (Ix + n), n
            case 0x37: INT_OP(SCF, rA); break; // SCF
            case 0x38: JR_COND(FlagC); break; // JR C, r8
            case 0x39: ADD_16(rIxl, rIxh, rSPl, rSPh); break; // ADD Ix, SP
            case 0x3A: LD_IND_8_nn(rA); break; // LD A, (nn)
            case 0x3B: DEC_16(rSPl, rSPh); break; // DEC SP
            case 0x3C: INT_OP(INC8, rA); break; // INC A
            case 0x3D: INT_OP(DEC8, rA); break; // DEC A
            case 0x3E: LD_IND_8_INC(rA, rPCl, rPCh); break; // LD A, n
            case 0x3F: INT_OP(CCF, rA); break; // CCF
            case 0x40: REG_OP(TR, rB, rB); break; // LD B, B
            case 0x41: REG_OP(TR, rB, rC); break; // LD B, C
            case 0x42: REG_OP(TR, rB, rD); break; // LD B, D
            case 0x43: REG_OP(TR, rB, rE); break; // LD B, E
            case 0x44: REG_OP(TR, rB, rIxh); break; // LD B, Ixh
            case 0x45: REG_OP(TR, rB, rIxl); break; // LD B, Ixl
            case 0x46: I_REG_OP_IND_n(TR, rB, rIxl, rIxh); break; // LD B, (Ix + n)
            case 0x47: REG_OP(TR, rB, rA); break; // LD B, A
            case 0x48: REG_OP(TR, rC, rB); break; // LD C, B
            case 0x49: REG_OP(TR, rC, rC); break; // LD C, C
            case 0x4A: REG_OP(TR, rC, rD); break; // LD C, D
            case 0x4B: REG_OP(TR, rC, rE); break; // LD C, E
            case 0x4C: REG_OP(TR, rC, rIxh); break; // LD C, Ixh
            case 0x4D: REG_OP(TR, rC, rIxl); break; // LD C, Ixl
            case 0x4E: I_REG_OP_IND_n(TR, rC, rIxl, rIxh); break; // LD C, (Ix + n)
            case 0x4F: REG_OP(TR, rC, rA); break; // LD C, A
            case 0x50: REG_OP(TR, rD, rB); break; // LD D, B
            case 0x51: REG_OP(TR, rD, rC); break; // LD D, C
            case 0x52: REG_OP(TR, rD, rD); break; // LD D, D
            case 0x53: REG_OP(TR, rD, rE); break; // LD D, E
            case 0x54: REG_OP(TR, rD, rIxh); break; // LD D, Ixh
            case 0x55: REG_OP(TR, rD, rIxl); break; // LD D, Ixl
            case 0x56: I_REG_OP_IND_n(TR, rD, rIxl, rIxh); break; // LD D, (Ix + n)
            case 0x57: REG_OP(TR, rD, rA); break; // LD D, A
            case 0x58: REG_OP(TR, rE, rB); break; // LD E, B
            case 0x59: REG_OP(TR, rE, rC); break; // LD E, C
            case 0x5A: REG_OP(TR, rE, rD); break; // LD E, D
            case 0x5B: REG_OP(TR, rE, rE); break; // LD E, E
            case 0x5C: REG_OP(TR, rE, rIxh); break; // LD E, Ixh
            case 0x5D: REG_OP(TR, rE, rIxl); break; // LD E, Ixl
            case 0x5E: I_REG_OP_IND_n(TR, rE, rIxl, rIxh); break; // LD E, (Ix + n)
            case 0x5F: REG_OP(TR, rE, rA); break; // LD E, A
            case 0x60: REG_OP(TR, rIxh, rB); break; // LD Ixh, B
            case 0x61: REG_OP(TR, rIxh, rC); break; // LD Ixh, C
            case 0x62: REG_OP(TR, rIxh, rD); break; // LD Ixh, D
            case 0x63: REG_OP(TR, rIxh, rE); break; // LD Ixh, E
            case 0x64: REG_OP(TR, rIxh, rIxh); break; // LD Ixh, Ixh
            case 0x65: REG_OP(TR, rIxh, rIxl); break; // LD Ixh, Ixl
            case 0x66: I_REG_OP_IND_n(TR, rH, rIxl, rIxh); break; // LD H, (Ix + n)
            case 0x67: REG_OP(TR, rIxh, rA); break; // LD Ixh, A
            case 0x68: REG_OP(TR, rIxl, rB); break; // LD Ixl, B
            case 0x69: REG_OP(TR, rIxl, rC); break; // LD Ixl, C
            case 0x6A: REG_OP(TR, rIxl, rD); break; // LD Ixl, D
            case 0x6B: REG_OP(TR, rIxl, rE); break; // LD Ixl, E
            case 0x6C: REG_OP(TR, rIxl, rIxh); break; // LD Ixl, Ixh
            case 0x6D: REG_OP(TR, rIxl, rIxl); break; // LD Ixl, Ixl
            case 0x6E: I_REG_OP_IND_n(TR, rL, rIxl, rIxh); break; // LD L, (Ix + n)
            case 0x6F: REG_OP(TR, rIxl, rA); break; // LD Ixl, A
            case 0x70: I_LD_8_IND_n(rIxl, rIxh, rB); break; // LD (Ix + n), B
            case 0x71: I_LD_8_IND_n(rIxl, rIxh, rC); break; // LD (Ix + n), C
            case 0x72: I_LD_8_IND_n(rIxl, rIxh, rD); break; // LD (Ix + n), D
            case 0x73: I_LD_8_IND_n(rIxl, rIxh, rE); break; // LD (Ix + n), E
            case 0x74: I_LD_8_IND_n(rIxl, rIxh, rH); break; // LD (Ix + n), H
            case 0x75: I_LD_8_IND_n(rIxl, rIxh, rL); break; // LD (Ix + n), L
            case 0x76: HALT_(); break; // HALT
            case 0x77: I_LD_8_IND_n(rIxl, rIxh, rA); break; // LD (Ix + n), A
            case 0x78: REG_OP(TR, rA, rB); break; // LD A, B
            case 0x79: REG_OP(TR, rA, rC); break; // LD A, C
            case 0x7A: REG_OP(TR, rA, rD); break; // LD A, D
            case 0x7B: REG_OP(TR, rA, rE); break; // LD A, E
            case 0x7C: REG_OP(TR, rA, rIxh); break; // LD A, Ixh
            case 0x7D: REG_OP(TR, rA, rIxl); break; // LD A, Ixl
            case 0x7E: I_REG_OP_IND_n(TR, rA, rIxl, rIxh); break; // LD A, (Ix + n)
            case 0x7F: REG_OP(TR, rA, rA); break; // LD A, A
            case 0x80: REG_OP(ADD8, rA, rB); break; // ADD A, B
            case 0x81: REG_OP(ADD8, rA, rC); break; // ADD A, C
            case 0x82: REG_OP(ADD8, rA, rD); break; // ADD A, D
            case 0x83: REG_OP(ADD8, rA, rE); break; // ADD A, E
            case 0x84: REG_OP(ADD8, rA, rIxh); break; // ADD A, Ixh
            case 0x85: REG_OP(ADD8, rA, rIxl); break; // ADD A, Ixl
            case 0x86: I_REG_OP_IND_n(ADD8, rA, rIxl, rIxh); break; // ADD A, (Ix + n)
            case 0x87: REG_OP(ADD8, rA, rA); break; // ADD A, A
            case 0x88: REG_OP(ADC8, rA, rB); break; // ADC A, B
            case 0x89: REG_OP(ADC8, rA, rC); break; // ADC A, C
            case 0x8A: REG_OP(ADC8, rA, rD); break; // ADC A, D
            case 0x8B: REG_OP(ADC8, rA, rE); break; // ADC A, E
            case 0x8C: REG_OP(ADC8, rA, rIxh); break; // ADC A, Ixh
            case 0x8D: REG_OP(ADC8, rA, rIxl); break; // ADC A, Ixl
            case 0x8E: I_REG_OP_IND_n(ADC8, rA, rIxl, rIxh); break; // ADC A, (Ix + n)
            case 0x8F: REG_OP(ADC8, rA, rA); break; // ADC A, A
            case 0x90: REG_OP(SUB8, rA, rB); break; // SUB A, B
            case 0x91: REG_OP(SUB8, rA, rC); break; // SUB A, C
            case 0x92: REG_OP(SUB8, rA, rD); break; // SUB A, D
            case 0x93: REG_OP(SUB8, rA, rE); break; // SUB A, E
            case 0x94: REG_OP(SUB8, rA, rIxh); break; // SUB A, Ixh
            case 0x95: REG_OP(SUB8, rA, rIxl); break; // SUB A, Ixl
            case 0x96: I_REG_OP_IND_n(SUB8, rA, rIxl, rIxh); break; // SUB A, (Ix + n)
            case 0x97: REG_OP(SUB8, rA, rA); break; // SUB A, A
            case 0x98: REG_OP(SBC8, rA, rB); break; // SBC A, B
            case 0x99: REG_OP(SBC8, rA, rC); break; // SBC A, C
            case 0x9A: REG_OP(SBC8, rA, rD); break; // SBC A, D
            case 0x9B: REG_OP(SBC8, rA, rE); break; // SBC A, E
            case 0x9C: REG_OP(SBC8, rA, rIxh); break; // SBC A, Ixh
            case 0x9D: REG_OP(SBC8, rA, rIxl); break; // SBC A, Ixl
            case 0x9E: I_REG_OP_IND_n(SBC8, rA, rIxl, rIxh); break; // SBC A, (Ix + n)
            case 0x9F: REG_OP(SBC8, rA, rA); break; // SBC A, A
            case 0xA0: REG_OP(AND8, rA, rB); break; // AND A, B
            case 0xA1: REG_OP(AND8, rA, rC); break; // AND A, C
            case 0xA2: REG_OP(AND8, rA, rD); break; // AND A, D
            case 0xA3: REG_OP(AND8, rA, rE); break; // AND A, E
            case 0xA4: REG_OP(AND8, rA, rIxh); break; // AND A, Ixh
            case 0xA5: REG_OP(AND8, rA, rIxl); break; // AND A, Ixl
            case 0xA6: I_REG_OP_IND_n(AND8, rA, rIxl, rIxh); break; // AND A, (Ix + n)
            case 0xA7: REG_OP(AND8, rA, rA); break; // AND A, A
            case 0xA8: REG_OP(XOR8, rA, rB); break; // XOR A, B
            case 0xA9: REG_OP(XOR8, rA, rC); break; // XOR A, C
            case 0xAA: REG_OP(XOR8, rA, rD); break; // XOR A, D
            case 0xAB: REG_OP(XOR8, rA, rE); break; // XOR A, E
            case 0xAC: REG_OP(XOR8, rA, rIxh); break; // XOR A, Ixh
            case 0xAD: REG_OP(XOR8, rA, rIxl); break; // XOR A, Ixl
            case 0xAE: I_REG_OP_IND_n(XOR8, rA, rIxl, rIxh); break; // XOR A, (Ix + n)
            case 0xAF: REG_OP(XOR8, rA, rA); break; // XOR A, A
            case 0xB0: REG_OP(OR8, rA, rB); break; // OR A, B
            case 0xB1: REG_OP(OR8, rA, rC); break; // OR A, C
            case 0xB2: REG_OP(OR8, rA, rD); break; // OR A, D
            case 0xB3: REG_OP(OR8, rA, rE); break; // OR A, E
            case 0xB4: REG_OP(OR8, rA, rIxh); break; // OR A, Ixh
            case 0xB5: REG_OP(OR8, rA, rIxl); break; // OR A, Ixl
            case 0xB6: I_REG_OP_IND_n(OR8, rA, rIxl, rIxh); break; // OR A, (Ix + n)
            case 0xB7: REG_OP(OR8, rA, rA); break; // OR A, A
            case 0xB8: REG_OP(CP8, rA, rB); break; // CP A, B
            case 0xB9: REG_OP(CP8, rA, rC); break; // CP A, C
            case 0xBA: REG_OP(CP8, rA, rD); break; // CP A, D
            case 0xBB: REG_OP(CP8, rA, rE); break; // CP A, E
            case 0xBC: REG_OP(CP8, rA, rIxh); break; // CP A, Ixh
            case 0xBD: REG_OP(CP8, rA, rIxl); break; // CP A, Ixl
            case 0xBE: I_REG_OP_IND_n(CP8, rA, rIxl, rIxh); break; // CP A, (Ix + n)
            case 0xBF: REG_OP(CP8, rA, rA); break; // CP A, A
            case 0xC0: RET_COND(!FlagZ); break; // Ret NZ
            case 0xC1: POP_(rC, rB); break; // POP BC
            case 0xC2: JP_COND(!FlagZ); break; // JP NZ
            case 0xC3: JP_COND(true); break; // JP
            case 0xC4: CALL_COND(!FlagZ); break; // CALL NZ
            case 0xC5: PUSH_(rC, rB); break; // PUSH BC
            case 0xC6: REG_OP_IND_INC(ADD8, rA, rPCl, rPCh); break; // ADD A, n
            case 0xC7: RST_(0); break; // RST 0
            case 0xC8: RET_COND(FlagZ); break; // RET Z
            case 0xC9: RET_(); break; // RET
            case 0xCA: JP_COND(FlagZ); break; // JP Z
            case 0xCB: PREFETCH_(PRE_DDCB); break; // PREFIX IXCB
            case 0xCC: CALL_COND(FlagZ); break; // CALL Z
            case 0xCD: CALL_COND(true); break; // CALL
            case 0xCE: REG_OP_IND_INC(ADC8, rA, rPCl, rPCh); break; // ADC A, n
            case 0xCF: RST_(0x08); break; // RST 0x08
            case 0xD0: RET_COND(!FlagC); break; // Ret NC
            case 0xD1: POP_(rE, rD); break; // POP DE
            case 0xD2: JP_COND(!FlagC); break; // JP NC
            case 0xD3: OUT_(); break; // OUT A
            case 0xD4: CALL_COND(!FlagC); break; // CALL NC
            case 0xD5: PUSH_(rE, rD); break; // PUSH DE
            case 0xD6: REG_OP_IND_INC(SUB8, rA, rPCl, rPCh); break; // SUB A, n
            case 0xD7: RST_(0x10); break; // RST 0x10
            case 0xD8: RET_COND(FlagC); break; // RET C
            case 0xD9: EXX_(); break; // EXX
            case 0xDA: JP_COND(FlagC); break; // JP C
            case 0xDB: IN_(); break; // IN A
            case 0xDC: CALL_COND(FlagC); break; // CALL C
            case 0xDD: PREFIX_(PRE_DD); break; // IX Prefix
            case 0xDE: REG_OP_IND_INC(SBC8, rA, rPCl, rPCh); break; // SBC A, n
            case 0xDF: RST_(0x18); break; // RST 0x18
            case 0xE0: RET_COND(!FlagP); break; // RET Po
            case 0xE1: POP_(rIxl, rIxh); break; // POP Ix
            case 0xE2: JP_COND(!FlagP); break; // JP Po
            case 0xE3: EXCH_16_IND_(rSPl, rSPh, rIxl, rIxh); break; // ex (SP), Ix
            case 0xE4: CALL_COND(!FlagP); break; // CALL Po
            case 0xE5: PUSH_(rIxl, rIxh); break; // PUSH Ix
            case 0xE6: REG_OP_IND_INC(AND8, rA, rPCl, rPCh); break; // AND A, n
            case 0xE7: RST_(0x20); break; // RST 0x20
            case 0xE8: RET_COND(FlagP); break; // RET Pe
            case 0xE9: JP_16(rIxl, rIxh); break; // JP (Ix)
            case 0xEA: JP_COND(FlagP); break; // JP Pe
            case 0xEB: EXCH_16_(rE, rD, rL, rH); break; // ex DE, HL
            case 0xEC: CALL_COND(FlagP); break; // CALL Pe
            case 0xED: PREFIX_(PRE_ED); break; // EXTD Prefix
            case 0xEE: REG_OP_IND_INC(XOR8, rA, rPCl, rPCh); break; // XOR A, n
            case 0xEF: RST_(0x28); break; // RST 0x28
            case 0xF0: RET_COND(!FlagS); break; // RET p
            case 0xF1: POP_(rF, rA); break; // POP AF
            case 0xF2: JP_COND(!FlagS); break; // JP p
            case 0xF3: DI_(); break; // DI
            case 0xF4: CALL_COND(!FlagS); break; // CALL p
            case 0xF5: PUSH_(rF, rA); break; // PUSH AF
            case 0xF6: REG_OP_IND_INC(OR8, rA, rPCl, rPCh); break; // OR A, n
            case 0xF7: RST_(0x30); break; // RST 0x30
            case 0xF8: RET_COND(FlagS); break; // RET M
            case 0xF9: LD_SP_16(rIxl, rIxh); break; // LD SP, Ix
            case 0xFA: JP_COND(FlagS); break; // JP M
            case 0xFB: EI_(); break; // EI
            case 0xFC: CALL_COND(FlagS); break; // CALL M
            case 0xFD: PREFIX_(PRE_FD); break; // IY Prefix
            case 0xFE: REG_OP_IND_INC(CP8, rA, rPCl, rPCh); break; // CP A, n
            case 0xFF: RST_(0x38); break; // RST $38
        }
    }

    /// <summary>Decode table: FD.</summary>
    private void DecodeFD()
    {
        switch (opcode)
        {
            case 0x00: NOP_(); break; // NOP
            case 0x01: LD_IND_16(rC, rB, rPCl, rPCh); break; // LD BC, nn
            case 0x02: LD_8_IND(rC, rB, rA); break; // LD (BC), A
            case 0x03: INC_16(rC, rB); break; // INC BC
            case 0x04: INT_OP(INC8, rB); break; // INC B
            case 0x05: INT_OP(DEC8, rB); break; // DEC B
            case 0x06: LD_IND_8_INC(rB, rPCl, rPCh); break; // LD B, n
            case 0x07: INT_OP(RLC, rAim); break; // RLCA
            case 0x08: EXCH_(); break; // EXCH AF, AF'
            case 0x09: ADD_16(rIyl, rIyh, rC, rB); break; // ADD Iy, BC
            case 0x0A: REG_OP_IND(TR, rA, rC, rB); break; // LD A, (BC)
            case 0x0B: DEC_16(rC, rB); break; // DEC BC
            case 0x0C: INT_OP(INC8, rC); break; // INC C
            case 0x0D: INT_OP(DEC8, rC); break; // DEC C
            case 0x0E: LD_IND_8_INC(rC, rPCl, rPCh); break; // LD C, n
            case 0x0F: INT_OP(RRC, rAim); break; // RRCA
            case 0x10: DJNZ_(); break; // DJNZ B
            case 0x11: LD_IND_16(rE, rD, rPCl, rPCh); break; // LD DE, nn
            case 0x12: LD_8_IND(rE, rD, rA); break; // LD (DE), A
            case 0x13: INC_16(rE, rD); break; // INC DE
            case 0x14: INT_OP(INC8, rD); break; // INC D
            case 0x15: INT_OP(DEC8, rD); break; // DEC D
            case 0x16: LD_IND_8_INC(rD, rPCl, rPCh); break; // LD D, n
            case 0x17: INT_OP(RL, rAim); break; // RLA
            case 0x18: JR_COND(true); break; // JR, r8
            case 0x19: ADD_16(rIyl, rIyh, rE, rD); break; // ADD Iy, DE
            case 0x1A: REG_OP_IND(TR, rA, rE, rD); break; // LD A, (DE)
            case 0x1B: DEC_16(rE, rD); break; // DEC DE
            case 0x1C: INT_OP(INC8, rE); break; // INC E
            case 0x1D: INT_OP(DEC8, rE); break; // DEC E
            case 0x1E: LD_IND_8_INC(rE, rPCl, rPCh); break; // LD E, n
            case 0x1F: INT_OP(RR, rAim); break; // RRA
            case 0x20: JR_COND(!FlagZ); break; // JR NZ, r8
            case 0x21: LD_IND_16(rIyl, rIyh, rPCl, rPCh); break; // LD Iy, nn
            case 0x22: LD_16_IND_nn(rIyl, rIyh); break; // LD (nn), Iy
            case 0x23: INC_16(rIyl, rIyh); break; // INC Iy
            case 0x24: INT_OP(INC8, rIyh); break; // INC Iyh
            case 0x25: INT_OP(DEC8, rIyh); break; // DEC Iyh
            case 0x26: LD_IND_8_INC(rIyh, rPCl, rPCh); break; // LD Iyh, n
            case 0x27: INT_OP(DA, rA); break; // DAA
            case 0x28: JR_COND(FlagZ); break; // JR Z, r8
            case 0x29: ADD_16(rIyl, rIyh, rIyl, rIyh); break; // ADD Iy, Iy
            case 0x2A: LD_IND_16_nn(rIyl, rIyh); break; // LD Iy, (nn)
            case 0x2B: DEC_16(rIyl, rIyh); break; // DEC Iy
            case 0x2C: INT_OP(INC8, rIyl); break; // INC Iyl
            case 0x2D: INT_OP(DEC8, rIyl); break; // DEC Iyl
            case 0x2E: LD_IND_8_INC(rIyl, rPCl, rPCh); break; // LD Iyl, n
            case 0x2F: INT_OP(CPL, rA); break; // CPL
            case 0x30: JR_COND(!FlagC); break; // JR NC, r8
            case 0x31: LD_IND_16(rSPl, rSPh, rPCl, rPCh); break; // LD SP, nn
            case 0x32: LD_8_IND_nn(rA); break; // LD (nn), A
            case 0x33: INC_16(rSPl, rSPh); break; // INC SP
            case 0x34: I_OP_n(INC8, rIyl, rIyh); break; // INC (Iy + n)
            case 0x35: I_OP_n(DEC8, rIyl, rIyh); break; // DEC (Iy + n)
            case 0x36: I_OP_n_n(rIyl, rIyh); break; // LD (Iy + n), n
            case 0x37: INT_OP(SCF, rA); break; // SCF
            case 0x38: JR_COND(FlagC); break; // JR C, r8
            case 0x39: ADD_16(rIyl, rIyh, rSPl, rSPh); break; // ADD Iy, SP
            case 0x3A: LD_IND_8_nn(rA); break; // LD A, (nn)
            case 0x3B: DEC_16(rSPl, rSPh); break; // DEC SP
            case 0x3C: INT_OP(INC8, rA); break; // INC A
            case 0x3D: INT_OP(DEC8, rA); break; // DEC A
            case 0x3E: LD_IND_8_INC(rA, rPCl, rPCh); break; // LD A, n
            case 0x3F: INT_OP(CCF, rA); break; // CCF
            case 0x40: REG_OP(TR, rB, rB); break; // LD B, B
            case 0x41: REG_OP(TR, rB, rC); break; // LD B, C
            case 0x42: REG_OP(TR, rB, rD); break; // LD B, D
            case 0x43: REG_OP(TR, rB, rE); break; // LD B, E
            case 0x44: REG_OP(TR, rB, rIyh); break; // LD B, Iyh
            case 0x45: REG_OP(TR, rB, rIyl); break; // LD B, Iyl
            case 0x46: I_REG_OP_IND_n(TR, rB, rIyl, rIyh); break; // LD B, (Iy + n)
            case 0x47: REG_OP(TR, rB, rA); break; // LD B, A
            case 0x48: REG_OP(TR, rC, rB); break; // LD C, B
            case 0x49: REG_OP(TR, rC, rC); break; // LD C, C
            case 0x4A: REG_OP(TR, rC, rD); break; // LD C, D
            case 0x4B: REG_OP(TR, rC, rE); break; // LD C, E
            case 0x4C: REG_OP(TR, rC, rIyh); break; // LD C, Iyh
            case 0x4D: REG_OP(TR, rC, rIyl); break; // LD C, Iyl
            case 0x4E: I_REG_OP_IND_n(TR, rC, rIyl, rIyh); break; // LD C, (Iy + n)
            case 0x4F: REG_OP(TR, rC, rA); break; // LD C, A
            case 0x50: REG_OP(TR, rD, rB); break; // LD D, B
            case 0x51: REG_OP(TR, rD, rC); break; // LD D, C
            case 0x52: REG_OP(TR, rD, rD); break; // LD D, D
            case 0x53: REG_OP(TR, rD, rE); break; // LD D, E
            case 0x54: REG_OP(TR, rD, rIyh); break; // LD D, Iyh
            case 0x55: REG_OP(TR, rD, rIyl); break; // LD D, Iyl
            case 0x56: I_REG_OP_IND_n(TR, rD, rIyl, rIyh); break; // LD D, (Iy + n)
            case 0x57: REG_OP(TR, rD, rA); break; // LD D, A
            case 0x58: REG_OP(TR, rE, rB); break; // LD E, B
            case 0x59: REG_OP(TR, rE, rC); break; // LD E, C
            case 0x5A: REG_OP(TR, rE, rD); break; // LD E, D
            case 0x5B: REG_OP(TR, rE, rE); break; // LD E, E
            case 0x5C: REG_OP(TR, rE, rIyh); break; // LD E, Iyh
            case 0x5D: REG_OP(TR, rE, rIyl); break; // LD E, Iyl
            case 0x5E: I_REG_OP_IND_n(TR, rE, rIyl, rIyh); break; // LD E, (Iy + n)
            case 0x5F: REG_OP(TR, rE, rA); break; // LD E, A
            case 0x60: REG_OP(TR, rIyh, rB); break; // LD Iyh, B
            case 0x61: REG_OP(TR, rIyh, rC); break; // LD Iyh, C
            case 0x62: REG_OP(TR, rIyh, rD); break; // LD Iyh, D
            case 0x63: REG_OP(TR, rIyh, rE); break; // LD Iyh, E
            case 0x64: REG_OP(TR, rIyh, rIyh); break; // LD Iyh, Iyh
            case 0x65: REG_OP(TR, rIyh, rIyl); break; // LD Iyh, Iyl
            case 0x66: I_REG_OP_IND_n(TR, rH, rIyl, rIyh); break; // LD H, (Iy + n)
            case 0x67: REG_OP(TR, rIyh, rA); break; // LD Iyh, A
            case 0x68: REG_OP(TR, rIyl, rB); break; // LD Iyl, B
            case 0x69: REG_OP(TR, rIyl, rC); break; // LD Iyl, C
            case 0x6A: REG_OP(TR, rIyl, rD); break; // LD Iyl, D
            case 0x6B: REG_OP(TR, rIyl, rE); break; // LD Iyl, E
            case 0x6C: REG_OP(TR, rIyl, rIyh); break; // LD Iyl, Iyh
            case 0x6D: REG_OP(TR, rIyl, rIyl); break; // LD Iyl, Iyl
            case 0x6E: I_REG_OP_IND_n(TR, rL, rIyl, rIyh); break; // LD L, (Iy + n)
            case 0x6F: REG_OP(TR, rIyl, rA); break; // LD Iyl, A
            case 0x70: I_LD_8_IND_n(rIyl, rIyh, rB); break; // LD (Iy + n), B
            case 0x71: I_LD_8_IND_n(rIyl, rIyh, rC); break; // LD (Iy + n), C
            case 0x72: I_LD_8_IND_n(rIyl, rIyh, rD); break; // LD (Iy + n), D
            case 0x73: I_LD_8_IND_n(rIyl, rIyh, rE); break; // LD (Iy + n), E
            case 0x74: I_LD_8_IND_n(rIyl, rIyh, rH); break; // LD (Iy + n), H
            case 0x75: I_LD_8_IND_n(rIyl, rIyh, rL); break; // LD (Iy + n), L
            case 0x76: HALT_(); break; // HALT
            case 0x77: I_LD_8_IND_n(rIyl, rIyh, rA); break; // LD (Iy + n), A
            case 0x78: REG_OP(TR, rA, rB); break; // LD A, B
            case 0x79: REG_OP(TR, rA, rC); break; // LD A, C
            case 0x7A: REG_OP(TR, rA, rD); break; // LD A, D
            case 0x7B: REG_OP(TR, rA, rE); break; // LD A, E
            case 0x7C: REG_OP(TR, rA, rIyh); break; // LD A, Iyh
            case 0x7D: REG_OP(TR, rA, rIyl); break; // LD A, Iyl
            case 0x7E: I_REG_OP_IND_n(TR, rA, rIyl, rIyh); break; // LD A, (Iy + n)
            case 0x7F: REG_OP(TR, rA, rA); break; // LD A, A
            case 0x80: REG_OP(ADD8, rA, rB); break; // ADD A, B
            case 0x81: REG_OP(ADD8, rA, rC); break; // ADD A, C
            case 0x82: REG_OP(ADD8, rA, rD); break; // ADD A, D
            case 0x83: REG_OP(ADD8, rA, rE); break; // ADD A, E
            case 0x84: REG_OP(ADD8, rA, rIyh); break; // ADD A, Iyh
            case 0x85: REG_OP(ADD8, rA, rIyl); break; // ADD A, Iyl
            case 0x86: I_REG_OP_IND_n(ADD8, rA, rIyl, rIyh); break; // ADD A, (Iy + n)
            case 0x87: REG_OP(ADD8, rA, rA); break; // ADD A, A
            case 0x88: REG_OP(ADC8, rA, rB); break; // ADC A, B
            case 0x89: REG_OP(ADC8, rA, rC); break; // ADC A, C
            case 0x8A: REG_OP(ADC8, rA, rD); break; // ADC A, D
            case 0x8B: REG_OP(ADC8, rA, rE); break; // ADC A, E
            case 0x8C: REG_OP(ADC8, rA, rIyh); break; // ADC A, Iyh
            case 0x8D: REG_OP(ADC8, rA, rIyl); break; // ADC A, Iyl
            case 0x8E: I_REG_OP_IND_n(ADC8, rA, rIyl, rIyh); break; // ADC A, (Iy + n)
            case 0x8F: REG_OP(ADC8, rA, rA); break; // ADC A, A
            case 0x90: REG_OP(SUB8, rA, rB); break; // SUB A, B
            case 0x91: REG_OP(SUB8, rA, rC); break; // SUB A, C
            case 0x92: REG_OP(SUB8, rA, rD); break; // SUB A, D
            case 0x93: REG_OP(SUB8, rA, rE); break; // SUB A, E
            case 0x94: REG_OP(SUB8, rA, rIyh); break; // SUB A, Iyh
            case 0x95: REG_OP(SUB8, rA, rIyl); break; // SUB A, Iyl
            case 0x96: I_REG_OP_IND_n(SUB8, rA, rIyl, rIyh); break; // SUB A, (Iy + n)
            case 0x97: REG_OP(SUB8, rA, rA); break; // SUB A, A
            case 0x98: REG_OP(SBC8, rA, rB); break; // SBC A, B
            case 0x99: REG_OP(SBC8, rA, rC); break; // SBC A, C
            case 0x9A: REG_OP(SBC8, rA, rD); break; // SBC A, D
            case 0x9B: REG_OP(SBC8, rA, rE); break; // SBC A, E
            case 0x9C: REG_OP(SBC8, rA, rIyh); break; // SBC A, Iyh
            case 0x9D: REG_OP(SBC8, rA, rIyl); break; // SBC A, Iyl
            case 0x9E: I_REG_OP_IND_n(SBC8, rA, rIyl, rIyh); break; // SBC A, (Iy + n)
            case 0x9F: REG_OP(SBC8, rA, rA); break; // SBC A, A
            case 0xA0: REG_OP(AND8, rA, rB); break; // AND A, B
            case 0xA1: REG_OP(AND8, rA, rC); break; // AND A, C
            case 0xA2: REG_OP(AND8, rA, rD); break; // AND A, D
            case 0xA3: REG_OP(AND8, rA, rE); break; // AND A, E
            case 0xA4: REG_OP(AND8, rA, rIyh); break; // AND A, Iyh
            case 0xA5: REG_OP(AND8, rA, rIyl); break; // AND A, Iyl
            case 0xA6: I_REG_OP_IND_n(AND8, rA, rIyl, rIyh); break; // AND A, (Iy + n)
            case 0xA7: REG_OP(AND8, rA, rA); break; // AND A, A
            case 0xA8: REG_OP(XOR8, rA, rB); break; // XOR A, B
            case 0xA9: REG_OP(XOR8, rA, rC); break; // XOR A, C
            case 0xAA: REG_OP(XOR8, rA, rD); break; // XOR A, D
            case 0xAB: REG_OP(XOR8, rA, rE); break; // XOR A, E
            case 0xAC: REG_OP(XOR8, rA, rIyh); break; // XOR A, Iyh
            case 0xAD: REG_OP(XOR8, rA, rIyl); break; // XOR A, Iyl
            case 0xAE: I_REG_OP_IND_n(XOR8, rA, rIyl, rIyh); break; // XOR A, (Iy + n)
            case 0xAF: REG_OP(XOR8, rA, rA); break; // XOR A, A
            case 0xB0: REG_OP(OR8, rA, rB); break; // OR A, B
            case 0xB1: REG_OP(OR8, rA, rC); break; // OR A, C
            case 0xB2: REG_OP(OR8, rA, rD); break; // OR A, D
            case 0xB3: REG_OP(OR8, rA, rE); break; // OR A, E
            case 0xB4: REG_OP(OR8, rA, rIyh); break; // OR A, Iyh
            case 0xB5: REG_OP(OR8, rA, rIyl); break; // OR A, Iyl
            case 0xB6: I_REG_OP_IND_n(OR8, rA, rIyl, rIyh); break; // OR A, (Iy + n)
            case 0xB7: REG_OP(OR8, rA, rA); break; // OR A, A
            case 0xB8: REG_OP(CP8, rA, rB); break; // CP A, B
            case 0xB9: REG_OP(CP8, rA, rC); break; // CP A, C
            case 0xBA: REG_OP(CP8, rA, rD); break; // CP A, D
            case 0xBB: REG_OP(CP8, rA, rE); break; // CP A, E
            case 0xBC: REG_OP(CP8, rA, rIyh); break; // CP A, Iyh
            case 0xBD: REG_OP(CP8, rA, rIyl); break; // CP A, Iyl
            case 0xBE: I_REG_OP_IND_n(CP8, rA, rIyl, rIyh); break; // CP A, (Iy + n)
            case 0xBF: REG_OP(CP8, rA, rA); break; // CP A, A
            case 0xC0: RET_COND(!FlagZ); break; // Ret NZ
            case 0xC1: POP_(rC, rB); break; // POP BC
            case 0xC2: JP_COND(!FlagZ); break; // JP NZ
            case 0xC3: JP_COND(true); break; // JP
            case 0xC4: CALL_COND(!FlagZ); break; // CALL NZ
            case 0xC5: PUSH_(rC, rB); break; // PUSH BC
            case 0xC6: REG_OP_IND_INC(ADD8, rA, rPCl, rPCh); break; // ADD A, n
            case 0xC7: RST_(0); break; // RST 0
            case 0xC8: RET_COND(FlagZ); break; // RET Z
            case 0xC9: RET_(); break; // RET
            case 0xCA: JP_COND(FlagZ); break; // JP Z
            case 0xCB: PREFETCH_(PRE_FDCB); break; // PREFIX IyCB
            case 0xCC: CALL_COND(FlagZ); break; // CALL Z
            case 0xCD: CALL_COND(true); break; // CALL
            case 0xCE: REG_OP_IND_INC(ADC8, rA, rPCl, rPCh); break; // ADC A, n
            case 0xCF: RST_(0x08); break; // RST 0x08
            case 0xD0: RET_COND(!FlagC); break; // Ret NC
            case 0xD1: POP_(rE, rD); break; // POP DE
            case 0xD2: JP_COND(!FlagC); break; // JP NC
            case 0xD3: OUT_(); break; // OUT A
            case 0xD4: CALL_COND(!FlagC); break; // CALL NC
            case 0xD5: PUSH_(rE, rD); break; // PUSH DE
            case 0xD6: REG_OP_IND_INC(SUB8, rA, rPCl, rPCh); break; // SUB A, n
            case 0xD7: RST_(0x10); break; // RST 0x10
            case 0xD8: RET_COND(FlagC); break; // RET C
            case 0xD9: EXX_(); break; // EXX
            case 0xDA: JP_COND(FlagC); break; // JP C
            case 0xDB: IN_(); break; // IN A
            case 0xDC: CALL_COND(FlagC); break; // CALL C
            case 0xDD: PREFIX_(PRE_DD); break; // IX Prefix
            case 0xDE: REG_OP_IND_INC(SBC8, rA, rPCl, rPCh); break; // SBC A, n
            case 0xDF: RST_(0x18); break; // RST 0x18
            case 0xE0: RET_COND(!FlagP); break; // RET Po
            case 0xE1: POP_(rIyl, rIyh); break; // POP Iy
            case 0xE2: JP_COND(!FlagP); break; // JP Po
            case 0xE3: EXCH_16_IND_(rSPl, rSPh, rIyl, rIyh); break; // ex (SP), Iy
            case 0xE4: CALL_COND(!FlagP); break; // CALL Po
            case 0xE5: PUSH_(rIyl, rIyh); break; // PUSH Iy
            case 0xE6: REG_OP_IND_INC(AND8, rA, rPCl, rPCh); break; // AND A, n
            case 0xE7: RST_(0x20); break; // RST 0x20
            case 0xE8: RET_COND(FlagP); break; // RET Pe
            case 0xE9: JP_16(rIyl, rIyh); break; // JP (Iy)
            case 0xEA: JP_COND(FlagP); break; // JP Pe
            case 0xEB: EXCH_16_(rE, rD, rL, rH); break; // ex DE, HL
            case 0xEC: CALL_COND(FlagP); break; // CALL Pe
            case 0xED: PREFIX_(PRE_ED); break; // EXTD Prefix
            case 0xEE: REG_OP_IND_INC(XOR8, rA, rPCl, rPCh); break; // XOR A, n
            case 0xEF: RST_(0x28); break; // RST 0x28
            case 0xF0: RET_COND(!FlagS); break; // RET p
            case 0xF1: POP_(rF, rA); break; // POP AF
            case 0xF2: JP_COND(!FlagS); break; // JP p
            case 0xF3: DI_(); break; // DI
            case 0xF4: CALL_COND(!FlagS); break; // CALL p
            case 0xF5: PUSH_(rF, rA); break; // PUSH AF
            case 0xF6: REG_OP_IND_INC(OR8, rA, rPCl, rPCh); break; // OR A, n
            case 0xF7: RST_(0x30); break; // RST 0x30
            case 0xF8: RET_COND(FlagS); break; // RET M
            case 0xF9: LD_SP_16(rIyl, rIyh); break; // LD SP, Iy
            case 0xFA: JP_COND(FlagS); break; // JP M
            case 0xFB: EI_(); break; // EI
            case 0xFC: CALL_COND(FlagS); break; // CALL M
            case 0xFD: PREFIX_(PRE_FD); break; // IY Prefix
            case 0xFE: REG_OP_IND_INC(CP8, rA, rPCl, rPCh); break; // CP A, n
            case 0xFF: RST_(0x38); break; // RST $38
        }
    }

    /// <summary>Decode table: IndexedCB.</summary>
    private void DecodeIndexedCB()
    {
        switch (opcode)
        {
            case 0x00: I_INT_OP(RLC, rB); break; // RLC (I* + n) -> B
            case 0x01: I_INT_OP(RLC, rC); break; // RLC (I* + n) -> C
            case 0x02: I_INT_OP(RLC, rD); break; // RLC (I* + n) -> D
            case 0x03: I_INT_OP(RLC, rE); break; // RLC (I* + n) -> E
            case 0x04: I_INT_OP(RLC, rH); break; // RLC (I* + n) -> H
            case 0x05: I_INT_OP(RLC, rL); break; // RLC (I* + n) -> L
            case 0x06: I_INT_OP(RLC, rALU); break; // RLC (I* + n)
            case 0x07: I_INT_OP(RLC, rA); break; // RLC (I* + n) -> A
            case 0x08: I_INT_OP(RRC, rB); break; // RRC (I* + n) -> B
            case 0x09: I_INT_OP(RRC, rC); break; // RRC (I* + n) -> C
            case 0x0A: I_INT_OP(RRC, rD); break; // RRC (I* + n) -> D
            case 0x0B: I_INT_OP(RRC, rE); break; // RRC (I* + n) -> E
            case 0x0C: I_INT_OP(RRC, rH); break; // RRC (I* + n) -> H
            case 0x0D: I_INT_OP(RRC, rL); break; // RRC (I* + n) -> L
            case 0x0E: I_INT_OP(RRC, rALU); break; // RRC (I* + n)
            case 0x0F: I_INT_OP(RRC, rA); break; // RRC (I* + n) -> A
            case 0x10: I_INT_OP(RL, rB); break; // RL (I* + n) -> B
            case 0x11: I_INT_OP(RL, rC); break; // RL (I* + n) -> C
            case 0x12: I_INT_OP(RL, rD); break; // RL (I* + n) -> D
            case 0x13: I_INT_OP(RL, rE); break; // RL (I* + n) -> E
            case 0x14: I_INT_OP(RL, rH); break; // RL (I* + n) -> H
            case 0x15: I_INT_OP(RL, rL); break; // RL (I* + n) -> L
            case 0x16: I_INT_OP(RL, rALU); break; // RL (I* + n)
            case 0x17: I_INT_OP(RL, rA); break; // RL (I* + n) -> A
            case 0x18: I_INT_OP(RR, rB); break; // RR (I* + n) -> B
            case 0x19: I_INT_OP(RR, rC); break; // RR (I* + n) -> C
            case 0x1A: I_INT_OP(RR, rD); break; // RR (I* + n) -> D
            case 0x1B: I_INT_OP(RR, rE); break; // RR (I* + n) -> E
            case 0x1C: I_INT_OP(RR, rH); break; // RR (I* + n) -> H
            case 0x1D: I_INT_OP(RR, rL); break; // RR (I* + n) -> L
            case 0x1E: I_INT_OP(RR, rALU); break; // RR (I* + n)
            case 0x1F: I_INT_OP(RR, rA); break; // RR (I* + n) -> A
            case 0x20: I_INT_OP(SLA, rB); break; // SLA (I* + n) -> B
            case 0x21: I_INT_OP(SLA, rC); break; // SLA (I* + n) -> C
            case 0x22: I_INT_OP(SLA, rD); break; // SLA (I* + n) -> D
            case 0x23: I_INT_OP(SLA, rE); break; // SLA (I* + n) -> E
            case 0x24: I_INT_OP(SLA, rH); break; // SLA (I* + n) -> H
            case 0x25: I_INT_OP(SLA, rL); break; // SLA (I* + n) -> L
            case 0x26: I_INT_OP(SLA, rALU); break; // SLA (I* + n)
            case 0x27: I_INT_OP(SLA, rA); break; // SLA (I* + n) -> A
            case 0x28: I_INT_OP(SRA, rB); break; // SRA (I* + n) -> B
            case 0x29: I_INT_OP(SRA, rC); break; // SRA (I* + n) -> C
            case 0x2A: I_INT_OP(SRA, rD); break; // SRA (I* + n) -> D
            case 0x2B: I_INT_OP(SRA, rE); break; // SRA (I* + n) -> E
            case 0x2C: I_INT_OP(SRA, rH); break; // SRA (I* + n) -> H
            case 0x2D: I_INT_OP(SRA, rL); break; // SRA (I* + n) -> L
            case 0x2E: I_INT_OP(SRA, rALU); break; // SRA (I* + n)
            case 0x2F: I_INT_OP(SRA, rA); break; // SRA (I* + n) -> A
            case 0x30: I_INT_OP(SLL, rB); break; // SLL (I* + n) -> B
            case 0x31: I_INT_OP(SLL, rC); break; // SLL (I* + n) -> C
            case 0x32: I_INT_OP(SLL, rD); break; // SLL (I* + n) -> D
            case 0x33: I_INT_OP(SLL, rE); break; // SLL (I* + n) -> E
            case 0x34: I_INT_OP(SLL, rH); break; // SLL (I* + n) -> H
            case 0x35: I_INT_OP(SLL, rL); break; // SLL (I* + n) -> L
            case 0x36: I_INT_OP(SLL, rALU); break; // SLL (I* + n)
            case 0x37: I_INT_OP(SLL, rA); break; // SLL (I* + n) -> A
            case 0x38: I_INT_OP(SRL, rB); break; // SRL (I* + n) -> B
            case 0x39: I_INT_OP(SRL, rC); break; // SRL (I* + n) -> C
            case 0x3A: I_INT_OP(SRL, rD); break; // SRL (I* + n) -> D
            case 0x3B: I_INT_OP(SRL, rE); break; // SRL (I* + n) -> E
            case 0x3C: I_INT_OP(SRL, rH); break; // SRL (I* + n) -> H
            case 0x3D: I_INT_OP(SRL, rL); break; // SRL (I* + n) -> L
            case 0x3E: I_INT_OP(SRL, rALU); break; // SRL (I* + n)
            case 0x3F: I_INT_OP(SRL, rA); break; // SRL (I* + n) -> A
            case 0x40: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x41: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x42: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x43: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x44: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x45: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x46: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x47: I_BIT_TE(0); break; // BIT 0, (I* + n)
            case 0x48: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x49: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4A: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4B: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4C: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4D: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4E: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x4F: I_BIT_TE(1); break; // BIT 1, (I* + n)
            case 0x50: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x51: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x52: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x53: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x54: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x55: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x56: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x57: I_BIT_TE(2); break; // BIT 2, (I* + n)
            case 0x58: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x59: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5A: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5B: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5C: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5D: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5E: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x5F: I_BIT_TE(3); break; // BIT 3, (I* + n)
            case 0x60: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x61: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x62: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x63: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x64: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x65: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x66: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x67: I_BIT_TE(4); break; // BIT 4, (I* + n)
            case 0x68: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x69: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6A: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6B: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6C: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6D: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6E: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x6F: I_BIT_TE(5); break; // BIT 5, (I* + n)
            case 0x70: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x71: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x72: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x73: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x74: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x75: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x76: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x77: I_BIT_TE(6); break; // BIT 6, (I* + n)
            case 0x78: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x79: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7A: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7B: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7C: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7D: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7E: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x7F: I_BIT_TE(7); break; // BIT 7, (I* + n)
            case 0x80: I_BIT_OP(RES, 0, rB); break; // RES 0, (I* + n) -> B
            case 0x81: I_BIT_OP(RES, 0, rC); break; // RES 0, (I* + n) -> C
            case 0x82: I_BIT_OP(RES, 0, rD); break; // RES 0, (I* + n) -> D
            case 0x83: I_BIT_OP(RES, 0, rE); break; // RES 0, (I* + n) -> E
            case 0x84: I_BIT_OP(RES, 0, rH); break; // RES 0, (I* + n) -> H
            case 0x85: I_BIT_OP(RES, 0, rL); break; // RES 0, (I* + n) -> L
            case 0x86: I_BIT_OP(RES, 0, rALU); break; // RES 0, (I* + n)
            case 0x87: I_BIT_OP(RES, 0, rA); break; // RES 0, (I* + n) -> A
            case 0x88: I_BIT_OP(RES, 1, rB); break; // RES 1, (I* + n) -> B
            case 0x89: I_BIT_OP(RES, 1, rC); break; // RES 1, (I* + n) -> C
            case 0x8A: I_BIT_OP(RES, 1, rD); break; // RES 1, (I* + n) -> D
            case 0x8B: I_BIT_OP(RES, 1, rE); break; // RES 1, (I* + n) -> E
            case 0x8C: I_BIT_OP(RES, 1, rH); break; // RES 1, (I* + n) -> H
            case 0x8D: I_BIT_OP(RES, 1, rL); break; // RES 1, (I* + n) -> L
            case 0x8E: I_BIT_OP(RES, 1, rALU); break; // RES 1, (I* + n)
            case 0x8F: I_BIT_OP(RES, 1, rA); break; // RES 1, (I* + n) -> A
            case 0x90: I_BIT_OP(RES, 2, rB); break; // RES 2, (I* + n) -> B
            case 0x91: I_BIT_OP(RES, 2, rC); break; // RES 2, (I* + n) -> C
            case 0x92: I_BIT_OP(RES, 2, rD); break; // RES 2, (I* + n) -> D
            case 0x93: I_BIT_OP(RES, 2, rE); break; // RES 2, (I* + n) -> E
            case 0x94: I_BIT_OP(RES, 2, rH); break; // RES 2, (I* + n) -> H
            case 0x95: I_BIT_OP(RES, 2, rL); break; // RES 2, (I* + n) -> L
            case 0x96: I_BIT_OP(RES, 2, rALU); break; // RES 2, (I* + n)
            case 0x97: I_BIT_OP(RES, 2, rA); break; // RES 2, (I* + n) -> A
            case 0x98: I_BIT_OP(RES, 3, rB); break; // RES 3, (I* + n) -> B
            case 0x99: I_BIT_OP(RES, 3, rC); break; // RES 3, (I* + n) -> C
            case 0x9A: I_BIT_OP(RES, 3, rD); break; // RES 3, (I* + n) -> D
            case 0x9B: I_BIT_OP(RES, 3, rE); break; // RES 3, (I* + n) -> E
            case 0x9C: I_BIT_OP(RES, 3, rH); break; // RES 3, (I* + n) -> H
            case 0x9D: I_BIT_OP(RES, 3, rL); break; // RES 3, (I* + n) -> L
            case 0x9E: I_BIT_OP(RES, 3, rALU); break; // RES 3, (I* + n)
            case 0x9F: I_BIT_OP(RES, 3, rA); break; // RES 3, (I* + n) -> A
            case 0xA0: I_BIT_OP(RES, 4, rB); break; // RES 4, (I* + n) -> B
            case 0xA1: I_BIT_OP(RES, 4, rC); break; // RES 4, (I* + n) -> C
            case 0xA2: I_BIT_OP(RES, 4, rD); break; // RES 4, (I* + n) -> D
            case 0xA3: I_BIT_OP(RES, 4, rE); break; // RES 4, (I* + n) -> E
            case 0xA4: I_BIT_OP(RES, 4, rH); break; // RES 4, (I* + n) -> H 
            case 0xA5: I_BIT_OP(RES, 4, rL); break; // RES 4, (I* + n) -> L
            case 0xA6: I_BIT_OP(RES, 4, rALU); break; // RES 4, (I* + n)
            case 0xA7: I_BIT_OP(RES, 4, rA); break; // RES 4, (I* + n) -> A
            case 0xA8: I_BIT_OP(RES, 5, rB); break; // RES 5, (I* + n) -> B
            case 0xA9: I_BIT_OP(RES, 5, rC); break; // RES 5, (I* + n) -> C
            case 0xAA: I_BIT_OP(RES, 5, rD); break; // RES 5, (I* + n) -> D
            case 0xAB: I_BIT_OP(RES, 5, rE); break; // RES 5, (I* + n) -> E
            case 0xAC: I_BIT_OP(RES, 5, rH); break; // RES 5, (I* + n) -> H
            case 0xAD: I_BIT_OP(RES, 5, rL); break; // RES 5, (I* + n) -> L
            case 0xAE: I_BIT_OP(RES, 5, rALU); break; // RES 5, (I* + n)
            case 0xAF: I_BIT_OP(RES, 5, rA); break; // RES 5, (I* + n) -> A
            case 0xB0: I_BIT_OP(RES, 6, rB); break; // RES 6, (I* + n) -> B
            case 0xB1: I_BIT_OP(RES, 6, rC); break; // RES 6, (I* + n) -> C
            case 0xB2: I_BIT_OP(RES, 6, rD); break; // RES 6, (I* + n) -> D
            case 0xB3: I_BIT_OP(RES, 6, rE); break; // RES 6, (I* + n) -> E
            case 0xB4: I_BIT_OP(RES, 6, rH); break; // RES 6, (I* + n) -> H
            case 0xB5: I_BIT_OP(RES, 6, rL); break; // RES 6, (I* + n) -> L
            case 0xB6: I_BIT_OP(RES, 6, rALU); break; // RES 6, (I* + n)
            case 0xB7: I_BIT_OP(RES, 6, rA); break; // RES 6, (I* + n) -> A
            case 0xB8: I_BIT_OP(RES, 7, rB); break; // RES 7, (I* + n) -> B
            case 0xB9: I_BIT_OP(RES, 7, rC); break; // RES 7, (I* + n) -> C
            case 0xBA: I_BIT_OP(RES, 7, rD); break; // RES 7, (I* + n) -> D
            case 0xBB: I_BIT_OP(RES, 7, rE); break; // RES 7, (I* + n) -> E
            case 0xBC: I_BIT_OP(RES, 7, rH); break; // RES 7, (I* + n) -> H
            case 0xBD: I_BIT_OP(RES, 7, rL); break; // RES 7, (I* + n) -> L
            case 0xBE: I_BIT_OP(RES, 7, rALU); break; // RES 7, (I* + n)
            case 0xBF: I_BIT_OP(RES, 7, rA); break; // RES 7, (I* + n) -> A
            case 0xC0: I_BIT_OP(SET, 0, rB); break; // SET 0, (I* + n) -> B
            case 0xC1: I_BIT_OP(SET, 0, rC); break; // SET 0, (I* + n) -> C
            case 0xC2: I_BIT_OP(SET, 0, rD); break; // SET 0, (I* + n) -> D
            case 0xC3: I_BIT_OP(SET, 0, rE); break; // SET 0, (I* + n) -> E
            case 0xC4: I_BIT_OP(SET, 0, rH); break; // SET 0, (I* + n) -> H
            case 0xC5: I_BIT_OP(SET, 0, rL); break; // SET 0, (I* + n) -> L
            case 0xC6: I_BIT_OP(SET, 0, rALU); break; // SET 0, (I* + n)
            case 0xC7: I_BIT_OP(SET, 0, rA); break; // SET 0, (I* + n) -> A
            case 0xC8: I_BIT_OP(SET, 1, rB); break; // SET 1, (I* + n) -> B
            case 0xC9: I_BIT_OP(SET, 1, rC); break; // SET 1, (I* + n) -> C
            case 0xCA: I_BIT_OP(SET, 1, rD); break; // SET 1, (I* + n) -> D
            case 0xCB: I_BIT_OP(SET, 1, rE); break; // SET 1, (I* + n) -> E
            case 0xCC: I_BIT_OP(SET, 1, rH); break; // SET 1, (I* + n) -> H
            case 0xCD: I_BIT_OP(SET, 1, rL); break; // SET 1, (I* + n) -> L
            case 0xCE: I_BIT_OP(SET, 1, rALU); break; // SET 1, (I* + n)
            case 0xCF: I_BIT_OP(SET, 1, rA); break; // SET 1, (I* + n) -> A
            case 0xD0: I_BIT_OP(SET, 2, rB); break; // SET 2, (I* + n) -> B
            case 0xD1: I_BIT_OP(SET, 2, rC); break; // SET 2, (I* + n) -> C
            case 0xD2: I_BIT_OP(SET, 2, rD); break; // SET 2, (I* + n) -> D
            case 0xD3: I_BIT_OP(SET, 2, rE); break; // SET 2, (I* + n) -> E
            case 0xD4: I_BIT_OP(SET, 2, rH); break; // SET 2, (I* + n) -> H
            case 0xD5: I_BIT_OP(SET, 2, rL); break; // SET 2, (I* + n) -> L
            case 0xD6: I_BIT_OP(SET, 2, rALU); break; // SET 2, (I* + n)
            case 0xD7: I_BIT_OP(SET, 2, rA); break; // SET 2, (I* + n) -> A
            case 0xD8: I_BIT_OP(SET, 3, rB); break; // SET 3, (I* + n) -> B
            case 0xD9: I_BIT_OP(SET, 3, rC); break; // SET 3, (I* + n) -> C
            case 0xDA: I_BIT_OP(SET, 3, rD); break; // SET 3, (I* + n) -> D
            case 0xDB: I_BIT_OP(SET, 3, rE); break; // SET 3, (I* + n) -> E
            case 0xDC: I_BIT_OP(SET, 3, rH); break; // SET 3, (I* + n) -> H
            case 0xDD: I_BIT_OP(SET, 3, rL); break; // SET 3, (I* + n) -> L
            case 0xDE: I_BIT_OP(SET, 3, rALU); break; // SET 3, (I* + n)
            case 0xDF: I_BIT_OP(SET, 3, rA); break; // SET 3, (I* + n) -> A
            case 0xE0: I_BIT_OP(SET, 4, rB); break; // SET 4, (I* + n) -> B
            case 0xE1: I_BIT_OP(SET, 4, rC); break; // SET 4, (I* + n) -> C
            case 0xE2: I_BIT_OP(SET, 4, rD); break; // SET 4, (I* + n) -> D
            case 0xE3: I_BIT_OP(SET, 4, rE); break; // SET 4, (I* + n) -> E
            case 0xE4: I_BIT_OP(SET, 4, rH); break; // SET 4, (I* + n) -> H
            case 0xE5: I_BIT_OP(SET, 4, rL); break; // SET 4, (I* + n) -> L
            case 0xE6: I_BIT_OP(SET, 4, rALU); break; // SET 4, (I* + n)
            case 0xE7: I_BIT_OP(SET, 4, rA); break; // SET 4, (I* + n) -> A
            case 0xE8: I_BIT_OP(SET, 5, rB); break; // SET 5, (I* + n) -> B
            case 0xE9: I_BIT_OP(SET, 5, rC); break; // SET 5, (I* + n) -> C
            case 0xEA: I_BIT_OP(SET, 5, rD); break; // SET 5, (I* + n) -> D
            case 0xEB: I_BIT_OP(SET, 5, rE); break; // SET 5, (I* + n) -> E
            case 0xEC: I_BIT_OP(SET, 5, rH); break; // SET 5, (I* + n) -> H
            case 0xED: I_BIT_OP(SET, 5, rL); break; // SET 5, (I* + n) -> L
            case 0xEE: I_BIT_OP(SET, 5, rALU); break; // SET 5, (I* + n)
            case 0xEF: I_BIT_OP(SET, 5, rA); break; // SET 5, (I* + n) -> A
            case 0xF0: I_BIT_OP(SET, 6, rB); break; // SET 6, (I* + n) -> B
            case 0xF1: I_BIT_OP(SET, 6, rC); break; // SET 6, (I* + n) -> C
            case 0xF2: I_BIT_OP(SET, 6, rD); break; // SET 6, (I* + n) -> D
            case 0xF3: I_BIT_OP(SET, 6, rE); break; // SET 6, (I* + n) -> E
            case 0xF4: I_BIT_OP(SET, 6, rH); break; // SET 6, (I* + n) -> H
            case 0xF5: I_BIT_OP(SET, 6, rL); break; // SET 6, (I* + n) -> L
            case 0xF6: I_BIT_OP(SET, 6, rALU); break; // SET 6, (I* + n)
            case 0xF7: I_BIT_OP(SET, 6, rA); break; // SET 6, (I* + n) -> A
            case 0xF8: I_BIT_OP(SET, 7, rB); break; // SET 7, (I* + n) -> B
            case 0xF9: I_BIT_OP(SET, 7, rC); break; // SET 7, (I* + n) -> C
            case 0xFA: I_BIT_OP(SET, 7, rD); break; // SET 7, (I* + n) -> D
            case 0xFB: I_BIT_OP(SET, 7, rE); break; // SET 7, (I* + n) -> E
            case 0xFC: I_BIT_OP(SET, 7, rH); break; // SET 7, (I* + n) -> H
            case 0xFD: I_BIT_OP(SET, 7, rL); break; // SET 7, (I* + n) -> L
            case 0xFE: I_BIT_OP(SET, 7, rALU); break; // SET 7, (I* + n)
            case 0xFF: I_BIT_OP(SET, 7, rA); break; // SET 7, (I* + n) -> A
        }
    }
}
