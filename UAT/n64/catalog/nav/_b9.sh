cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b8.log) -ge 9 ]; do sleep 20; done
for g in Knockout_Kings_2000 WCW_vs_nWo_World_Tour Turok_Rage_Wars; do DL=300 bash nav/try.sh $g nav/step_long_mix.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_lm; done
