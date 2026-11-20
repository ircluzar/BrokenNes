cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b4.log) -ge 14 ]; do sleep 20; done
for g in Jeopardy Toukon_Road_2_The_Next_Generation Power_League_Baseball_64 Getter_Love_Cho_Renai_Party_Game 64_Oozumou_2 Cruis_n_USA Robotech_Crystal_Dreams; do VISP=1 DL=300 bash nav/try.sh $g nav/step_dpad.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_dpv1; done
