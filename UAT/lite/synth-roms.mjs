// Tiny hand-assembled Sega programs for the Lite functional suite (no game, no ROM from anywhere: bytes written here). They exercise what a real
// game cannot be made to do on demand: a save that changes every run, a video mode that changes while the game runs.
import fs from 'node:fs';
import path from 'node:path';

const ascii = (s) => Array.from(s, c => c.charCodeAt(0));

/** A Master System / Game Gear cartridge (Sega mapper, 32 KB): turns the cartridge RAM on, adds 1 to the byte at $8000 and loops. Each run of the page keeps one more than the last. */
export function smsSaveCounter() {
  const rom = new Uint8Array(0x8000);
  const code = [
    0xF3,                    // DI
    0xED, 0x56,              // IM 1
    0x31, 0xF0, 0xDF,        // LD SP,$DFF0
    0x3E, 0x08,              // LD A,$08      the RAM-mapping pattern the cartridge scan looks for: LD A,8 ; LD ($FFFC),A
    0x32, 0xFC, 0xFF,        // LD ($FFFC),A
    0x3A, 0x00, 0x80,        // LD A,($8000)
    0x3C,                    // INC A
    0x32, 0x00, 0x80,        // LD ($8000),A
    0x18, 0xFE,              // JR $
  ];
  rom.set(code, 0);
  return rom;
}

function mdRom(size, patch) {
  const rom = new Uint8Array(size);
  const L = (at, v) => { rom[at] = v >>> 24; rom[at + 1] = v >>> 16 & 255; rom[at + 2] = v >>> 8 & 255; rom[at + 3] = v & 255; };
  L(0, 0xFFFE00); L(4, 0x200);
  for (let v = 2; v < 64; v++) L(v * 4, 0x400);
  rom.set(ascii('SEGA MEGA DRIVE '), 0x100);
  rom.set(ascii('(C)TEST 2026.OCT'), 0x110);
  rom.set(ascii('LITE TEST ROM'.padEnd(48)), 0x120);
  rom.set(ascii('LITE TEST ROM'.padEnd(48)), 0x150);
  rom.set(ascii('GM 00000000-00'), 0x180);
  rom.set(ascii('JUE'.padEnd(16)), 0x1F0);
  rom[0x400] = 0x60; rom[0x401] = 0xFE;
  patch?.(rom);
  return rom;
}
const put = (rom, at, words) => words.forEach((w, i) => { rom[at + i * 2] = w >> 8; rom[at + i * 2 + 1] = w & 255; });

/** A Genesis cartridge with battery SRAM at $200001 (odd bytes): adds 1 to the byte there on every start. */
export function mdSramCounter() {
  return mdRom(0x20000, r => {
    r.set(ascii('RA'), 0x1B0); r[0x1B2] = 0xF8; r[0x1B3] = 0x20;
    r.set([0, 0x20, 0, 0x01], 0x1B4);              // start $200001
    r.set([0, 0x20, 0x3F, 0xFF], 0x1B8);           // end $203FFF
    put(r, 0x200, [
      0x1039, 0x0020, 0x0001,                      // move.b $200001,d0
      0x5200,                                      // addq.b #1,d0
      0x13C0, 0x0020, 0x0001,                      // move.b d0,$200001
      0x60FE,                                      // bra *
    ]);
  });
}

/** A Genesis picture program: the display on, `h40` picks the 320-wide mode (else 256), a backdrop colour; with `switchAfter` it waits a while (about 150 frames) and then switches to the other width and another colour. */
export function mdVideo({ h40 = true, switchAfter = false } = {}) {
  return mdRom(0x20000, r => {
    const w = [
      0x33FC, 0x8144, 0x00C0, 0x0004,              // register 1: display on, V28
      0x33FC, h40 ? 0x8C81 : 0x8C00, 0x00C0, 0x0004, // register 12: H40 or H32
      0x33FC, 0x8707, 0x00C0, 0x0004,              // register 7: backdrop colour 7
      0x23FC, 0xC00E, 0x0000, 0x00C0, 0x0004,      // CRAM write at colour 7
      0x33FC, 0x0E00, 0x00C0, 0x0000,              // blue
    ];
    if (switchAfter) w.push(
      0x203C, 0x000F, 0xFFFF,                      // move.l #$FFFFF,d0
      0x5380,                                      // subq.l #1,d0        (the wait: about 18 cycles a turn)
      0x66FC,                                      // bne  -4
      0x33FC, h40 ? 0x8C00 : 0x8C81, 0x00C0, 0x0004, // the other width
      0x23FC, 0xC00E, 0x0000, 0x00C0, 0x0004,      // CRAM write at colour 7
      0x33FC, 0x00E0, 0x00C0, 0x0000,              // green
    );
    w.push(0x60FE);
    put(r, 0x200, w);
  });
}

export function writeAll(dir) {
  fs.mkdirSync(dir, { recursive: true });
  const files = {
    'sms-save-counter.sms': smsSaveCounter(), 'gg-save-counter.gg': smsSaveCounter(), 'md-sram-counter.bin': mdSramCounter(),
    'md-h40.bin': mdVideo({ h40: true }), 'md-h32.bin': mdVideo({ h40: false }), 'md-switch.bin': mdVideo({ h40: false, switchAfter: true }),
  };
  for (const [name, bytes] of Object.entries(files)) fs.writeFileSync(path.join(dir, name), bytes);
  return Object.keys(files);
}
