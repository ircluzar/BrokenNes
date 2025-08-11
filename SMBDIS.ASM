; Minimal test disassembly subset for SMB rebuilder
PPU_STATUS = $2002
PPU_CTRL_REG2 = $2001
FrameCounter = $0009
Start:
    SEI
    CLD
    LDX #$FF
    TXS
VBlank1:
    LDA PPU_STATUS
    BPL VBlank1
    INC FrameCounter
    JMP Start
NonMaskableInterrupt:
    RTI
