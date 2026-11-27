Get-ChildItem C:\REMOTE_WORK\runs\scbot_fullA, C:\REMOTE_WORK\runs\scbot_fullB, D:\REMOTE_WORK\results\scbot_fullA, D:\REMOTE_WORK\results\scbot_fullB -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer -and $_.Name -notlike 'end_step*' } | Select-Object FullName,Length,LastWriteTime | Format-Table -AutoSize | Out-String -Width 250
"--- A run.txt tail"; Get-Content C:\REMOTE_WORK\runs\scbot_fullA\run.txt -Tail 5 -ErrorAction SilentlyContinue
"--- B run.txt tail"; Get-Content C:\REMOTE_WORK\runs\scbot_fullB\run.txt -Tail 5 -ErrorAction SilentlyContinue
"--- procs"; Get-Process | Where-Object { $_.ProcessName -like '*Workshop*' -or $_.ProcessName -like '*ffmpeg*' } | Select-Object Id,ProcessName,StartTime | Format-Table | Out-String
