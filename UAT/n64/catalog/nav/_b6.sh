cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b5.log) -ge 7 ]; do sleep 20; done
t() { bash nav/try.sh $1 nav/$2.txt; mv runs_v2_nav_try/$1 runs_v2_nav_try/${1}_$3; }
{ cat nav/step_dp_dda_aw2.txt | head -60; } > /dev/null
cat nav/step_dpad.txt nav/step_bz.txt > nav/step_dp_bz.txt
t Battlezone_Rise_of_the_Black_Dogs step_dp_bz bz
for g in Knockout_Kings_2000 WCW_vs_nWo_World_Tour Turok_Rage_Wars; do t $g step_long long; done
