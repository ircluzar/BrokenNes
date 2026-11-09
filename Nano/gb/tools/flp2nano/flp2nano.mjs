#!/usr/bin/env node
// flp2nano: an FL Studio project (.flp) -> <name>.nano.mid + <name>.nano.json (the Nano GB sidecar), next to the project or in --out.
//
//   node Nano/gb/tools/flp2nano/flp2nano.mjs "song.flp" [more.flp ...] [--out DIR] [--dump]
//
// It reads the .flp itself (flp.mjs): the channel rack, every BrokenNes2 instance's saved state (chip, channel, Instrument, Volume, Pan, Duty),
// the patterns' notes (velocity, length, fine pitch, slide notes) and the playlist. See README.md (Nano/gb) "FL projects (.flp)".
// It only ever writes files ending in .nano.mid / .nano.json, and never the .flp.

import fs from 'node:fs';
import path from 'node:path';
import { parseFlp } from './flp.mjs';
import { convert } from './convert.mjs';

const args = process.argv.slice(2);
const files = [];
let outDir = null, dump = false, slide = 'fl';
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--out') outDir = args[++i];
  else if (args[i] === '--dump') dump = true;
  else if (args[i] === '--slide') slide = args[++i];
  else if (args[i] === '-h' || args[i] === '--help') { console.log('usage: node flp2nano.mjs song.flp [more.flp ...] [--out DIR] [--dump]'); process.exit(0); }
  else files.push(args[i]);
}
if (!files.length) { console.error('usage: node flp2nano.mjs song.flp [more.flp ...] [--out DIR] [--dump]'); process.exit(2); }

let failed = 0;
for (const f of files) {
  try {
    const proj = parseFlp(fs.readFileSync(f));
    const base = path.basename(f).replace(/\.flp$/i, '');
    const r = convert(proj, { title: base, slide });
    const dir = outDir ?? path.dirname(f);
    const mid = path.join(dir, base + '.nano.mid'), json = path.join(dir, base + '.nano.json');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(mid, r.midi);
    fs.writeFileSync(json, JSON.stringify(r.sidecar, null, 2) + '\n');
    console.log(`\n${f}`);
    console.log(`  FL ${proj.version}, ${proj.ppq} PPQ, ${r.tempo} BPM, ${r.songEnd} ticks = ${r.seconds.toFixed(3)} s${r.fallback ? '  (NO PLAYLIST: patterns in order)' : ''}`);
    for (const s of r.summary)
      console.log(`  MIDI ch ${String(s.midiChannel).padStart(2)}  rack ${s.rack} "${s.name}": ${s.kind}; ${s.notes} notes, ${s.slides} slide notes (${s.bentNotes} notes bent), bend range +-${s.bendRange}`);
    if (!r.summary.length) console.log('  NOTHING to convert: no BrokenNes2 Nano GB / NES channel');
    for (const w of r.warnings) console.log(`  ${/PLAYLIST IS EMPTY/.test(w) ? '!!! WARNING' : 'warning'}: ${w}`);
    console.log(`  wrote ${mid}\n  wrote ${json}`);
    if (dump) console.log(JSON.stringify({ channels: proj.channels.map(c => ({ index: c.index, type: c.type, plugin: c.pluginName, name: c.name })), patterns: [...proj.patterns.values()].map(p => ({ index: p.index, notes: p.notes.length })), playlist: proj.arrangements.map(a => a.items) }, null, 1));
  } catch (e) {
    failed++;
    console.error(`${f}: ${e.message}`);
  }
}
process.exit(failed ? 1 : 0);
