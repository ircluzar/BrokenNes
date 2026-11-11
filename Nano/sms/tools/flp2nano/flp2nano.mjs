#!/usr/bin/env node
// flp2nano (Nano SMS): an FL Studio project (.flp) -> <name>.nanosms.mid + <name>.nanosms.json (the Nano SMS sidecar), next to the project or in --out.
//
//   node Nano/sms/tools/flp2nano/flp2nano.mjs "song.flp" [more.flp ...] [--out DIR] [--dump]
//
// It reads the .flp itself (flp.mjs): the channel rack, every BrokenNes2 instance's saved state (chip, channel, Instrument, Volume, Pan, Duty),
// the patterns' notes (velocity, length, fine pitch, slide notes) and the playlist. See Nano/sms/README.md "FL projects (.flp)"; the reader and the fence are Nano GB's (Nano/gb/tools/flp2nano).
// It only ever writes files ending in .nanosms.mid / .nanosms.json / .nanosms.fence.json (the fence report: fence.mjs), and never the .flp.

import fs from 'node:fs';
import path from 'node:path';
import { parseFlp } from '../../../gb/tools/flp2nano/flp.mjs';
import { convert } from './convert.mjs';
import { fenceOf, fenceText } from '../../../gb/tools/flp2nano/fence.mjs';

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
    const mid = path.join(dir, base + '.nanosms.mid'), json = path.join(dir, base + '.nanosms.json');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(mid, r.midi);
    fs.writeFileSync(json, JSON.stringify(r.sidecar, null, 2) + '\n');
    const fence = fenceOf(r, 'Nano SMS', proj), fenceFile = path.join(dir, base + '.nanosms.fence.json');
    fs.writeFileSync(fenceFile, JSON.stringify(fence) + '\n');
    console.log(`\n${f}`);
    console.log(`  FL ${proj.version}, ${proj.ppq} PPQ, ${r.tempo} BPM, ${r.songEnd} ticks = ${r.seconds.toFixed(3)} s${r.fallback ? '  (NO PLAYLIST: patterns in order)' : ''}`);
    for (const s of r.summary)
      console.log(`  MIDI ch ${String(s.midiChannel).padStart(2)}  rack ${s.rack} "${s.name}": ${s.kind}; ${s.notes} notes, ${s.slides} slide notes (${s.bentNotes} notes bent), bend range +-${s.bendRange}`);
    if (!r.summary.length) console.log('  NOTHING to convert: no BrokenNes2 Nano SMS / SMS / NES channel');
    for (const w of r.warnings) console.log(`  ${/PLAYLIST IS EMPTY/.test(w) ? '!!! WARNING' : 'warning'}: ${w}`);
    console.log('  ' + fenceText(fence).split('\n').join('\n  '));
    console.log(`  wrote ${mid}\n  wrote ${json}\n  wrote ${fenceFile}`);
    if (dump) console.log(JSON.stringify({ channels: proj.channels.map(c => ({ index: c.index, type: c.type, plugin: c.pluginName, name: c.name })), patterns: [...proj.patterns.values()].map(p => ({ index: p.index, notes: p.notes.length })), playlist: proj.arrangements.map(a => a.items) }, null, 1));
  } catch (e) {
    failed++;
    console.error(`${f}: ${e.message}`);
  }
}
process.exit(failed ? 1 : 0);
