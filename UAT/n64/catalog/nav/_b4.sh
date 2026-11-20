cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
t() { bash nav/try.sh $1 nav/$2.txt; mv runs_v2_nav_try/$1 runs_v2_nav_try/${1}_$3; }
for g in WCW_vs_nWo_World_Tour Hot_Wheels_Turbo_Racing Snowboard_Kids Space_Invaders Carmageddon_64 Knockout_Kings_2000 Choro_Q_64_II Mahjong_64 Turok_Rage_Wars; do t $g step_dp_aw2 dpaw; done
t Battlezone_Rise_of_the_Black_Dogs step_dp_dda_aw2 dpaw
for g in Michael_Owens_WLS_2000 Pocket_Monsters_Stadium Dezaemon_3D; do t $g step_dp_mix dpaw; done
t F_1_World_Grand_Prix_II step_dpad dp2
