import json, os
WT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..'))   # the repository this folder sits in
def cps(frames):  # frame index f of md-find = f + 1 frames run
    return {str(f + 1): "" for f in frames}
E = []
def add(name, rom, frames, inp="", region=None, verified="oracle"):
    e = {"name": name, "rom": rom, "verified": verified, "checkpoints": cps(frames)}
    if inp: e["input"] = inp
    if region: e["region"] = region
    E.append(e)
g = "games:"
add("md01 Flicky (title)", g + "Flicky (JUE) [!].bin", [181], "318:Start,324:,797:Right")
add("md02 Altered Beast", g + "Altered Beast (JU) (REV 01) [!].bin", [432, 622, 1175, 1363, 1615, 1867], "497:Start,503:,797:Start,803:,1097:Start,1103:")
add("md03 Sonic the Hedgehog", g + "Sonic the Hedgehog (JUE) [!].bin", [159, 554, 814], "737:Start,743:,1276:Right")
add("md04 Golden Axe", g + "Golden Axe (JU) (REV 00) [!].bin", [227, 494, 923, 1343, 1654, 1963], "557:Start,563:,857:Start,863:,1156:Start,1162:")
add("md05 Strider", g + "Strider (UE) [!].bin", [124, 557, 638, 1596, 1863], "617:Start,623:,1097:Start,1103:")
add("md06 Streets of Rage", g + "Streets of Rage (JUE) (REV 00) [!].bin", [244, 492, 762], "737:Start,743:,1037:Start,1043:,1396:Start,1402:")
add("md07 Castle of Illusion", g + "Mickey Mouse - Castle of Illusion (U) [!].bin", [63, 451, 744, 1177, 1692], "737:Start,743:,1216:Start,1222:")
add("md08 Ecco the Dolphin", g + "ECCO (U) [!].bin", [220, 614, 801, 1525, 1835], "737:Start,743:,1276:Start,1282:")
add("md09 Sonic the Hedgehog 2", g + "Sonic the Hedgehog 2 (JUE) [!].bin", [242, 474, 928], "617:Start,623:,1097:Start,1103:,1456:Start,1462:")
add("md10 Kid Chameleon", g + "Kid Chameleon (JUE) [!].bin", [234, 419, 715, 1175, 1263, 1967], "617:Start,623:,1156:Start,1162:")
t = "tests:"
add("Titan Overdrive 1 (PAL)", t + "Titan_Overdrive_Rev1.1-106-Final.bin", [81, 412, 646, 785, 1006, 1081, 1429, 1796, 1854, 3172, 4314, 4672, 5030], region="pal")
add("Titan Overdrive 2 (PAL)", t + "titan-overdrive2.zip|titan-overdrive2.bin", [305, 611, 920, 1162, 1534, 1847, 2154, 2462, 2771, 3708], region="pal")
add("MacDonald V counter test", t + "vctest.zip|vctest.bin", [399])
add("Fonzie's window test", t + "window-test-by-fonzie.zip|Window Test by Fonzie (PD).bin", [399])
add("MacDonald window distortion bug", t + "window-distortion-bug.zip|Window distortion bug.BIN", [283])
add("Nemesis CRAM flicker (H32)", t + "cram-flicker.zip|cram flicker.bin", [399, 402])
add("Chilly Willy direct colour DMA", t + "Direct-Color-DMA.zip|Direct-Color-DMA.bin", [9, 100, 399], verified="regression")
add("Nemesis VDP FIFO test, page 2 (tests 10-16)", t + "VDPFIFOTesting.zip|VDPFIFOTesting.bin", [2599], "400:Start,410:", verified="regression")
path = os.path.join(WT, 'Workshop', 'MdGolden')
os.makedirs(path, exist_ok=True)
json.dump({"version": 1, "note": "Genesis golden pictures: FNV-1a over width, height and ARGB pixels at the listed frame counts (frames run, 1-based). 'oracle' entries are frames that matched BlastEm 1.0.0's own screenshots pixel for pixel; 'regression' entries only pin this emulator. ROMs are not in git; a missing ROM is skipped.", "entries": E}, open(os.path.join(path, 'md_golden.json'), 'w'), indent=2)
print(len(E), 'entries')
