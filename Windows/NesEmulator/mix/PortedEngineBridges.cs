namespace NesEmulator
{
    // FA-BR-1 (2026-11-05): the NES APU menu's SNES and Game Boy entries for every ported engine (docs/projects/foreign-apu-project.md).
    // Each class is the stock bridge (APU_SNES / APU_DMG) with its engine fixed, so the pick is a plain NES APU id ("SNES_ZS15", "DMG_GNUB")
    // that save states, settings and the HTTP API carry like any other core, and that does not depend on the mix lab's MixConfig.

    public sealed class APU_SNES_ZS15 : APU_SNES { public APU_SNES_ZS15(Bus bus) : base(bus, "ZS15") { } }
    public sealed class APU_SNES_ZS151 : APU_SNES { public APU_SNES_ZS151(Bus bus) : base(bus, "ZS151") { } }
    public sealed class APU_SNES_S916 : APU_SNES { public APU_SNES_S916(Bus bus) : base(bus, "S916") { } }
    public sealed class APU_SNES_S9X1 : APU_SNES { public APU_SNES_S9X1(Bus bus) : base(bus, "S9X1") { } }
    public sealed class APU_SNES_S951 : APU_SNES { public APU_SNES_S951(Bus bus) : base(bus, "S951") { } }
    public sealed class APU_SNES_OSPC : APU_SNES { public APU_SNES_OSPC(Bus bus) : base(bus, "OSPC") { } }
    public sealed class APU_SNES_SNDS : APU_SNES { public APU_SNES_SNDS(Bus bus) : base(bus, "SNDS") { } }

    public sealed class APU_DMG_GNUB : APU_DMG { public APU_DMG_GNUB(Bus bus) : base(bus, "GNUB", extBank: false) { } }
    public sealed class APU_DMG_VBA : APU_DMG { public APU_DMG_VBA(Bus bus) : base(bus, "VBA", extBank: false) { } }
    public sealed class APU_DMG_VBAM : APU_DMG { public APU_DMG_VBAM(Bus bus) : base(bus, "VBAM", extBank: false) { } }
    public sealed class APU_DMG_TGBD : APU_DMG { public APU_DMG_TGBD(Bus bus) : base(bus, "TGBD", extBank: false) { } }
    public sealed class APU_DMG_GYOB : APU_DMG { public APU_DMG_GYOB(Bus bus) : base(bus, "GYOB", extBank: false) { } }
}

namespace NesEmulator
{
    // Step 2 (2026-11-05): the Master System PSG ports behind the NES's APU_SMS bridge (the engine renders, the stock PSG mirrors the registers).
    public sealed class APU_SMS_BLST : APU_SMS { public APU_SMS_BLST(Bus bus) : base(bus, "BLST") { } }
    public sealed class APU_SMS_M036 : APU_SMS { public APU_SMS_M036(Bus bus) : base(bus, "M036") { } }
    public sealed class APU_SMS_GPGX : APU_SMS { public APU_SMS_GPGX(Bus bus) : base(bus, "GPGX") { } }
    public sealed class APU_SMS_GENS : APU_SMS { public APU_SMS_GENS(Bus bus) : base(bus, "GENS") { } }
    public sealed class APU_SMS_PICO : APU_SMS { public APU_SMS_PICO(Bus bus) : base(bus, "PICO") { } }
    public sealed class APU_SMS_DARC : APU_SMS { public APU_SMS_DARC(Bus bus) : base(bus, "DARC") { } }
    public sealed class APU_SMS_DEGA : APU_SMS { public APU_SMS_DEGA(Bus bus) : base(bus, "DEGA") { } }
}

namespace NesEmulator
{
    // Step 2: the Genesis sound-unit ports behind the NES's APU_MD bridge (the unit's Z80 held in reset, the bridge's YM2612 / PSG writes mirrored into it, its output played).
    public sealed class APU_MD_GENS : APU_MD { public APU_MD_GENS(Bus bus) : base(bus, "GENS") { } }
    public sealed class APU_MD_PICO : APU_MD { public APU_MD_PICO(Bus bus) : base(bus, "PICO") { } }
    public sealed class APU_MD_M036 : APU_MD { public APU_MD_M036(Bus bus) : base(bus, "M036") { } }
    public sealed class APU_MD_BLST : APU_MD { public APU_MD_BLST(Bus bus) : base(bus, "BLST") { } }
    public sealed class APU_MD_GPGX : APU_MD { public APU_MD_GPGX(Bus bus) : base(bus, "GPGX") { } }
}

namespace NesEmulator
{
    // All-APU matrix, FM family (2026-11-05): the Master System's ported YM2413 engines behind the NES's APU_SMSFM bridge (the bridge's FM and PSG writes go through the unit's ports, its stock chips mirror them, the unit's output is played).
    // The Game Gear has no FM chip, so its menu offers neither (its own family is the Master System's: these are never NES:<id> there).
    public sealed class APU_SMSFM_M036FM : APU_SMSFM { public APU_SMSFM_M036FM(Bus bus) : base(bus, "M036FM") { } }
    public sealed class APU_SMSFM_E055 : APU_SMSFM { public APU_SMSFM_E055(Bus bus) : base(bus, "E055") { } }
}
