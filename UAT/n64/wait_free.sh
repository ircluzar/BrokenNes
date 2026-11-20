#!/bin/bash
# [UAT/n64] Wait until no SessionProbe/RunN64 runs on this PC and more than ${N64_MIN_FREE_GB:-8} GB RAM is free (one emulator at a time).
while tasklist | grep -qiE "sessionprobe|runn64" || [ "$(powershell -NoProfile -c "[int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB)" | tr -d '\r')" -le "${N64_MIN_FREE_GB:-8}" ]; do sleep 5; done
