cd /c/Users/philt/Desktop/cer64plus/lanes/catalog_nav1/cor64/catalog
until [ $(grep -c rc= nav/_b9.log) -ge 3 ]; do sleep 20; done
for g in Jeopardy 64_Oozumou_2 Michael_Owens_WLS_2000 Choro_Q_64_II; do bash nav/try.sh $g nav/step_btns.txt; mv runs_v2_nav_try/$g runs_v2_nav_try/${g}_bt; done
