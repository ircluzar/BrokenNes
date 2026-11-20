cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b7.log) -ge 4 ]; do sleep 20; done
for g in Harvest_Moon_64 J_League_Tactics_Soccer Super_Robot_Taisen_64 PD_Ultraman_Battle_Collection_64 Premier_Manager_64; do bash nav/try.sh $g nav/step_name.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_nm; done
for g in Forsaken_64 Superman Space_Station_Silicon_Valley Zool_Majou_Tsukai_Densetsu; do { cat nav/observe.txt; cat nav/step_intro.txt; } > nav/_in_$g.txt; bash nav/run.sh $g nav/_in_$g.txt; mkdir -p runs_v2_nav_try; rm -rf runs_v2_nav_try/${g}_in; mv runs_v2_nav/$g runs_v2_nav_try/${g}_in; done
