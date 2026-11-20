cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b6.log) -ge 4 ]; do sleep 20; done
for g in Jeopardy Toukon_Road_2_The_Next_Generation Power_League_Baseball_64 64_Oozumou_2; do bash nav/try.sh $g nav/step_m4dpad.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_m4; done
