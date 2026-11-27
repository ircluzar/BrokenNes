#!/bin/bash
cd /c/BrokenNes-work/scbug; . /c/BrokenNes-work/sc98/slot.sh; SLOT_OWNER=scbug claim_slot par2
G=gbinm/BrokenNes.Workshop.exe; P=C:/BrokenNes-work/sc98/pair; A=C:/BrokenNes-work/aoe
SC=(--x86boot --machine WIN98-P166 --hdd $P/hd_scpairC_256.img --hdd2 $P/hd_scpairD.img --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --apu SB16_D072 --loadstate $P/st_sbingame2.bin --frames 300)
AO=(--x86boot --machine WIN98-P166 --hdd $A/hd_aoeC.img --hdd2 $A/hd_aoeD.img --cd C:/BrokenNes-work/x86_cds/age_of_empires.iso --apu SB16_D072 --loadstate $A/st_a23.bin --frames 300)
par() { local k=$1; shift; rm -f p_j.bin p_n.bin; X86_NONIC=1 X86_JITSYNC=1 $G "$@" --savestate p_j.bin > p_j.txt 2>&1; X86_NONIC=1 X86_NOJIT=1 $G "$@" --savestate p_n.bin > p_n.txt 2>&1
  if [ -s p_j.bin ] && cmp -s p_j.bin p_n.bin; then echo "$k jit parity OK ($(stat -c %s p_j.bin) bytes)"; else echo "$k jit parity FAIL (sizes $(stat -c %s p_j.bin p_n.bin 2>&1 | tr '\n' ' '))"; fi; rm -f p_j.bin p_n.bin; }
par sc "${SC[@]}"; par aoe "${AO[@]}"
$G --bug-test --only media,ring,bundle,places,games > bugtest.txt 2>&1; echo "bug-test rc=$?"; tail -3 bugtest.txt
release_slot
