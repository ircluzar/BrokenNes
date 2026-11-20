#!/bin/bash
# [UAT/n64] compat/tools/get_rom.sh <id>: uses N64_ROM_DIR/<id>.z64 when present, else puts the ROM at N64_WORK/roms/<id>.z64 (cached), big-endian (.z64)
# whatever the source byte order (.v64 byte-swapped, .n64 word-swapped), and prints the header.
# Source: compat/games.tsv column 3, under N64_LIBRARY_DIR (UAT/n64/lib/n64env.sh; read only: never written).
# Safe to run in parallel: each id has its own temp dir and the final file is moved in atomically.
set -e
C=$(cd "$(dirname "$0")/.." && pwd); ID="$1"
. "$C/../lib/n64env.sh"
[ -n "$ID" ] || { echo "usage: get_rom.sh <id>" >&2; exit 2; }
ROMDIR=${ROMDIR:-$N64_LIBRARY_DIR}; SEVENZ=${SEVENZ:-"/c/Program Files/7-Zip/7z.exe"}
ARCH=$(awk -F'\t' -v id="$ID" '$1==id {print $3}' "$C/games.tsv")
[ -n "$ARCH" ] || { echo "unknown id $ID (see compat/games.tsv)" >&2; exit 2; }
RD="$N64_WORK/roms"; [ -n "$N64_ROM_DIR" ] && [ -s "$N64_ROM_DIR/$ID.z64" ] && RD="$N64_ROM_DIR"
OUT="$RD/$ID.z64"; mkdir -p "$RD"
if [ ! -s "$OUT" ]; then
  TMP="$RD/.tmp_$ID.$$"; rm -rf "$TMP"; mkdir -p "$TMP"
  case "$ARCH" in
    *.7z|*.zip) "$SEVENZ" e -y -o"$(cygpath -w "$TMP")" "$(cygpath -w "$ROMDIR/$ARCH")" >/dev/null
                # Prefer a verified good dump: (U) [!], then (E) [!], then any [!]; avoid kiosk/demo builds. Only then
                # the largest file. Picking the largest alone chose bad ([b1]) or patched ([f1]/[t1]) dumps.
                SRC=""
                for pat in '\(U\).*\[!\]' '\(E\).*\[!\]' '\[!\]'; do
                  SRC=$(ls "$TMP"/* | grep -iE '\.(z64|v64|n64)$' | grep -viE 'kiosk|demo|beta|proto' | grep -E "$pat" | head -1)
                  [ -n "$SRC" ] && break
                done
                [ -n "$SRC" ] || SRC=$(ls -S "$TMP"/* | grep -iE '\.(z64|v64|n64|rom|bin)$' | head -1)
                [ -n "$SRC" ] || SRC=$(ls -S "$TMP"/* | head -1) ;;
    *) cp "$ROMDIR/$ARCH" "$TMP/rom"; SRC="$TMP/$ARCH"; mv "$TMP/rom" "$SRC" ;;
  esac
  python -I - "$(cygpath -m "$SRC")" "$(cygpath -m "$TMP/out.z64")" <<'EOF'
import sys
d = bytearray(open(sys.argv[1], 'rb').read())
m = bytes(d[:4])
if m == b'\x37\x80\x40\x12':   # v64: 16-bit byte swapped
    d[0::2], d[1::2] = d[1::2], d[0::2]
elif m == b'\x40\x12\x37\x80': # n64: 32-bit little endian
    for i in range(0, len(d) - 3, 4): d[i:i+4] = d[i:i+4][::-1]
elif m != b'\x80\x37\x12\x40':
    print("warning: unknown ROM magic " + m.hex(), file=sys.stderr)
open(sys.argv[2], 'wb').write(d)
EOF
  basename "$SRC" > "$RD/$ID.src"; mv -f "$TMP/out.z64" "$OUT"; rm -rf "$TMP"
fi
[ -f "$RD/$ID.src" ] && echo "dump=$(cat "$RD/$ID.src")"
python -I - "$(cygpath -m "$OUT")" <<'EOF'
import sys, zlib
d = open(sys.argv[1], 'rb').read()
cic = {0x6170A4A1: "6101", 0x009E9EA3: "7102", 0x90BB6CB5: "6102/7101", 0x0B050EE0: "6103/7103", 0x98BC2C86: "6105/7105", 0xACC8580A: "6106/7106"}
crc = zlib.crc32(d[0x40:0x1000]) & 0xFFFFFFFF
print(f"rom={sys.argv[1]}")
print(f"name='{d[0x20:0x34].decode('ascii', 'replace').strip()}' code={d[0x3B:0x3F].decode('ascii', 'replace')} "
      f"version={d[0x3F]} cic={cic.get(crc, 'unknown')} bootcrc={crc:08X} size={len(d)} ({len(d) >> 20} MB)")
EOF
