#!/bin/bash
# [UAT/n64] One emulator per PC: a mkdir lock held for the life of the calling script (released on exit; a lock whose
# Windows owner process is gone is taken over). wait_free.sh alone was a check-then-start race: two jobs waiting on the
# same PC both saw "no SessionProbe" and started together, and two cor64 + parallel-rdp processes on one Intel HD 630
# crash (c0000005 -> rc=139 catalog runs, krom NO-IMAGE). Opt out with N64_PROBE_LOCK=0.
# Usage: . lib/probe_lock.sh; probe_lock   (blocks until the lock is ours)
N64_PROBE_LOCK=${N64_PROBE_LOCK:-$(cygpath -u "${TEMP:-/tmp}")/n64_probe.lock}
_probe_lock_mine=""
_probe_lock_alive() { [ -n "$1" ] && tasklist //FI "PID eq $1" //NH 2>/dev/null | grep -q " $1 "; }
probe_lock() {
  [ "$N64_PROBE_LOCK" = 0 ] && return 0
  [ -n "$_probe_lock_mine" ] && return 0
  local me; me=$(cat /proc/$$/winpid 2>/dev/null || echo $$)
  while ! mkdir "$N64_PROBE_LOCK" 2>/dev/null; do
    local owner; owner=$(cat "$N64_PROBE_LOCK/pid" 2>/dev/null)
    if [ -n "$owner" ] && ! _probe_lock_alive "$owner"; then rm -rf "$N64_PROBE_LOCK"; continue; fi
    sleep 3
  done
  echo "$me" > "$N64_PROBE_LOCK/pid"; _probe_lock_mine=1
  trap 'probe_unlock' EXIT
}
probe_unlock() {
  [ -n "$_probe_lock_mine" ] || return 0
  [ "$(cat "$N64_PROBE_LOCK/pid" 2>/dev/null)" = "$(cat /proc/$$/winpid 2>/dev/null || echo $$)" ] && rm -rf "$N64_PROBE_LOCK"
  _probe_lock_mine=""
}
