cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
t() { bash nav/try.sh $1 nav/$2.txt; mv runs_v2_nav_try/$1 runs_v2_nav_try/${1}_$3; }
for g in WCW_vs_nWo_World_Tour Hot_Wheels_Turbo_Racing Knockout_Kings_2000 Michael_Owens_WLS_2000 Jeopardy Power_League_Baseball_64 Carmageddon_64 Cruis_n_USA 64_Oozumou_2 Premier_Manager_64 F_1_World_Grand_Prix_II Mahjong_64 Lode_Runner_3_D International_Track_Field_2000 Dezaemon_3D Toukon_Road_2_The_Next_Generation Choro_Q_64_II F1_Racing_Championship Getter_Love_Cho_Renai_Party_Game; do t $g step_mix mix; done
for g in Battlezone_Rise_of_the_Black_Dogs Pokemon_Stadium Re_Volt Robotech_Crystal_Dreams Jinsei_Game_64 Jikkyou_J_League_Perfect_Striker Snowboard_Kids Turok_Rage_Wars Pocket_Monsters_Stadium Brunswick_Circuit_Pro_Bowling; do t $g step_awalk2 aw2; done
t GT_64_Championship_Edition step_gt64 gt
for g in WCW_vs_nWo_World_Tour Hot_Wheels_Turbo_Racing Knockout_Kings_2000 Space_Invaders; do PAK=none t $g step_awalk nopak; done
