#!/bin/bash
# [UAT/n64] Shared settings for the N64 (cor64) gates. Sourced by every script in UAT/n64.
# Resolution order for each setting: environment variable, then UAT/n64/n64.local.json (per PC, gitignored), then
# %BROKENNES_WORK%\n64\n64.local.json, then the default. Same idea as sega-gate.ps1 (-Roms, BROKENNES_SMS_ROMS, C:\BrokenNes-work\...).
#   N64_SM64_ROM     Super Mario 64 (U) [!] .z64 (md5 20b854b239203baf6c961b850a4a51a2)   json: sm64Rom   default <N64_ROM_DIR>/sm64.z64
#   N64_ROM_DIR      folder of ready .z64 ROMs named <compat id>.z64 (sm64.z64, oot.z64...)   json: romDir   (BROKENNES_N64_ROMS also read)
#   N64_LIBRARY_DIR  the archive library (.7z/.zip/.z64), names as in compat/games.tsv           json: libraryDir
#   N64_STATE_DIR    castle.state and title.state (SM64 save states; game memory, so not in git) json: stateDir  default goldens/states
#   N64_KROM_DIR     krom's RSPTest folder (public domain test ROMs, reference PNGs are in goldens/krom) json: kromDir
#   N64_WORK         scratch: ROM cache, results, gate output  default %BROKENNES_WORK%\n64 (C:\BrokenNes-work\n64)
#   Cor64Root        the cor64 tree (as for the BrokenNes build)  default <repo>/third_party/cor64
#   N64_BIN / N64_BIN_RT  built probe folders  default <Cor64Root>/bin and <Cor64Root>/bin_rt (made by UAT/n64/build.sh)
N64_UAT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
N64_REPO=$(cd "$N64_UAT/../.." && pwd)
_work=${BROKENNES_WORK:-C:/BrokenNes-work}; _work=$(cygpath -u "$_work" 2>/dev/null || echo "$_work")
_cfg() {  # _cfg key -> value from the first local json that has it
  for f in "$N64_UAT/n64.local.json" "$_work/n64/n64.local.json"; do
    [ -f "$f" ] || continue
    v=$(python -I -c "import json,sys; print(json.load(open(sys.argv[1])).get(sys.argv[2],''))" "$(cygpath -m "$f")" "$1" 2>/dev/null | tr -d '\r')
    [ -n "$v" ] && { echo "$v"; return; }
  done
}
_u() { [ -z "$1" ] && return 0; cygpath -u "$1" 2>/dev/null || echo "$1"; }
N64_WORK=$(_u "${N64_WORK:-$(_cfg work)}"); N64_WORK=${N64_WORK:-$_work/n64}
N64_ROM_DIR=$(_u "${N64_ROM_DIR:-${BROKENNES_N64_ROMS:-$(_cfg romDir)}}")
N64_LIBRARY_DIR=$(_u "${N64_LIBRARY_DIR:-$(_cfg libraryDir)}")
N64_SM64_ROM=$(_u "${N64_SM64_ROM:-$(_cfg sm64Rom)}"); [ -n "$N64_SM64_ROM" ] || N64_SM64_ROM="$N64_ROM_DIR/sm64.z64"
N64_STATE_DIR=$(_u "${N64_STATE_DIR:-$(_cfg stateDir)}"); N64_STATE_DIR=${N64_STATE_DIR:-$N64_UAT/goldens/states}
N64_KROM_DIR=$(_u "${N64_KROM_DIR:-$(_cfg kromDir)}")
COR64=$(_u "${Cor64Root:-$(_cfg cor64Root)}"); COR64=${COR64%/}; COR64=${COR64:-$N64_REPO/third_party/cor64}
N64_BIN=$(_u "${N64_BIN:-$COR64/bin}"); N64_BIN_RT=$(_u "${N64_BIN_RT:-$COR64/bin_rt}")
N64_GOLD="$N64_UAT/goldens"
export N64_UAT N64_REPO N64_WORK N64_ROM_DIR N64_LIBRARY_DIR N64_SM64_ROM N64_STATE_DIR N64_KROM_DIR COR64 N64_BIN N64_BIN_RT N64_GOLD
