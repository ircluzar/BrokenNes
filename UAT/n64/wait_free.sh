#!/bin/bash
# [UAT/n64] Wait until no SessionProbe/RunN64 runs on this PC and more than N64_MIN_FREE_GB GB RAM is free (one emulator at a time).
# Default threshold: a third of the PC's RAM, capped at 8 GB (a fixed 8 GB never cleared on a 12 GB PC idling at 8 GB free:
# ZIRCON's items sat here until their wall time and came back INCOMPLETE).
if [ -z "$N64_MIN_FREE_GB" ]; then
  N64_MIN_FREE_GB=$(powershell -NoProfile -c "[int][Math]::Min(8,[Math]::Floor((Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize/1MB/3))" | tr -d '\r')
fi
while tasklist | grep -qiE "sessionprobe|runn64" || [ "$(powershell -NoProfile -c "[int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB)" | tr -d '\r')" -le "${N64_MIN_FREE_GB:-4}" ]; do sleep 5; done
