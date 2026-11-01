#!/usr/bin/env bash
# Accuracy scorecard for a core loadout. Usage: ./suite.sh [--cpu ID] [--ppu ID] [--apu ID]
# Runs nestest (CPU only) plus the blargg protocol suites; prints a pass/fail summary.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; T="$here/testroms/src"
cpu=""; prev=""; for a in "$@"; do [ "$prev" = "--cpu" ] && cpu="$a"; prev="$a"; done
echo "== nestest"
"$here/nesh" nestest "$T/other/nestest.nes" "$T/other/nestest.log" ${cpu:+--cpu "$cpu"}
echo; echo "== blargg suites"
"$here/nesh" blargg "$@" \
  "$T/instr_test-v5/rom_singles" "$T/instr_timing/rom_singles" "$T/cpu_interrupts_v2/rom_singles" \
  "$T/apu_test/rom_singles" "$T/ppu_vbl_nmi/rom_singles" "$T/mmc3_test_2/rom_singles" \
  "$T/ppu_open_bus" "$T/ppu_read_buffer" "$T/oam_read" "$T/cpu_dummy_reads" "$T/instr_misc/rom_singles"
