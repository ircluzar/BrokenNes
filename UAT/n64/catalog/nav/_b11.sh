cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b10.log) -ge 4 ]; do sleep 20; done
DL=300 bash nav/try.sh Knockout_Kings_2000 nav/step_long_m4mix.txt; mv runs_v2_nav_try/Knockout_Kings_2000 runs_v2_nav_try/Knockout_Kings_2000_m4
for g in Turok_Rage_Wars WCW_vs_nWo_World_Tour Premier_Manager_64 Pokemon_Stadium Pocket_Monsters_Stadium Michael_Owens_WLS_2000 Mahjong_64 Dezaemon_3D GT_64_Championship_Edition F1_Racing_Championship Jinsei_Game_64; do bash nav/try.sh $g nav/step_m4mix.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_m4m; done
