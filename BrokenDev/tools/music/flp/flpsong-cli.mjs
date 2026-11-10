#!/usr/bin/env node
// flpsong-cli: an FL project -> its neutral song model (flpsong.mjs), canonical JSON.
//   node BrokenDev/tools/music/flp/flpsong-cli.mjs song.flp [--out file.song.json]      (stdout without --out; never writes the .flp)
import fs from 'node:fs';
import { songFromFlp, songJson } from './flpsong.mjs';
const a = process.argv.slice(2);
const oi = a.indexOf('--out'); const out = oi >= 0 ? a.splice(oi, 2)[1] : null;
if (a.length !== 1) { console.error('usage: node flpsong-cli.mjs song.flp [--out file.song.json]'); process.exit(2); }
const text = songJson(songFromFlp(fs.readFileSync(a[0])));
if (out) fs.writeFileSync(out, text); else process.stdout.write(text);
